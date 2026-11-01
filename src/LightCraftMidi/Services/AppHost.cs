using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;

namespace LightCraftMidi.Services;

/// <summary>Owns the long-lived services that run while the app sits in the tray.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppHost(ProfileStore? store = null)
    {
        Store = store ?? new ProfileStore();
        Settings = Store.LoadSettings();
        Profiles = Store.LoadProfiles().ToList();
        GlobalProfile = Store.LoadGlobalProfile(Settings);
        foreach (var profile in Profiles)
        {
            var switches = profile.Mappings.Where(m => m.TargetKind == MappingTargetKind.LoadProfile).ToArray();
            if (switches.Length == 0) continue;
            foreach (var mapping in switches)
            {
                if (!GlobalProfile.Mappings.Any(m => m.Id == mapping.Id)) GlobalProfile.Mappings.Add(mapping);
                profile.Mappings.Remove(mapping);
            }
            Store.SaveProfile(GlobalProfile);
            Store.SaveProfile(profile);
        }
        ActiveProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, Settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) ?? Profiles[0];

        Midi = new NAudioMidiService();
        Client = new LightCraftClient(Settings.Host, Settings.Port);
        Engine = new MappingEngine(Midi, Client) { Enabled = Settings.MappingEnabled };
        ApplyProfile(ActiveProfile);
    }

    public ProfileStore Store { get; }
    public AppSettings Settings { get; }
    public List<MappingProfile> Profiles { get; }
    public MappingProfile ActiveProfile { get; private set; }
    public MappingProfile GlobalProfile { get; }
    public NAudioMidiService Midi { get; }
    public LightCraftClient Client { get; }
    public MappingEngine Engine { get; }

    public void Start()
    {
        Client.Start();
        Engine.Start();
    }

    public void ApplyProfile(MappingProfile profile)
    {
        ActiveProfile = profile;
        Settings.ActiveProfile = profile.Name;
        Engine.FeedbackEnabled = profile.FeedbackEnabled;
        UpdateMappings();
        Midi.SelectDevices(profile.MidiInput, profile.MidiOutput);
        Engine.SetActiveProfile(profile.Name);
        Store.SaveSettings(Settings);
    }

    /// <summary>Persists the active profile and pushes its mappings to the running engine.</summary>
    public void SaveActiveProfile()
    {
        Engine.FeedbackEnabled = ActiveProfile.FeedbackEnabled;
        UpdateMappings();
        Store.SaveProfile(ActiveProfile);
    }

    public void SaveSettings()
    {
        Store.SaveSettings(Settings);
    }

    public void SaveGlobalProfile()
    {
        UpdateMappings();
        Store.SaveProfile(GlobalProfile);
    }

    private void UpdateMappings()
    {
        Engine.SetProfileButtons(GlobalProfile.Mappings.Where(m => m.TargetKind == MappingTargetKind.LoadProfile));
        Engine.SetMappings(GlobalProfile.Mappings.Where(m => m.TargetKind != MappingTargetKind.LoadProfile)
            .Concat(ActiveProfile.Mappings));
    }

    public async ValueTask DisposeAsync()
    {
        Engine.Dispose();
        await Client.DisposeAsync();
        Midi.Dispose();
    }
}
