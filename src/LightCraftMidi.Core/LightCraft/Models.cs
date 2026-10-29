using System.Text.Json.Nodes;

namespace LightCraftMidi.Core.LightCraft;

/// <summary>An engine or UI command from <c>engine.commands</c>.</summary>
public sealed record LightCraftCommand(
    string Id,
    string Label,
    IReadOnlyList<string> Menu,
    string? Shortcut,
    string? ParamsDoc,
    bool Enabled,
    bool IsUi,
    string? DisabledReason)
{
    public string MenuPath => Menu.Count == 0 ? "(no menu)" : string.Join(" › ", Menu);

    /// <summary>True when the params doc says the command takes no parameters.</summary>
    public bool TakesNoParams => string.IsNullOrWhiteSpace(ParamsDoc) || ParamsDoc.Trim() is "{}" or "—" or "-";

    public static LightCraftCommand? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var id = Json.Str(o, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var menu = o["menu"] switch
        {
            JsonArray a => a.Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToArray(),
            JsonValue v when v.ToString().Length > 0 => [v.ToString()],
            _ => Array.Empty<string>(),
        };
        return new LightCraftCommand(
            id,
            Json.Str(o, "label") ?? id,
            menu,
            Json.Str(o, "shortcut"),
            Json.Str(o, "params"),
            Json.Bool(o, "enabled") ?? true,
            Json.Bool(o, "ui") ?? false,
            Json.Str(o, "disabledReason") ?? Json.Str(o, "disabled_reason"));
    }
}

/// <summary>A numeric develop slider from <c>develop.controls</c>.</summary>
public sealed record DevelopControl(
    string Id,
    string Label,
    string Section,
    double Min,
    double Max,
    double Default,
    double Step,
    int Decimals,
    string? Track,
    double? Value)
{
    public string SectionLabel => SectionLabels.TryGetValue(Section, out var l) ? l : Humanize(Section);

    private static string Humanize(string s) => s.Length == 0 ? s
        : char.ToUpperInvariant(s[0]) + System.Text.RegularExpressions.Regex.Replace(s[1..], "(?<=[a-z0-9])([A-Z])", " $1");

    public string RangeText => $"{Format(Min)} … {Format(Max)}";

    public string Format(double v) => v.ToString("F" + Math.Clamp(Decimals, 0, 6), System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Dictionary<string, string> SectionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["light"] = "Light", ["curve"] = "Tone Curve", ["color"] = "Color", ["mixer"] = "Color Mixer",
        ["bwMix"] = "B&W Mixer", ["grading"] = "Color Grading", ["effects"] = "Effects", ["vignette"] = "Vignette",
        ["grain"] = "Grain", ["detail"] = "Detail", ["optics"] = "Optics", ["geometry"] = "Geometry",
        ["profile"] = "Profile", ["calibration"] = "Calibration", ["pointColor"] = "Point Color", ["redEye"] = "Red Eye",
    };

    public static DevelopControl? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var id = Json.Str(o, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var track = o["track"] switch
        {
            JsonObject t => Json.Str(t, "kind"),
            JsonValue v => v.ToString(),
            _ => null,
        };
        double min = Json.Num(o, "min") ?? 0, max = Json.Num(o, "max") ?? 1;
        if (max < min) (min, max) = (max, min);
        return new DevelopControl(
            id,
            Json.Str(o, "label") ?? id,
            Json.Str(o, "section") ?? "",
            min,
            max,
            Json.Num(o, "default") ?? 0,
            Json.Num(o, "step") ?? 0,
            (int)(Json.Num(o, "decimals") ?? 2),
            track,
            Json.Num(o, "value"));
    }
}

internal static class Json
{
    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
