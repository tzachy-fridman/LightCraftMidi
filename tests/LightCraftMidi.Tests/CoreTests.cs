using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.Tests;

public class MidiCodecTests
{
    [Theory]
    [InlineData(MidiMessageType.ControlChange, 1, 7, 100)]
    [InlineData(MidiMessageType.ControlChange, 16, 127, 0)]
    [InlineData(MidiMessageType.Note, 10, 36, 90)]
    [InlineData(MidiMessageType.PitchBend, 3, 0, 12345)]
    public void EncodeDecode_RoundTrips(MidiMessageType type, int channel, int number, int value)
    {
        var m = new MidiMessage(channel, type, number, value);
        Assert.Equal(m, MidiCodec.Decode(MidiCodec.Encode(m)));
    }

    [Fact]
    public void Decode_NoteOff_IsNoteWithZeroValue()
    {
        int raw = 0x80 | (60 << 8) | (64 << 16);
        Assert.Equal(new MidiMessage(1, MidiMessageType.Note, 60, 0), MidiCodec.Decode(raw));
    }

    [Fact]
    public void Decode_IgnoresNonChannelVoiceMessages()
    {
        Assert.Null(MidiCodec.Decode(0xF8)); // clock
        Assert.Null(MidiCodec.Decode(0xC0 | (5 << 8))); // program change
    }
}

public class ValueScalerTests
{
    private static readonly DevelopControl Exposure = new("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", 0);
    private static readonly DevelopControl Clarity = new("effects.clarity", "Clarity", "effects", 0, 100, 0, 1, 0, "plain", 0);

    [Fact]
    public void ToTarget_MapsEndsAndSnapsCentreToZero()
    {
        var m = new MidiMapping();
        Assert.Equal(-5, ValueScaler.ToTarget(0, 127, m, Exposure));
        Assert.Equal(5, ValueScaler.ToTarget(127, 127, m, Exposure));
        Assert.Equal(0, ValueScaler.ToTarget(64, 127, m, Exposure));
        Assert.Equal(0, ValueScaler.ToTarget(8192, 16383, m, Exposure));
    }

    [Fact]
    public void ToTarget_RespectsStepInvertAndRangeOverride()
    {
        var m = new MidiMapping { Invert = true };
        Assert.Equal(100, ValueScaler.ToTarget(0, 127, m, Clarity));
        Assert.Equal(Math.Round(100 - 100.0 * 50 / 127), ValueScaler.ToTarget(50, 127, m, Clarity));

        var narrow = new MidiMapping { RangeMin = -2, RangeMax = 2 };
        Assert.Equal(2, ValueScaler.ToTarget(127, 127, narrow, Exposure));
        Assert.Equal(-2, ValueScaler.ToTarget(0, 127, narrow, Exposure));
    }

    [Theory]
    [InlineData(0, 127)]
    [InlineData(37, 127)]
    [InlineData(64, 127)]
    [InlineData(127, 127)]
    [InlineData(5000, 16383)]
    public void ToMidi_IsInverseOfToTarget(int raw, int max)
    {
        var m = new MidiMapping();
        var v = ValueScaler.ToTarget(raw, max, m, Exposure);
        // Snapping to the control's step loses resolution; allow one step's worth of raw units.
        int tolerance = (int)Math.Ceiling(Exposure.Step / (Exposure.Max - Exposure.Min) * max) + 1;
        Assert.InRange(ValueScaler.ToMidi(v, max, m, Exposure), raw - tolerance, raw + tolerance);
    }

    [Fact]
    public void ToMidi_ClampsOutOfRangeValues()
    {
        var narrow = new MidiMapping { RangeMin = -1, RangeMax = 1 };
        Assert.Equal(127, ValueScaler.ToMidi(4, 127, narrow, Exposure));
        Assert.Equal(0, ValueScaler.ToMidi(-4, 127, narrow, Exposure));
    }
}

public class ProfileStoreTests : IDisposable
{
    [Fact]
    public void GlobalButtons_PersistInSettings_NotProfiles()
    {
        var store = new ProfileStore(_dir);
        store.SaveSettings(new AppSettings { ProfileButtons = [
            new MidiMapping { MessageType = MidiMessageType.Note, Number = 36, Channel = 10,
                TargetKind = MappingTargetKind.LoadProfile, TargetId = "Color" }] });
        var button = Assert.Single(new ProfileStore(_dir).LoadSettings().ProfileButtons);
        Assert.Equal("Color", button.TargetId);
        Assert.Equal(10, button.Channel);
        Assert.Equal(36, button.Number);
        Assert.Equal(MappingTargetKind.LoadProfile, button.TargetKind);
        Assert.Empty(Assert.Single(store.LoadProfiles()).Mappings);
    }

    [Fact]
    public void DiscoveryCaches_RoundTripMidiDevicesAndLightCraftCatalog()
    {
        var store = new ProfileStore(_dir);
        store.SaveSettings(new AppSettings
        {
            MidiControlsByDevice = new Dictionary<string, List<DiscoveredMidiControl>>
            {
                ["Controller A"] =
                [
                    new(2, MidiMessageType.ControlChange, 14, 81, 6, "Control Change (CC)", DateTime.Now, [64, 81]),
                ],
            },
            CachedDevelopControls = [new("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", null)],
            CachedCommands = [new("edit.undo", "Undo", ["Edit"], "Ctrl+Z", null, true, false, null)],
        });

        var cached = new ProfileStore(_dir).LoadSettings();
        var midiControl = Assert.Single(cached.MidiControlsByDevice["Controller A"]);
        Assert.Equal(14, midiControl.Number);
        Assert.Equal([64, 81], midiControl.ObservedValues);
        Assert.Null(Assert.Single(cached.CachedDevelopControls).Value);
        Assert.Equal("edit.undo", Assert.Single(cached.CachedCommands).Id);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lcmidi-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadProfiles_CreatesDefaultProfile()
    {
        var store = new ProfileStore(_dir);
        var profiles = store.LoadProfiles();
        Assert.Single(profiles);
        Assert.Equal("Default", profiles[0].Name);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMappingsAndSettings()
    {
        var store = new ProfileStore(_dir);
        var profile = new MappingProfile
        {
            Name = "X-Touch / Mini",
            MidiInput = "X-TOUCH MINI",
            Mappings =
            [
                new MidiMapping { Channel = 11, MessageType = MidiMessageType.ControlChange, Number = 1, TargetId = "light.exposure", RangeMin = -2, Invert = true },
                new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 10, TargetKind = MappingTargetKind.LoadProfile, TargetId = "Editing", HandleAsToggle = true },
                new MidiMapping { MessageType = MidiMessageType.Note, Number = 8, TargetKind = MappingTargetKind.Command, TargetId = "photo.rate", CommandParams = "{\"rating\":4}" },
            ],
        };
        store.SaveProfile(profile);
        store.SaveSettings(new AppSettings { Port = 7999, ActiveProfile = profile.Name, LogLevel = MappingLogLevel.Warning });

        var reloaded = new ProfileStore(_dir);
        var p = Assert.Single(reloaded.LoadProfiles());
        Assert.Equal(profile.Name, p.Name);
        Assert.Equal("X-TOUCH MINI", p.MidiInput);
        Assert.Equal(3, p.Mappings.Count);
        Assert.Equal(-2, p.Mappings[0].RangeMin);
        Assert.True(p.Mappings[0].Invert);
        Assert.Equal(MappingTargetKind.LoadProfile, p.Mappings[1].TargetKind);
        Assert.True(p.Mappings[1].HandleAsToggle);
        Assert.Equal(MappingTargetKind.Command, p.Mappings[2].TargetKind);
        Assert.Equal("{\"rating\":4}", p.Mappings[2].CommandParams);
        Assert.Equal(7999, reloaded.LoadSettings().Port);
        Assert.Equal(MappingLogLevel.Warning, reloaded.LoadSettings().LogLevel);
    }

    [Fact]
    public void LoadProfile_LegacyToggleProfileButtonSettingAsHandleAsToggle()
    {
        var store = new ProfileStore(_dir);
        File.WriteAllText(Path.Combine(store.ProfilesDirectory, "Legacy.json"),
            """{"name":"Legacy","mappings":[{"toggleProfileButton":true}]}""");

        var profile = Assert.Single(store.LoadProfiles());
        Assert.True(profile.Mappings[0].HandleAsToggle);

        store.SaveProfile(profile);
        var savedJson = File.ReadAllText(Path.Combine(store.ProfilesDirectory, "Legacy.json"));
        Assert.Contains("\"handleAsToggle\": true", savedJson);
        Assert.DoesNotContain("\"toggleProfileButton\"", savedJson);
    }

    [Fact]
    public void RenameProfile_RemovesOldFile()
    {
        var store = new ProfileStore(_dir);
        var p = new MappingProfile { Name = "One" };
        store.SaveProfile(p);
        store.RenameProfile(p, "Two");
        Assert.Equal(["Two"], store.LoadProfiles().Select(x => x.Name));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
