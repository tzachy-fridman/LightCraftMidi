using NAudio.Midi;

namespace LightCraftMidi.Core.Midi;

/// <summary>winmm MIDI via NAudio. Polls the device list to reconnect after hot-plugging.</summary>
public sealed class NAudioMidiService : IMidiService
{
    private readonly object _gate = new();
    private readonly Timer _pollTimer;
    private MidiIn? _in;
    private MidiOut? _out;
    private string[] _lastInputs = [];
    private string[] _lastOutputs = [];
    private bool _disposed;

    public NAudioMidiService(TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollTimer = new Timer(_ => Poll(), null, interval, interval);
    }

    public event EventHandler<MidiMessage>? MessageReceived;
    public event EventHandler? StateChanged;

    public string? DesiredInput { get; private set; }
    public string? DesiredOutput { get; private set; }
    public bool IsInputOpen { get { lock (_gate) return _in != null; } }
    public bool IsOutputOpen { get { lock (_gate) return _out != null; } }

    public IReadOnlyList<string> GetInputDevices()
    {
        try
        {
            return Enumerable.Range(0, MidiIn.NumberOfDevices).Select(i => MidiIn.DeviceInfo(i).ProductName).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<string> GetOutputDevices()
    {
        try
        {
            return Enumerable.Range(0, MidiOut.NumberOfDevices).Select(i => MidiOut.DeviceInfo(i).ProductName).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void SelectDevices(string? input, string? output)
    {
        lock (_gate)
        {
            if (DesiredInput != input)
            {
                CloseInput();
                DesiredInput = string.IsNullOrWhiteSpace(input) ? null : input;
            }
            if (DesiredOutput != output)
            {
                CloseOutput();
                DesiredOutput = string.IsNullOrWhiteSpace(output) ? null : output;
            }
        }
        Poll();
    }

    public void Send(MidiMessage message)
    {
        lock (_gate)
        {
            if (_out == null) return;
            try
            {
                _out.Send(MidiCodec.Encode(message));
            }
            catch
            {
                CloseOutput();
            }
        }
    }

    private void Poll()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            var inputs = GetInputDevices().ToArray();
            var outputs = GetOutputDevices().ToArray();
            if (!inputs.SequenceEqual(_lastInputs) || !outputs.SequenceEqual(_lastOutputs))
            {
                changed = true;
                _lastInputs = inputs;
                _lastOutputs = outputs;
            }

            if (_in != null && (DesiredInput == null || !inputs.Contains(DesiredInput)))
            {
                CloseInput();
                changed = true;
            }
            if (_in == null && DesiredInput != null)
            {
                int idx = Array.IndexOf(inputs, DesiredInput);
                if (idx >= 0 && TryOpenInput(idx)) changed = true;
            }

            if (_out != null && (DesiredOutput == null || !outputs.Contains(DesiredOutput)))
            {
                CloseOutput();
                changed = true;
            }
            if (_out == null && DesiredOutput != null)
            {
                int idx = Array.IndexOf(outputs, DesiredOutput);
                if (idx >= 0 && TryOpenOutput(idx)) changed = true;
            }
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryOpenInput(int index)
    {
        try
        {
            var midiIn = new MidiIn(index);
            midiIn.MessageReceived += OnMessageReceived;
            midiIn.Start();
            _in = midiIn;
            return true;
        }
        catch
        {
            // Device busy (opened by another app) or vanished; retried on the next poll.
            return false;
        }
    }

    private bool TryOpenOutput(int index)
    {
        try
        {
            _out = new MidiOut(index);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        var msg = MidiCodec.Decode(e.RawMessage);
        if (msg is { } m) MessageReceived?.Invoke(this, m);
    }

    private void CloseInput()
    {
        if (_in == null) return;
        try
        {
            _in.MessageReceived -= OnMessageReceived;
            _in.Stop();
            _in.Dispose();
        }
        catch { }
        _in = null;
    }

    private void CloseOutput()
    {
        if (_out == null) return;
        try { _out.Dispose(); } catch { }
        _out = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pollTimer.Dispose();
            CloseInput();
            CloseOutput();
        }
    }
}
