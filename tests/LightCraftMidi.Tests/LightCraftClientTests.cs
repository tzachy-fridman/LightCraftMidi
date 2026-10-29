using System.Text.Json.Nodes;
using LightCraftMidi.Core.LightCraft;

namespace LightCraftMidi.Tests;

public class LightCraftClientTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met");
            await Task.Delay(20);
        }
    }

    internal static JsonObject DefaultHandler(JsonObject req)
    {
        string method = req["method"]!.GetValue<string>();
        string? command = req["params"]?["command"]?.GetValue<string>();
        if (method == "engine.commands")
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["result"] = JsonNode.Parse("""
                [
                  {"id":"photo.rate","label":"Rate","menu":["Photo","Rating"],"shortcut":null,"params":"{rating: 0-5}","enabled":true},
                  {"id":"edit.undo","label":"Undo","menu":["Edit"],"shortcut":"Cmd+Z","params":"{}","enabled":true},
                  {"id":"view.filmstrip","label":"Filmstrip","shortcut":"F6","menu":["View"],"enabled":true,"ui":true}
                ]
                """),
            };
        }
        if (method == "engine.execute" && command == "develop.controls")
        {
            return new JsonObject
            {
                ["ok"] = true,
                ["result"] = JsonNode.Parse("""
                [
                  {"id":"light.exposure","label":"Exposure","section":"light","min":-5.0,"max":5.0,"default":0.0,"step":0.01,"decimals":2,"track":{"kind":"centered"},"value":0.5},
                  {"id":"wb.temp","label":"Temp","section":"color","min":2000,"max":50000,"default":5500,"step":50,"decimals":0,"track":{"kind":"temp"},"value":null}
                ]
                """),
            };
        }
        if (method == "engine.execute" && command == "nope")
            return new JsonObject { ["ok"] = false, ["error"] = "unknown command `nope`" };
        return new JsonObject { ["ok"] = true, ["result"] = null };
    }

    [Fact]
    public async Task Discovers_CommandsAndControls()
    {
        await using var server = new FakeLightCraftServer { Handler = DefaultHandler };
        await using var client = new LightCraftClient("127.0.0.1", server.Port);
        client.Start();
        await WaitForAsync(() => client.State == LightCraftConnectionState.Connected);

        var commands = await client.GetCommandsAsync();
        Assert.Equal(3, commands.Count);
        var rate = commands[0];
        Assert.Equal("photo.rate", rate.Id);
        Assert.Equal("Photo › Rating", rate.MenuPath);
        Assert.False(rate.TakesNoParams);
        Assert.True(commands[1].TakesNoParams);
        Assert.True(commands[2].IsUi);

        var controls = await client.GetControlsAsync();
        Assert.Equal(2, controls.Count);
        Assert.Equal("Light", controls[0].SectionLabel);
        Assert.Equal(0.5, controls[0].Value);
        Assert.Equal("centered", controls[0].Track);
        Assert.Null(controls[1].Value);

        var req = server.Requests.Last();
        Assert.Equal("engine.execute", req["method"]!.GetValue<string>());
        Assert.Equal("develop.controls", req["params"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task ErrorReply_ThrowsLightCraftException()
    {
        await using var server = new FakeLightCraftServer { Handler = DefaultHandler };
        await using var client = new LightCraftClient("127.0.0.1", server.Port);
        client.Start();
        await WaitForAsync(() => client.State == LightCraftConnectionState.Connected);

        var ex = await Assert.ThrowsAsync<LightCraftException>(() => client.ExecuteAsync("nope"));
        Assert.Contains("unknown command", ex.Message);
    }

    [Fact]
    public async Task Reconnects_AfterServerDropsConnection()
    {
        await using var server = new FakeLightCraftServer { Handler = DefaultHandler };
        await using var client = new LightCraftClient("127.0.0.1", server.Port) { ReconnectDelay = TimeSpan.FromMilliseconds(100) };
        client.Start();
        await WaitForAsync(() => client.State == LightCraftConnectionState.Connected);

        server.DropClients();
        await WaitForAsync(() => client.State != LightCraftConnectionState.Connected);
        await WaitForAsync(() => client.State == LightCraftConnectionState.Connected);
        Assert.NotEmpty(await client.GetControlsAsync());
    }

    [Fact]
    public async Task SendWhileDisconnected_Throws()
    {
        await using var client = new LightCraftClient("127.0.0.1", 1);
        await Assert.ThrowsAsync<LightCraftException>(() => client.SendAsync("engine.commands"));
    }
}
