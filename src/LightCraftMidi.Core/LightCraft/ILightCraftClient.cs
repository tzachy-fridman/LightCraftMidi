using System.Text.Json.Nodes;

namespace LightCraftMidi.Core.LightCraft;

public enum LightCraftConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed class LightCraftException(string message) : Exception(message);

public interface ILightCraftClient
{
    LightCraftConnectionState State { get; }
    string? LastError { get; }
    event EventHandler? StateChanged;

    /// <summary>Sends one JSON-lines request and returns its <c>result</c>; throws <see cref="LightCraftException"/> on <c>ok: false</c>.</summary>
    Task<JsonNode?> SendAsync(string method, JsonNode? parameters = null, CancellationToken ct = default);
}

public static class LightCraftClientExtensions
{
    public static Task<JsonNode?> ExecuteAsync(this ILightCraftClient client, string command, JsonNode? parameters = null, CancellationToken ct = default) =>
        client.SendAsync("engine.execute", parameters == null
            ? new JsonObject { ["command"] = command }
            : new JsonObject { ["command"] = command, ["params"] = parameters }, ct);

    public static async Task<IReadOnlyList<LightCraftCommand>> GetCommandsAsync(this ILightCraftClient client, CancellationToken ct = default)
    {
        var result = await client.SendAsync("engine.commands", null, ct).ConfigureAwait(false);
        return ExtractArray(result, "commands").Select(LightCraftCommand.FromJson).OfType<LightCraftCommand>().ToArray();
    }

    public static async Task<IReadOnlyList<DevelopControl>> GetControlsAsync(this ILightCraftClient client, CancellationToken ct = default)
    {
        var result = await client.ExecuteAsync("develop.controls", null, ct).ConfigureAwait(false);
        return ExtractArray(result, "controls").Select(DevelopControl.FromJson).OfType<DevelopControl>().ToArray();
    }

    private static IEnumerable<JsonNode?> ExtractArray(JsonNode? result, string key) => result switch
    {
        JsonArray a => a,
        JsonObject o when o[key] is JsonArray a => a,
        _ => [],
    };
}
