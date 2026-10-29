using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightCraftMidi.Core.Mapping;

/// <summary>Stores settings.json and one JSON file per profile under a base folder (default %APPDATA%\LightCraftMidi).</summary>
public sealed class ProfileStore
{
    public const string GlobalProfileName = "Global";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public ProfileStore(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightCraftMidi");
        ProfilesDirectory = Path.Combine(BaseDirectory, "profiles");
        Directory.CreateDirectory(ProfilesDirectory);
    }

    public string BaseDirectory { get; }
    public string ProfilesDirectory { get; }
    private string SettingsPath => Path.Combine(BaseDirectory, "settings.json");

    public AppSettings LoadSettings() => Read<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) => Write(SettingsPath, settings);

    public IReadOnlyList<MappingProfile> LoadProfiles()
    {
        var profiles = Directory.EnumerateFiles(ProfilesDirectory, "*.json")
            .Select(Read<MappingProfile>)
            .OfType<MappingProfile>()
            .Where(p => !string.Equals(p.Name, GlobalProfileName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (profiles.Count == 0)
        {
            var def = new MappingProfile { Name = "Default" };
            SaveProfile(def);
            profiles.Add(def);
        }
        return profiles;
    }

    public MappingProfile LoadGlobalProfile(AppSettings settings)
    {
        var profile = Read<MappingProfile>(PathFor(GlobalProfileName)) ?? new MappingProfile { Name = GlobalProfileName };
        foreach (var mapping in settings.ProfileButtons)
            if (!profile.Mappings.Any(m => m.Id == mapping.Id)) profile.Mappings.Add(mapping.Clone());
        SaveProfile(profile);
        if (settings.ProfileButtons.Count > 0)
        {
            settings.ProfileButtons.Clear();
            SaveSettings(settings);
        }
        return profile;
    }

    public void SaveProfile(MappingProfile profile) => Write(PathFor(profile.Name), profile);

    public void DeleteProfile(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public void RenameProfile(MappingProfile profile, string newName)
    {
        var oldName = profile.Name;
        profile.Name = newName;
        SaveProfile(profile);
        if (!string.Equals(FileNameFor(oldName), FileNameFor(newName), StringComparison.OrdinalIgnoreCase))
            DeleteProfile(oldName);
    }

    public static string FileNameFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return (safe.Length == 0 ? "profile" : safe) + ".json";
    }

    private string PathFor(string name) => Path.Combine(ProfilesDirectory, FileNameFor(name));

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
