using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LightCraftMidi.Core.LightCraft;

/// <summary>
/// Client for LightCraft's JSON-lines control port (<c>lightcraft --control 7980</c>).
/// Keeps one connection open, reconnects automatically and sends a keep-alive so the
/// server's five-minute idle timeout does not drop it.
/// </summary>
public sealed class LightCraftClient : ILightCraftClient, IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectionCts;
    private long _nextId;
    private long _lastSendTicks;
    private LightCraftConnectionState _state;

    public LightCraftClient(string host = "127.0.0.1", int port = 7980)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; private set; }
    public int Port { get; private set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(65);
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(120);
    public string? LastError { get; private set; }

    public LightCraftConnectionState State
    {
        get { lock (_stateGate) return _state; }
        private set
        {
            lock (_stateGate)
            {
                if (_state == value) return;
                _state = value;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Changes the endpoint (if different) and reconnects immediately.</summary>
    public void Reconnect(string? host = null, int? port = null)
    {
        if (host != null) Host = host;
        if (port != null) Port = port.Value;
        _connectionCts?.Cancel();
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<JsonNode?> SendAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var stream = _stream;
        if (State != LightCraftConnectionState.Connected || stream == null)
            throw new LightCraftException("Not connected to LightCraft");

        long id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters != null) request["params"] = parameters;
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
            }
            finally
            {
                _writeLock.Release();
            }
            return await tcs.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw new LightCraftException("Connection to LightCraft lost: " + ex.Message);
        }
        catch (TimeoutException)
        {
            throw new LightCraftException($"LightCraft did not answer `{method}` in time");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connectionCts = connectionCts;
            try
            {
                State = LightCraftConnectionState.Connecting;
                var tcp = new TcpClient { NoDelay = true };
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await tcp.ConnectAsync(Host, Port, connectTimeout.Token).ConfigureAwait(false);
                }
                _tcp = tcp;
                _stream = tcp.GetStream();
                LastError = null;
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
                State = LightCraftConnectionState.Connected;

                var keepAlive = KeepAliveAsync(connectionCts.Token);
                await ReadLoopAsync(_stream, connectionCts.Token).ConfigureAwait(false);
                connectionCts.Cancel();
                await keepAlive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (State != LightCraftConnectionState.Connected)
            {
                LastError ??= $"Could not connect to {Host}:{Port} (is LightCraft running with --control {Port}?)";
            }
            catch (OperationCanceledException)
            {
                // Reconnect() requested while connected.
            }
            catch (Exception ex)
            {
                LastError = ex is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused
                    ? $"Connection refused on {Host}:{Port} — start LightCraft with --control {Port}"
                    : ex.Message;
            }
            finally
            {
                CloseConnection();
                State = LightCraftConnectionState.Disconnected;
                _connectionCts = null;
            }

            try
            {
                await _wake.WaitAsync(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
            {
                LastError ??= "LightCraft closed the connection";
                return;
            }
            if (line.Length == 0) continue;
            HandleReply(line);
        }
    }

    private void HandleReply(string line)
    {
        JsonObject? reply;
        try
        {
            reply = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (reply == null) return;

        bool ok = reply["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var b) && b;
        string error = reply["error"]?.ToString() ?? "LightCraft reported an error";

        if (reply["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var tcs))
        {
            if (ok) tcs.TrySetResult(reply["result"]?.DeepClone());
            else tcs.TrySetException(new LightCraftException(error));
        }
        else if (!ok)
        {
            // Server-initiated errors (idle timeout, connection limit) come without a matching id.
            LastError = error;
        }
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastSendTicks);
                if (idleMs < KeepAliveInterval.TotalMilliseconds) continue;
                try
                {
                    await this.ExecuteAsync("activity.list", null, ct).ConfigureAwait(false);
                }
                catch (LightCraftException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CloseConnection()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new LightCraftException("Connection to LightCraft lost"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
