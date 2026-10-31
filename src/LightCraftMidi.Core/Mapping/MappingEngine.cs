using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.Core.Mapping;

public sealed record MidiObservation(MidiMessage Message, IReadOnlyList<MidiMapping> Matched, bool Learned, DateTime Timestamp);

public enum MappingLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record MappingDiagnostic(DateTime Timestamp, MappingLogLevel Level, string Message);

/// <summary>
/// Routes MIDI messages to LightCraft. Every mapped fader value is sent in order and grouped into
/// a single undo step with <c>develop.beginInteraction</c>/<c>develop.endInteraction</c>.
/// When feedback is on, the current develop values are polled and echoed to the MIDI output.
/// </summary>
public sealed class MappingEngine : IDisposable
{
    private sealed record PendingValue(long Sequence, string ControlId, double Value, int RawValue, string Source, long EnqueuedAt, bool IsReset);

    private readonly IMidiService _midi;
    private readonly ILightCraftClient _lc;
    private readonly ConcurrentDictionary<string, DevelopControl> _controls = new();
    private readonly ConcurrentDictionary<string, long> _lastTouch = new();
    private readonly ConcurrentDictionary<Guid, int> _lastFeedback = new();
    private readonly ConcurrentDictionary<Guid, bool> _resetButtonPressed = new();
    private readonly ConcurrentDictionary<Guid, bool> _toggleProfileButtonPressed = new();
    private readonly ConcurrentQueue<PendingValue> _pendingValues = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile MidiMapping[] _mappings = [];
    private volatile MidiMapping[] _profileButtons = [];
    private Task? _senderTask;
    private Task? _feedbackTask;
    private bool _inInteraction;
    private long _lastSetTicks;
    private long _nextUpdateSequence;

    public MappingEngine(IMidiService midi, ILightCraftClient lightCraft)
    {
        _midi = midi;
        _lc = lightCraft;
        _midi.MessageReceived += (_, m) => HandleMessage(m);
        _lc.StateChanged += OnLightCraftStateChanged;
    }

    public bool Enabled { get; set; } = true;
    public bool FeedbackEnabled { get; set; } = true;
    public MappingLogLevel MinimumLogLevel { get; set; } = MappingLogLevel.Debug;
    public bool LearnMode { get; set; }
    public TimeSpan InteractionIdle { get; set; } = TimeSpan.FromMilliseconds(700);
    public TimeSpan FeedbackInterval { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan TouchHold { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Every incoming message with the mappings it matched.</summary>
    public event EventHandler<MidiObservation>? MessageObserved;

    /// <summary>The message captured while <see cref="LearnMode"/> was on.</summary>
    public event EventHandler<MidiMessage>? Learned;

    public event EventHandler<string>? Log;
    public event EventHandler<MappingDiagnostic>? Diagnostic;
    public event EventHandler<string>? ProfileRequested;

    public void SetProfileButtons(IEnumerable<MidiMapping> buttons)
    {
        _profileButtons = buttons.Select(m => m.Clone()).ToArray();
        _toggleProfileButtonPressed.Clear();
        ResetFeedback();
    }

    private static bool SourcesOverlap(MidiMapping a, MidiMapping b) =>
        a.MessageType == b.MessageType &&
        (a.MessageType == MidiMessageType.PitchBend || a.Number == b.Number) &&
        (a.Channel == 0 || b.Channel == 0 || a.Channel == b.Channel);

    public IReadOnlyCollection<DevelopControl> Controls => _controls.Values.ToArray();

    public void Start()
    {
        _senderTask ??= Task.Run(() => SenderLoopAsync(_cts.Token));
        _feedbackTask ??= Task.Run(() => FeedbackLoopAsync(_cts.Token));
    }

    public void SetMappings(IEnumerable<MidiMapping> mappings)
    {
        _mappings = mappings.Select(m => m.Clone()).ToArray();
        _pendingValues.Clear();
        _resetButtonPressed.Clear();
        ResetFeedback();
    }

    public void ResetFeedback() => _lastFeedback.Clear();

    public void UpdateControls(IEnumerable<DevelopControl> controls)
    {
        foreach (var c in controls) _controls[c.Id] = c;
    }

    public async Task RefreshControlsAsync(CancellationToken ct = default)
    {
        var controls = await _lc.GetControlsAsync(ct).ConfigureAwait(false);
        UpdateControls(controls);
    }

    public async Task TestMappingAsync(MidiMapping mapping, int? rawValue = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (_lc.State != LightCraftConnectionState.Connected)
            throw new LightCraftException("LightCraft is not connected.");
        if (!mapping.HasTarget)
            throw new LightCraftException("Assign a target before testing this mapping.");

        if (mapping.TargetKind == MappingTargetKind.Command)
        {
            JsonNode? parameters = null;
            if (!string.IsNullOrWhiteSpace(mapping.CommandParams))
            {
                parameters = JsonNode.Parse(mapping.CommandParams);
                if (parameters is not JsonObject)
                    throw new LightCraftException("Command params must be a JSON object.");
            }
            if (_inInteraction) await EndInteractionAsync(ct).ConfigureAwait(false);
            await _lc.ExecuteAsync(mapping.TargetId, parameters, ct).ConfigureAwait(false);
            ResetFeedback();
            return;
        }

        bool resetToDefault = mapping.TargetKind == MappingTargetKind.ResetControl;
        if (mapping.TargetKind is not (MappingTargetKind.DevelopControl or MappingTargetKind.ResetControl))
            throw new LightCraftException("Only command and develop-control mappings can be tested.");
        if (!resetToDefault && (!mapping.HasSource || rawValue is null))
            throw new LightCraftException("Move the mapped MIDI control once before testing it.");
        if (!_controls.TryGetValue(mapping.TargetId, out var control))
            throw new LightCraftException("LightCraft has not discovered this develop control yet.");

        double value;
        if (resetToDefault)
        {
            value = control.Default;
        }
        else
        {
            int maxRaw = MidiMessage.MaxValueFor(mapping.MessageType);
            value = ValueScaler.ToTarget(Math.Clamp(rawValue!.Value, 0, maxRaw), maxRaw, mapping, control);
        }
        if (_inInteraction) await EndInteractionAsync(ct).ConfigureAwait(false);
        await _lc.ExecuteAsync("develop.beginInteraction", new JsonObject { ["label"] = resetToDefault ? "MIDI reset" : "MIDI test" }, ct).ConfigureAwait(false);
        try
        {
            var values = new JsonObject { [mapping.TargetId] = value };
            await _lc.ExecuteAsync("develop.set", new JsonObject { ["values"] = values }, ct).ConfigureAwait(false);
            if (resetToDefault) SendFeedbackForControl(mapping.TargetId, value);
        }
        finally
        {
            await _lc.ExecuteAsync("develop.endInteraction", new JsonObject(), ct).ConfigureAwait(false);
        }
        if (!resetToDefault) ResetFeedback();
    }

    private void OnLightCraftStateChanged(object? sender, EventArgs e)
    {
        if (_lc.State != LightCraftConnectionState.Connected) return;
        _inInteraction = false;
        ResetFeedback();
        _ = Task.Run(async () =>
        {
            try { await RefreshControlsAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log?.Invoke(this, "Could not read develop controls: " + ex.Message); }
        });
    }

    public void HandleMessage(MidiMessage m)
    {
        Trace(MappingLogLevel.Trace, $"MIDI IN {m}");

        if (LearnMode)
        {
            // A note-off after the note-on would otherwise be learned as the second message.
            if (m.Type == MidiMessageType.Note && m.Value == 0) return;
            LearnMode = false;
            Learned?.Invoke(this, m);
            MessageObserved?.Invoke(this, new MidiObservation(m, [], true, DateTime.Now));
            return;
        }

        var buttons = _profileButtons.Where(x => x.HasSource && x.HasTarget && x.Channel is >= 0 and <= 16
            && x.Number is >= 0 and <= 127 && x.MessageType != MidiMessageType.PitchBend && x.Matches(m)).ToArray();
        if (buttons.Length > 0)
        {
            MessageObserved?.Invoke(this, new MidiObservation(m, buttons, false, DateTime.Now));
            var button = buttons[0];
            if (button.ToggleProfileButton)
            {
                bool pressed = m.Value > 0;
                bool wasPressed = _toggleProfileButtonPressed.TryGetValue(button.Id, out var previous) && previous;
                _toggleProfileButtonPressed[button.Id] = pressed;
                if (pressed && !wasPressed)
                {
                    foreach (var other in _profileButtons.Where(x => x.ToggleProfileButton && x.Id != button.Id))
                    {
                        _toggleProfileButtonPressed[other.Id] = false;
                        if (other.HasSource && other.MessageType != MidiMessageType.PitchBend)
                            _midi.Send(new MidiMessage(other.Channel <= 0 ? m.Channel : other.Channel, other.MessageType, other.Number, 0));
                    }
                    ProfileRequested?.Invoke(this, button.TargetId);
                }
            }
            else if (button.Trigger == CommandTrigger.OnAnyChange || m.Value > 0)
            {
                ProfileRequested?.Invoke(this, button.TargetId);
            }
            return;
        }

        var matched = _mappings.Where(x => x.HasTarget && x.Matches(m)).ToArray();
        MessageObserved?.Invoke(this, new MidiObservation(m, matched, false, DateTime.Now));
        var resetPressed = new HashSet<Guid>();
        foreach (var mapping in matched.Where(x => x.TargetKind == MappingTargetKind.ResetControl))
        {
            bool isPressed = m.Value > 0;
            bool wasPressed = _resetButtonPressed.TryGetValue(mapping.Id, out var previous) && previous;
            _resetButtonPressed[mapping.Id] = isPressed;
            if (isPressed && !wasPressed) resetPressed.Add(mapping.Id);
        }
        if (!Enabled) return;

        foreach (var mapping in matched)
        {
            switch (mapping.TargetKind)
            {
                case MappingTargetKind.DevelopControl:
                    if (!_controls.TryGetValue(mapping.TargetId, out var control)) continue;
                    var value = ValueScaler.ToTarget(m.Value, m.MaxValue, mapping, control);
                    _lastFeedback[mapping.Id] = m.Value;
                    QueueValue(mapping.TargetId, value, m);
                    break;

                case MappingTargetKind.ResetControl:
                    if (!resetPressed.Contains(mapping.Id)) continue;
                    if (!_controls.TryGetValue(mapping.TargetId, out var resetControl)) continue;
                    QueueValue(mapping.TargetId, resetControl.Default, m, isReset: true);
                    break;

                case MappingTargetKind.Command:
                    if (mapping.Trigger == CommandTrigger.OnPress && m.Value == 0) continue;
                    _ = ExecuteCommandAsync(mapping);
                    break;
            }
        }
    }

    private void QueueValue(string controlId, double value, MidiMessage message, bool isReset = false)
    {
        long sequence = Interlocked.Increment(ref _nextUpdateSequence);
        _pendingValues.Enqueue(new PendingValue(sequence, controlId, value, message.Value, message.SourceText, Environment.TickCount64, isReset));
        _lastTouch[controlId] = Environment.TickCount64;
        Trace(MappingLogLevel.Debug, $"QUEUED #{sequence} MIDI {message.SourceText} raw={message.Value}/{message.MaxValue} -> {controlId}={Format(value)}{(isReset ? " (reset)" : "")}; pending={_pendingValues.Count}");
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task ExecuteCommandAsync(MidiMapping mapping)
    {
        if (_lc.State != LightCraftConnectionState.Connected) return;
        try
        {
            if (_inInteraction) await EndInteractionAsync(_cts.Token).ConfigureAwait(false);
            JsonNode? parameters = null;
            if (!string.IsNullOrWhiteSpace(mapping.CommandParams))
                parameters = JsonNode.Parse(mapping.CommandParams);
            await _lc.ExecuteAsync(mapping.TargetId, parameters, _cts.Token).ConfigureAwait(false);
            ResetFeedback();
        }
        catch (JsonException ex)
        {
            Log?.Invoke(this, $"{mapping.TargetId}: invalid params JSON ({ex.Message})");
        }
        catch (Exception ex) when (ex is LightCraftException or OperationCanceledException)
        {
            Log?.Invoke(this, $"{mapping.TargetId}: {ex.Message}");
        }
    }

    private async Task SenderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool signalled;
            try
            {
                signalled = await _signal.WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                while (_pendingValues.TryDequeue(out var update))
                {
                    if (_lc.State != LightCraftConnectionState.Connected)
                    {
                        Trace(MappingLogLevel.Warning, $"Dropped {update.Source} raw={update.RawValue}: LightCraft disconnected");
                        continue;
                    }
                    if (!_inInteraction)
                    {
                        try
                        {
                            await _lc.ExecuteAsync("develop.beginInteraction", new JsonObject { ["label"] = "MIDI" }, ct).ConfigureAwait(false);
                            _inInteraction = true;
                        }
                        catch (LightCraftException ex) { Log?.Invoke(this, ex.Message); }
                    }
                    var values = new JsonObject();
                    values[update.ControlId] = update.Value;
                    var sentAt = Environment.TickCount64;
                    Trace(MappingLogLevel.Debug, $"SEND #{update.Sequence} develop.set {update.ControlId}={Format(update.Value)} from {update.Source} raw={update.RawValue}; queued={sentAt - update.EnqueuedAt}ms, pending={_pendingValues.Count}");
                    try
                    {
                        await _lc.ExecuteAsync("develop.set", new JsonObject { ["values"] = values }, ct).ConfigureAwait(false);
                        _lastSetTicks = Environment.TickCount64;
                        Trace(MappingLogLevel.Debug, $"ACK #{update.Sequence} {update.ControlId}={Format(update.Value)}; request={Environment.TickCount64 - sentAt}ms, total={Environment.TickCount64 - update.EnqueuedAt}ms, pending={_pendingValues.Count}");
                        if (update.IsReset) SendFeedbackForControl(update.ControlId, update.Value);
                    }
                    catch (LightCraftException ex)
                    {
                        Log?.Invoke(this, ex.Message);
                        Trace(MappingLogLevel.Error, $"ERROR #{update.Sequence} {update.ControlId} from {update.Source} raw={update.RawValue}: {ex.Message}");
                    }
                }
                if (!signalled && _inInteraction && Environment.TickCount64 - _lastSetTicks > InteractionIdle.TotalMilliseconds)
                {
                    await EndInteractionAsync(ct).ConfigureAwait(false);
                }
            }
            catch (LightCraftException ex)
            {
                Log?.Invoke(this, ex.Message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EndInteractionAsync(CancellationToken ct)
    {
        _inInteraction = false;
        try
        {
            await _lc.ExecuteAsync("develop.endInteraction", new JsonObject(), ct).ConfigureAwait(false);
        }
        catch (LightCraftException) { }
    }

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FeedbackInterval, ct).ConfigureAwait(false);
                await PushFeedbackAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (LightCraftException) { }
        }
    }

    /// <summary>Reads current develop values and sends changed ones to the MIDI output.</summary>
    public async Task PushFeedbackAsync(CancellationToken ct = default)
    {
        if (!FeedbackEnabled || !_midi.IsOutputOpen || _lc.State != LightCraftConnectionState.Connected) return;
        var buttons = _profileButtons;
        var feedbackMappings = _mappings.Where(m => m.Feedback && m.HasTarget && m.TargetKind == MappingTargetKind.DevelopControl && m.HasSource
            && !buttons.Any(b => b.HasSource && b.HasTarget && SourcesOverlap(b, m))
            && !_mappings.Any(b => b.TargetKind == MappingTargetKind.ResetControl &&
                                   b.HasSource && b.HasTarget && SourcesOverlap(b, m))).ToArray();
        if (feedbackMappings.Length == 0) return;

        var now = Environment.TickCount64;
        if (feedbackMappings.Any(mapping =>
                _lastTouch.TryGetValue(mapping.TargetId, out var touched) &&
                now - touched < TouchHold.TotalMilliseconds))
        {
            Trace(MappingLogLevel.Trace, "Feedback poll skipped: mapped control is being moved");
            return;
        }

        var controls = await _lc.GetControlsAsync(ct).ConfigureAwait(false);
        UpdateControls(controls);

        foreach (var mapping in feedbackMappings)
        {
            if (!_controls.TryGetValue(mapping.TargetId, out var c) || c.Value is not { } value) continue;
            int maxRaw = MidiMessage.MaxValueFor(mapping.MessageType);
            int raw = ValueScaler.ToMidi(value, maxRaw, mapping, c);
            bool changed = !_lastFeedback.TryGetValue(mapping.Id, out var last) || last != raw;
            _lastFeedback[mapping.Id] = raw;
            if (!changed)
            {
                Trace(MappingLogLevel.Trace, $"Feedback {mapping.TargetId}={Format(value)} -> MIDI unchanged ({raw}/{maxRaw})");
                continue;
            }
            var message = new MidiMessage(mapping.Channel == 0 ? 1 : mapping.Channel, mapping.MessageType, Math.Max(0, mapping.Number), raw);
            _midi.Send(message);
            Trace(MappingLogLevel.Debug, $"Feedback {mapping.TargetId}={Format(value)} -> {message}");
        }
    }

    private void SendFeedbackForControl(string controlId, double value)
    {
        if (!FeedbackEnabled || !_midi.IsOutputOpen || !_controls.TryGetValue(controlId, out var control)) return;
        var mappings = _mappings.Where(mapping =>
            mapping.Feedback &&
            mapping.HasSource &&
            mapping.TargetKind == MappingTargetKind.DevelopControl &&
            mapping.TargetId == controlId &&
            !_profileButtons.Any(button => button.HasSource && button.HasTarget && SourcesOverlap(button, mapping)) &&
            !_mappings.Any(button => button.TargetKind == MappingTargetKind.ResetControl &&
                                     button.HasSource && button.HasTarget && SourcesOverlap(button, mapping)));

        foreach (var mapping in mappings)
        {
            int maxRaw = MidiMessage.MaxValueFor(mapping.MessageType);
            int raw = ValueScaler.ToMidi(value, maxRaw, mapping, control);
            _lastFeedback[mapping.Id] = raw;
            var message = new MidiMessage(mapping.Channel == 0 ? 1 : mapping.Channel, mapping.MessageType, Math.Max(0, mapping.Number), raw);
            _midi.Send(message);
            Trace(MappingLogLevel.Debug, $"Feedback after reset ACK {controlId}={Format(value)} -> {message}");
        }
    }

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public void WriteDiagnostic(MappingLogLevel level, string message) => Trace(level, message);

    private void Trace(MappingLogLevel level, string message)
    {
        if (level < MinimumLogLevel) return;
        Diagnostic?.Invoke(this, new MappingDiagnostic(DateTime.Now, level, message));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lc.StateChanged -= OnLightCraftStateChanged;
    }
}
