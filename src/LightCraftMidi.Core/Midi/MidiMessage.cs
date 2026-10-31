namespace LightCraftMidi.Core.Midi;

public enum MidiMessageType
{
    ControlChange,
    Note,
    PitchBend,
}

/// <summary>A channel voice message reduced to what mapping needs. Channel is 1-16.</summary>
public readonly record struct MidiMessage(int Channel, MidiMessageType Type, int Number, int Value)
{
    public int MaxValue => MaxValueFor(Type);

    public static int MaxValueFor(MidiMessageType type) => type == MidiMessageType.PitchBend ? 16383 : 127;

    public string SourceText => FormatSource(Channel, Type, Number);

    public override string ToString() => $"{SourceText} = {Value}";

    public static string FormatSource(int channel, MidiMessageType type, int number)
    {
        var ch = channel == 0 ? "Any ch" : $"Ch {channel}";
        return type switch
        {
            MidiMessageType.ControlChange => $"{ch} CC {number}",
            MidiMessageType.Note => $"{ch} Note {number} ({NoteName(number)})",
            MidiMessageType.PitchBend => $"{ch} Pitch Bend",
            _ => $"{ch} {type} {number}",
        };
    }

    public static string NoteName(int note)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[note % 12]}{note / 12 - 1}";
    }
}

public sealed record DiscoveredMidiControl(
    int Channel,
    MidiMessageType Type,
    int Number,
    int LastValue,
    int MessageCount,
    string Kind,
    DateTime LastSeen,
    IReadOnlyList<int> ObservedValues);

/// <summary>Converts between packed winmm short messages (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16) and <see cref="MidiMessage"/>.</summary>
public static class MidiCodec
{
    public static MidiMessage? Decode(int raw)
    {
        int status = raw & 0xFF;
        int data1 = (raw >> 8) & 0x7F;
        int data2 = (raw >> 16) & 0x7F;
        int channel = (status & 0x0F) + 1;
        return (status & 0xF0) switch
        {
            0xB0 => new MidiMessage(channel, MidiMessageType.ControlChange, data1, data2),
            0x90 => new MidiMessage(channel, MidiMessageType.Note, data1, data2),
            0x80 => new MidiMessage(channel, MidiMessageType.Note, data1, 0),
            0xE0 => new MidiMessage(channel, MidiMessageType.PitchBend, 0, data1 | (data2 << 7)),
            _ => null,
        };
    }

    public static int Encode(MidiMessage m)
    {
        int ch = Math.Clamp(m.Channel <= 0 ? 1 : m.Channel, 1, 16) - 1;
        return m.Type switch
        {
            MidiMessageType.ControlChange => (0xB0 | ch) | ((m.Number & 0x7F) << 8) | ((Math.Clamp(m.Value, 0, 127) & 0x7F) << 16),
            MidiMessageType.Note => (0x90 | ch) | ((m.Number & 0x7F) << 8) | ((Math.Clamp(m.Value, 0, 127) & 0x7F) << 16),
            MidiMessageType.PitchBend => EncodePitchBend(ch, Math.Clamp(m.Value, 0, 16383)),
            _ => throw new ArgumentOutOfRangeException(nameof(m)),
        };
    }

    private static int EncodePitchBend(int ch, int value) => (0xE0 | ch) | ((value & 0x7F) << 8) | (((value >> 7) & 0x7F) << 16);
}
