namespace LightCraftMidi.Core.Midi;

public interface IMidiService : IDisposable
{
    IReadOnlyList<string> GetInputDevices();
    IReadOnlyList<string> GetOutputDevices();

    string? DesiredInput { get; }
    string? DesiredOutput { get; }
    bool IsInputOpen { get; }
    bool IsOutputOpen { get; }

    /// <summary>Selects devices by name. They are opened now if present, otherwise as soon as they appear.</summary>
    void SelectDevices(string? input, string? output);

    void Send(MidiMessage message);

    /// <summary>Raised on the driver callback thread; handlers must not block.</summary>
    event EventHandler<MidiMessage>? MessageReceived;

    /// <summary>Raised when a device opens/closes or the device list changes.</summary>
    event EventHandler? StateChanged;
}
