using System.IO;
using System.Text;
using System.Threading.Channels;
using LightCraftMidi.Core.Mapping;

namespace LightCraftMidi.Services;

public sealed class MappingTraceFile : IAsyncDisposable
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly StreamWriter _writer;
    private readonly Task _writeTask;
    private bool _disposed;
    private int _failed;

    public MappingTraceFile(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightCraftMidi",
            "Logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"lightcraft-midi-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _writeTask = Task.Run(WriteLoopAsync);
    }

    public string FilePath { get; }
    public event EventHandler<Exception>? WriteFailed;

    public bool Write(MappingDiagnostic diagnostic) =>
        Volatile.Read(ref _failed) == 0 &&
        _lines.Writer.TryWrite($"{diagnostic.Timestamp:O} [{diagnostic.Level}] {diagnostic.Message}");

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
                await _writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _failed, 1);
            _lines.Writer.TryComplete(ex);
            WriteFailed?.Invoke(this, ex);
        }
        finally
        {
            _writer.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lines.Writer.TryComplete();
        await _writeTask.ConfigureAwait(false);
    }
}
