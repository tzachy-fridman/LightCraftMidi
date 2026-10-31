using CommunityToolkit.Mvvm.ComponentModel;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.ViewModels;

/// <summary>
/// A physical control discovered by watching incoming messages. MIDI has no standard way to ask a
/// controller for its layout, so the kind is inferred from the values it sends.
/// </summary>
public sealed partial class DetectedControlViewModel : ObservableObject
{
    private readonly HashSet<int> _values = [];

    public DetectedControlViewModel(MidiMessage first)
    {
        Channel = first.Channel;
        Type = first.Type;
        Number = first.Type == MidiMessageType.PitchBend ? 0 : first.Number;
        Observe(first);
    }

    public DetectedControlViewModel(DiscoveredMidiControl saved)
    {
        Channel = saved.Channel;
        Type = saved.Type;
        Number = saved.Number;
        LastValue = saved.LastValue;
        MessageCount = saved.MessageCount;
        Kind = saved.Kind;
        LastSeen = saved.LastSeen;
        _values.UnionWith(saved.ObservedValues.Take(256));
    }

    public int Channel { get; }
    public MidiMessageType Type { get; }
    public int Number { get; }
    public string SourceText => MidiMessage.FormatSource(Channel, Type, Number);

    [ObservableProperty] private int _lastValue;
    [ObservableProperty] private int _messageCount;
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string? _mappedTo;
    [ObservableProperty] private DateTime _lastSeen;

    public bool IsContinuous => Type == MidiMessageType.PitchBend
        || Type == MidiMessageType.ControlChange && Kind != "Button (CC)" && !Kind.StartsWith("Endless encoder", StringComparison.Ordinal);

    public bool Matches(MidiMessage m) =>
        m.Channel == Channel && m.Type == Type && (Type == MidiMessageType.PitchBend || m.Number == Number);

    public DiscoveredMidiControl ToSnapshot() =>
        new(Channel, Type, Number, LastValue, MessageCount, Kind, LastSeen, _values.ToArray());

    public void Observe(MidiMessage m)
    {
        LastValue = m.Value;
        MessageCount++;
        LastSeen = DateTime.Now;
        if (_values.Count < 256) _values.Add(m.Value);
        Kind = Classify();
        OnPropertyChanged(nameof(IsContinuous));
    }

    private string Classify()
    {
        if (Type == MidiMessageType.PitchBend) return "Fader / wheel (14-bit)";
        if (Type == MidiMessageType.Note) return "Button / pad (note)";
        if (_values.Count == 2 && _values.Contains(0) && _values.Contains(127)) return "Button (CC)";
        // Relative encoders send small offsets around 0/64 (e.g. 1/127 or 63/65) instead of positions.
        if (MessageCount >= 6 && _values.Count <= 4 && _values.All(v => v is <= 3 or >= 125 or (>= 61 and <= 67)))
            return "Endless encoder (relative — not supported, set it to absolute)";
        return "Control Change (CC)";
    }
}
