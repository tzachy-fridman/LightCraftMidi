using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.Core.Mapping;

public sealed record MidiObservation(MidiMessage Message, IReadOnlyList<MidiMapping> Matched, bool Learned);

/// <summary>
/// Routes MIDI messages to LightCraft. Fader values are coalesced into one <c>develop.set</c> per
/// round-trip and grouped into a single undo step with <c>develop.beginInteraction</c>/<c>endInteraction</c>.
/// When feedback is on, the current develop values are polled and echoed to the MIDI output.
/// </summary>
public sealed class MappingEngine : IDisposable
{
    private readonly IMidiService _midi;
    private readonly ILightCraftClient _lc;
    private readonly ConcurrentDictionary<string, DevelopControl> _controls = new();
    private readonly ConcurrentDictionary<string, long> _lastTouch = new();
    private readonly ConcurrentDictionary<Guid, int> _lastFeedback = new();
    private readonly Dictionary<string, double> _pendingValues = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile MidiMapping[] _mappings = [];
    private volatile MidiMapping[] _profileButtons = [];
    private Task? _senderTask;
    private Task? _feedbackTask;
    private bool _inInteraction;
    private long _lastSetTicks;

    public MappingEngine(IMidiService midi, ILightCraftClient lightCraft)
    {
        _midi = midi;
        _lc = lightCraft;
        _midi.MessageReceived += (_, m) => HandleMessage(m);
        _lc.StateChanged += OnLightCraftStateChanged;
    }

    public bool Enabled { get; set; } = true;
    public bool FeedbackEnabled { get; set; } = true;
    public bool LearnMode { get; set; }
    public TimeSpan InteractionIdle { get; set; } = TimeSpan.FromMilliseconds(700);
    public TimeSpan FeedbackInterval { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan TouchHold { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>Every incoming message (for the monitor), with the mappings it matched.</summary>
    public event EventHandler<MidiObservation>? MessageObserved;

    /// <summary>The message captured while <see cref="LearnMode"/> was on.</summary>
    public event EventHandler<MidiMessage>? Learned;

    public event EventHandler<string>? Log;
    public event EventHandler<string>? ProfileRequested;

    public void SetProfileButtons(IEnumerable<MidiMapping> buttons)
    {
        _profileButtons = buttons.Select(m => m.Clone()).ToArray();
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
        lock (_pendingValues) _pendingValues.Clear();
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
        if (LearnMode)
        {
            // A note-off after the note-on would otherwise be learned as the second message.
            if (m.Type == MidiMessageType.Note && m.Value == 0) return;
            LearnMode = false;
            Learned?.Invoke(this, m);
            MessageObserved?.Invoke(this, new MidiObservation(m, [], true));
            return;
        }

        var buttons = _profileButtons.Where(x => x.HasSource && x.HasTarget && x.Channel is >= 0 and <= 16
            && x.Number is >= 0 and <= 127 && x.MessageType != MidiMessageType.PitchBend && x.Matches(m)).ToArray();
        if (buttons.Length > 0)
        {
            MessageObserved?.Invoke(this, new MidiObservation(m, buttons, false));
            var button = buttons[0];
            if (button.Trigger == CommandTrigger.OnAnyChange || m.Value > 0)
                ProfileRequested?.Invoke(this, button.TargetId);
            return;
        }

        var matched = _mappings.Where(x => x.HasTarget && x.Matches(m)).ToArray();
        MessageObserved?.Invoke(this, new MidiObservation(m, matched, false));
        if (!Enabled) return;

        foreach (var mapping in matched)
        {
            switch (mapping.TargetKind)
            {
                case MappingTargetKind.DevelopControl:
                    if (!_controls.TryGetValue(mapping.TargetId, out var control)) continue;
                    var value = ValueScaler.ToTarget(m.Value, m.MaxValue, mapping, control);
                    _lastFeedback[mapping.Id] = m.Value;
                    QueueValue(mapping.TargetId, value);
                    break;

                case MappingTargetKind.Command:
                    if (mapping.Trigger == CommandTrigger.OnPress && m.Value == 0) continue;
                    _ = ExecuteCommandAsync(mapping);
                    break;
            }
        }
    }

    private void QueueValue(string controlId, double value)
    {
        lock (_pendingValues) _pendingValues[controlId] = value;
        _lastTouch[controlId] = Environment.TickCount64;
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

            Dictionary<string, double>? batch = null;
            lock (_pendingValues)
            {
                if (_pendingValues.Count > 0)
                {
                    batch = new Dictionary<string, double>(_pendingValues);
                    _pendingValues.Clear();
                }
            }

            try
            {
                if (batch != null)
                {
                    if (_lc.State != LightCraftConnectionState.Connected) continue;
                    if (!_inInteraction)
                    {
                        try
                        {
                            await _lc.ExecuteAsync("develop.beginInteraction", new JsonObject { ["label"] = "MIDI" }, ct).ConfigureAwait(false);
                            _inInteraction = true;
                        }
                        catch (LightCraftException) { }
                    }
                    var values = new JsonObject();
                    foreach (var (id, v) in batch) values[id] = v;
                    await _lc.ExecuteAsync("develop.set", new JsonObject { ["values"] = values }, ct).ConfigureAwait(false);
                    _lastSetTicks = Environment.TickCount64;
                }
                else if (!signalled && _inInteraction && Environment.TickCount64 - _lastSetTicks > InteractionIdle.TotalMilliseconds)
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
            && !buttons.Any(b => b.HasSource && b.HasTarget && SourcesOverlap(b, m))).ToArray();
        if (feedbackMappings.Length == 0) return;

        var controls = await _lc.GetControlsAsync(ct).ConfigureAwait(false);
        UpdateControls(controls);
        var now = Environment.TickCount64;

        foreach (var mapping in feedbackMappings)
        {
            if (_lastTouch.TryGetValue(mapping.TargetId, out var touched) && now - touched < TouchHold.TotalMilliseconds) continue;
            if (!_controls.TryGetValue(mapping.TargetId, out var c) || c.Value is not { } value) continue;
            int maxRaw = MidiMessage.MaxValueFor(mapping.MessageType);
            int raw = ValueScaler.ToMidi(value, maxRaw, mapping, c);
            if (_lastFeedback.TryGetValue(mapping.Id, out var last) && last == raw) continue;
            _lastFeedback[mapping.Id] = raw;
            _midi.Send(new MidiMessage(mapping.Channel == 0 ? 1 : mapping.Channel, mapping.MessageType, Math.Max(0, mapping.Number), raw));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lc.StateChanged -= OnLightCraftStateChanged;
    }
}
