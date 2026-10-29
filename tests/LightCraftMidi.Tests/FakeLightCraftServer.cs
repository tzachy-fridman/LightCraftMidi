using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LightCraftMidi.Tests;

/// <summary>A minimal stand-in for LightCraft's JSON-lines control server.</summary>
public sealed class FakeLightCraftServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;

    public FakeLightCraftServer()
    {
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<JsonObject> Requests { get; } = new();

    /// <summary>Returns the reply object (without id) for a request; default replies ok with null.</summary>
    public Func<JsonObject, JsonObject> Handler { get; set; } = _ => new JsonObject { ["ok"] = true, ["result"] = null };

    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_clients) _clients.Add(client);
                _ = ServeAsync(client);
            }
        }
        catch (Exception) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            var stream = client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) return;
                var request = (JsonObject)JsonNode.Parse(line)!;
                Requests.Enqueue(request);
                var reply = Handler(request);
                reply["id"] = request["id"]?.DeepClone();
                var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
                await stream.WriteAsync(bytes, _cts.Token);
            }
        }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropClients();
        try { await _acceptLoop; } catch { }
    }
}
