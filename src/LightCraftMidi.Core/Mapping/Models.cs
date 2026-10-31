using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.Core.Mapping;

public enum MappingTargetKind
{
    /// <summary>A develop slider (absolute value from a fader/knob).</summary>
    DevelopControl,

    /// <summary>Reset a develop slider to its default when a MIDI button is pressed.</summary>
    ResetControl,

    /// <summary>An engine/UI command (triggered by a button/note).</summary>
    Command,
    LoadProfile,
}

public enum CommandTrigger
{
    /// <summary>Fire when the value is above zero (note on, button press).</summary>
    OnPress,

    /// <summary>Fire on every message (latching/toggle buttons that alternate 127/0).</summary>
    OnAnyChange,
}

public sealed class MidiMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>MIDI channel 1-16, or 0 for any channel.</summary>
    public int Channel { get; set; }
    public MidiMessageType MessageType { get; set; } = MidiMessageType.ControlChange;
    /// <summary>CC or note number; -1 while unassigned.</summary>
    public int Number { get; set; } = -1;
    public bool ToggleProfileButton { get; set; }

    public MappingTargetKind TargetKind { get; set; } = MappingTargetKind.DevelopControl;
    public string TargetId { get; set; } = "";
    public string? TargetLabel { get; set; }

    // Develop control options
    public bool Invert { get; set; }
    public double? RangeMin { get; set; }
    public double? RangeMax { get; set; }
    public bool Feedback { get; set; } = true;

    // Command options
    public CommandTrigger Trigger { get; set; } = CommandTrigger.OnPress;

    /// <summary>JSON object text passed as the command's params, e.g. <c>{"rating": 4}</c>.</summary>
    public string? CommandParams { get; set; }

    public bool HasSource => MessageType == MidiMessageType.PitchBend || Number >= 0;
    public bool HasTarget => !string.IsNullOrWhiteSpace(TargetId);

    public bool Matches(MidiMessage m) =>
        m.Type == MessageType
        && (MessageType == MidiMessageType.PitchBend || m.Number == Number)
        && (Channel == 0 || Channel == m.Channel);

    public string SourceText => MidiMessage.FormatSource(Channel, MessageType, Number);

    public MidiMapping Clone() => (MidiMapping)MemberwiseClone();
}

public sealed class MappingProfile
{
    public string Name { get; set; } = "Default";
    public string? MidiInput { get; set; }
    public string? MidiOutput { get; set; }
    public bool FeedbackEnabled { get; set; } = true;
    public List<MidiMapping> Mappings { get; set; } = [];

    public MappingProfile Clone(string? newName = null) => new()
    {
        Name = newName ?? Name,
        MidiInput = MidiInput,
        MidiOutput = MidiOutput,
        FeedbackEnabled = FeedbackEnabled,
        Mappings = Mappings.Select(m => { var c = m.Clone(); if (newName != null) c.Id = Guid.NewGuid(); return c; }).ToList(),
    };
}

public sealed class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7980;
    public string ActiveProfile { get; set; } = "Default";
    public bool MappingEnabled { get; set; } = true;
    public bool HideWindowOnStart { get; set; } = true;
    public MappingLogLevel LogLevel { get; set; } = MappingLogLevel.Debug;
    public List<MidiMapping> ProfileButtons { get; set; } = [];
    public Dictionary<string, List<DiscoveredMidiControl>> MidiControlsByDevice { get; set; } = [];
    public List<DevelopControl> CachedDevelopControls { get; set; } = [];
    public List<LightCraftCommand> CachedCommands { get; set; } = [];
}
