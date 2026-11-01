using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;
using LightCraftMidi.Services;

namespace LightCraftMidi.Tests;

public class MappingEngineTests
{
    [Fact]
    public void GlobalButtons_SurviveProfileChanges_AndConsumePressAndRelease()
    {
        var button = new MidiMapping { Channel = 1, MessageType = MidiMessageType.Note, Number = 36,
            TargetKind = MappingTargetKind.LoadProfile, TargetId = "Editing" };
        var conflicting = new MidiMapping { Channel = 1, MessageType = MidiMessageType.Note, Number = 36,
            TargetKind = MappingTargetKind.Command, TargetId = "edit.undo", Trigger = CommandTrigger.OnAnyChange };
        var (engine, midi, client) = Create(conflicting);
        using (engine)
        {
            engine.SetProfileButtons([button]);
            var requests = new List<string>();
            engine.ProfileRequested += (_, name) => requests.Add(name);
            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 100));
            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 0));
            engine.SetMappings([]);
            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 100));
            Assert.Equal(["Editing", "Editing"], requests);
            Assert.Empty(client.Calls);
        }
    }

    [Fact]
    public void GlobalButtons_WorkOfflineAndPaused_ButLearnDoesNotSwitch()
    {
        var (engine, midi, client) = Create();
        using (engine)
        {
            client.State = LightCraftConnectionState.Disconnected;
            engine.Enabled = false;
            engine.SetProfileButtons([new MidiMapping { MessageType = MidiMessageType.ControlChange, Number = 12,
                TargetKind = MappingTargetKind.LoadProfile, TargetId = "Color" }]);
            var requests = new List<string>();
            engine.ProfileRequested += (_, name) => requests.Add(name);
            engine.LearnMode = true;
            midi.Receive(new MidiMessage(2, MidiMessageType.ControlChange, 12, 127));
            Assert.Empty(requests);
            midi.Receive(new MidiMessage(2, MidiMessageType.ControlChange, 12, 127));
            Assert.Equal(["Color"], requests);
        }
    }

    [Fact]
    public void GlobalButtons_RespectChannelAndToggleTrigger_FirstDuplicateWins()
    {
        var (engine, midi, _) = Create();
        using (engine)
        {
            var button = new MidiMapping { Channel = 3, MessageType = MidiMessageType.ControlChange, Number = 8,
                TargetKind = MappingTargetKind.LoadProfile, TargetId = "Light", Trigger = CommandTrigger.OnAnyChange };
            var duplicate = button.Clone();
            duplicate.TargetId = "Other";
            engine.SetProfileButtons([button, duplicate]);
            var requests = new List<string>();
            engine.ProfileRequested += (_, name) => requests.Add(name);
            midi.Receive(new MidiMessage(2, MidiMessageType.ControlChange, 8, 127));
            Assert.Empty(requests);
            midi.Receive(new MidiMessage(3, MidiMessageType.ControlChange, 8, 0));
            Assert.Equal(["Light"], requests);
        }
    }

    [Fact]
    public void ToggleProfileButtons_LoadOnlyOnActivation_AndClearOtherToggleButtons()
    {
        var (engine, midi, _) = Create();
        using (engine)
        {
            var first = new MidiMapping
            {
                Channel = 1,
                MessageType = MidiMessageType.ControlChange,
                Number = 10,
                TargetKind = MappingTargetKind.LoadProfile,
                TargetId = "Develop",
                HandleAsToggle = true,
            };
            var second = new MidiMapping
            {
                Channel = 1,
                MessageType = MidiMessageType.Note,
                Number = 36,
                TargetKind = MappingTargetKind.LoadProfile,
                TargetId = "Color",
                HandleAsToggle = true,
            };
            engine.SetProfileButtons([first, second]);
            var requests = new List<string>();
            engine.ProfileRequested += (_, name) => requests.Add(name);

            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 10, 127));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 10, 127));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 10, 0));
            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 127));

            Assert.Equal(["Develop", "Color"], requests);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 10, 0), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.Note, 36, 127), midi.Sent);

            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 0));
            midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 127));
            Assert.Equal(["Develop", "Color", "Color"], requests);
            Assert.Contains(new MidiMessage(1, MidiMessageType.Note, 36, 127), midi.Sent);
        }
    }

    [Fact]
    public void ActiveProfileButton_FeedbackRestoresItsOnStateWhenTurnedOff()
    {
        var (engine, midi, _) = Create();
        using (engine)
        {
            var button = new MidiMapping
            {
                Channel = 1,
                MessageType = MidiMessageType.ControlChange,
                Number = 10,
                TargetKind = MappingTargetKind.LoadProfile,
                TargetId = "Develop",
                HandleAsToggle = true,
            };
            engine.SetProfileButtons([button]);
            engine.SetActiveProfile("Develop");
            midi.Sent.Clear();

            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 10, 0));

            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 10, 127), midi.Sent);
        }
    }

    [Fact]
    public async Task GlobalButtonSource_DoesNotReceiveSliderFeedback()
    {
        var mapping = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 1,
            TargetId = "light.exposure" };
        var (engine, midi, _) = Create(mapping);
        using (engine)
        {
            var button = mapping.Clone();
            button.TargetKind = MappingTargetKind.LoadProfile;
            button.TargetId = "Editing";
            button.Channel = 0;
            engine.SetProfileButtons([button]);
            await engine.PushFeedbackAsync();
            Assert.Empty(midi.Sent);
        }
    }

    [Fact]
    public async Task ToggleCommands_ExecuteOnBothStateEdgesAndFollowLightCraftCheckedState()
    {
        var edit = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 10,
            TargetKind = MappingTargetKind.Command,
            TargetId = "panel.edit",
            HandleAsToggle = true,
        };
        var crop = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 11,
            TargetKind = MappingTargetKind.Command,
            TargetId = "panel.crop",
            HandleAsToggle = true,
        };
        var (engine, midi, client) = Create(edit, crop);
        using (engine)
        {
            SetMenuState(client, ("panel.edit", true), ("panel.crop", false));
            await engine.PushFeedbackAsync();
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 10, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 11, 0), midi.Sent);
            midi.Sent.Clear();

            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 11, 127));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 11, 127));
            await WaitForAsync(() => client.Executed().Any(c => c.Command == "panel.crop"));
            SetMenuState(client, ("panel.edit", false), ("panel.crop", true));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 11, 0));
            await WaitForAsync(() => client.Executed().Count(c => c.Command == "panel.crop") == 2);
            Assert.Equal(2, client.Executed().Count(c => c.Command == "panel.crop"));

            SetMenuState(client, ("panel.edit", true), ("panel.crop", false));
            await engine.PushFeedbackAsync();
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 10, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 11, 0), midi.Sent);
        }
    }

    [Fact]
    public async Task TogglePanelFeedback_CoversQueryablePanelSectionAndSidebarCommands()
    {
        var commands = new[]
        {
            (Id: "view.leftPanel", Number: 20),
            (Id: "view.rightPanel", Number: 21),
            (Id: "panel.presets", Number: 22),
            (Id: "section.light", Number: 23),
            (Id: "tool.brush", Number: 24),
            (Id: "panel.activity", Number: 25),
        };
        var mappings = commands.Select(command => new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = command.Number,
            TargetKind = MappingTargetKind.Command,
            TargetId = command.Id,
            HandleAsToggle = true,
        }).ToArray();
        var (engine, midi, client) = Create(mappings);
        using (engine)
        {
            SetMenuState(client,
                ("view.leftPanel", false),
                ("view.rightPanel", true),
                ("panel.presets", true),
                ("section.light", true),
                ("tool.brush", true),
                ("panel.activity", false));

            await engine.PushFeedbackAsync();

            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 20, 0), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 21, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 22, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 23, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 24, 127), midi.Sent);
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 25, 0), midi.Sent);
        }
    }

    [Fact]
    public async Task ToggleCommandState_MatchesConfiguredParamsAndExecutesWhenStateIsUnavailable()
    {
        var parameterized = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 30,
            TargetKind = MappingTargetKind.Command,
            TargetId = "panel.dynamic",
            CommandParams = "{\"mode\":\"a\"}",
            HandleAsToggle = true,
        };
        var action = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 31,
            TargetKind = MappingTargetKind.Command,
            TargetId = "edit.undo",
            HandleAsToggle = true,
        };
        var (engine, midi, client) = Create(parameterized, action);
        using (engine)
        {
            client.MenuTree = new JsonArray(new JsonObject
            {
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["kind"] = "item",
                        ["id"] = "panel.dynamic",
                        ["params"] = new JsonObject { ["mode"] = "a" },
                        ["checked"] = false,
                    },
                    new JsonObject
                    {
                        ["kind"] = "item",
                        ["id"] = "panel.dynamic",
                        ["params"] = new JsonObject { ["mode"] = "b" },
                        ["checked"] = true,
                    }),
            });

            await engine.PushFeedbackAsync();
            Assert.Contains(new MidiMessage(1, MidiMessageType.ControlChange, 30, 0), midi.Sent);
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 30, 127));
            await WaitForAsync(() => client.Executed().Any(c => c.Command == "panel.dynamic"));
            Assert.Single(client.Executed(), c => c.Command == "panel.dynamic");

            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 31, 127));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 31, 0));
            await WaitForAsync(() => client.Executed().Count(c => c.Command == "edit.undo") == 2);
            Assert.Equal(2, client.Executed().Count(c => c.Command == "edit.undo"));
        }
    }

    [Fact]
    public async Task ToggleCommand_InvalidParamsDoNotMatchMenuStateAndAreReported()
    {
        var mapping = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 32,
            TargetKind = MappingTargetKind.Command,
            TargetId = "panel.dynamic",
            CommandParams = "{invalid",
            HandleAsToggle = true,
        };
        var (engine, midi, client) = Create(mapping);
        using (engine)
        {
            var logs = new List<string>();
            engine.Log += (_, message) => logs.Add(message);
            SetMenuState(client, ("panel.dynamic", true));
            midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 32, 127));
            await WaitForAsync(() => logs.Any(message => message.Contains("invalid params JSON", StringComparison.Ordinal)));
            Assert.Empty(client.Executed());
        }
    }

    [Fact]
    public async Task TestMapping_CommandExecutesWithConfiguredParams()
    {
        var (engine, _, client) = Create();
        using (engine)
        {
            var mapping = new MidiMapping
            {
                TargetKind = MappingTargetKind.Command,
                TargetId = "photo.rate",
                CommandParams = "{\"rating\":4}",
            };
            await engine.TestMappingAsync(mapping);
            var call = Assert.Single(client.Executed());
            Assert.Equal("photo.rate", call.Command);
            Assert.Equal(4, call.Params!["rating"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task TestMapping_ControlUsesLastRawValueAndCreatesOneInteraction()
    {
        var (engine, _, client) = Create();
        using (engine)
        {
            await engine.RefreshControlsAsync();
            var mapping = new MidiMapping
            {
                MessageType = MidiMessageType.ControlChange,
                Number = 7,
                TargetId = "light.exposure",
            };
            await engine.TestMappingAsync(mapping, 127);
            var calls = client.Executed().Where(c => c.Command != "develop.controls").ToArray();
            Assert.Equal(["develop.beginInteraction", "develop.set", "develop.endInteraction"],
                calls.Select(c => c.Command));
            Assert.Equal(5.0, calls[1].Params!["values"]!["light.exposure"]!.GetValue<double>());
        }
    }

    [Fact]
    public async Task TestMapping_ReportsMissingControlValueAndDisconnectedLightCraft()
    {
        var (engine, _, client) = Create();
        using (engine)
        {
            var knob = new MidiMapping { Number = 7, TargetId = "light.exposure" };
            var missingValue = await Assert.ThrowsAsync<LightCraftException>(() => engine.TestMappingAsync(knob));
            Assert.Contains("Move the mapped MIDI control", missingValue.Message);

            client.State = LightCraftConnectionState.Disconnected;
            var offline = await Assert.ThrowsAsync<LightCraftException>(() => engine.TestMappingAsync(new MidiMapping
            {
                TargetKind = MappingTargetKind.Command,
                TargetId = "edit.undo",
            }));
            Assert.Contains("not connected", offline.Message);
        }
    }

    [Fact]
    public async Task ResetControl_UsesDefaultOncePerButtonPress()
    {
        var mapping = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.Note,
            Number = 36,
            TargetKind = MappingTargetKind.ResetControl,
            TargetId = "light.exposure",
        };
        var (engine, midi, client) = Create(mapping);
        await engine.RefreshControlsAsync();
        engine.Start();

        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 100));
        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 120));
        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 0));
        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 100));

        await WaitForAsync(() => client.Executed().Count(e => e.Command == "develop.set") == 2);
        var resetValues = client.Executed()
            .Where(e => e.Command == "develop.set")
            .Select(e => e.Params!["values"]!["light.exposure"]!.GetValue<double>());
        Assert.Equal([1.25, 1.25], resetValues);
        engine.Dispose();
    }

    [Fact]
    public async Task ResetControl_TestMappingAppliesDefaultWithoutRawValue()
    {
        var (engine, _, client) = Create();
        using (engine)
        {
            await engine.RefreshControlsAsync();
            var mapping = new MidiMapping
            {
                TargetKind = MappingTargetKind.ResetControl,
                TargetId = "light.exposure",
            };
            await engine.TestMappingAsync(mapping);

            var set = Assert.Single(client.Executed(), c => c.Command == "develop.set");
            Assert.Equal(1.25, set.Params!["values"]!["light.exposure"]!.GetValue<double>());
        }
    }

    [Fact]
    public async Task ResetControl_SendsKnobFeedbackAfterLightCraftAcknowledgesReset()
    {
        var reset = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.Note,
            Number = 36,
            TargetKind = MappingTargetKind.ResetControl,
            TargetId = "light.exposure",
        };
        var knob = new MidiMapping
        {
            Channel = 1,
            MessageType = MidiMessageType.ControlChange,
            Number = 7,
            TargetKind = MappingTargetKind.DevelopControl,
            TargetId = "light.exposure",
            Feedback = true,
        };
        var (engine, midi, client) = Create(reset, knob);
        await engine.RefreshControlsAsync();
        var control = Assert.Single(engine.Controls, c => c.Id == "light.exposure");
        engine.Start();

        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 127));
        await WaitForAsync(() => midi.Sent.Count > 0);

        var feedback = Assert.Single(midi.Sent);
        Assert.Equal(7, feedback.Number);
        Assert.Equal(ValueScaler.ToMidi(control.Default, feedback.MaxValue, knob, control), feedback.Value);
        Assert.Equal(1, client.Executed().Count(c => c.Command == "develop.set"));
        engine.Dispose();
    }

    private sealed class FakeMidi : IMidiService
    {
        public ConcurrentQueue<MidiMessage> Sent { get; } = new();
        public IReadOnlyList<string> GetInputDevices() => ["Fake"];
        public IReadOnlyList<string> GetOutputDevices() => ["Fake"];
        public string? DesiredInput => "Fake";
        public string? DesiredOutput => "Fake";
        public bool IsInputOpen => true;
        public bool IsOutputOpen => true;
        public void SelectDevices(string? input, string? output) { }
        public void Send(MidiMessage message) => Sent.Enqueue(message);
        public event EventHandler<MidiMessage>? MessageReceived;
        public event EventHandler? StateChanged { add { } remove { } }
        public void Receive(MidiMessage m) => MessageReceived?.Invoke(this, m);
        public void Dispose() { }
    }

    private sealed class FakeClient : ILightCraftClient
    {
        public ConcurrentQueue<(string Method, JsonNode? Params)> Calls { get; } = new();
        public double ExposureValue { get; set; } = 1.0;
        public TimeSpan DevelopSetDelay { get; set; }
        public JsonArray MenuTree { get; set; } = [];
        public LightCraftConnectionState State { get; set; } = LightCraftConnectionState.Connected;
        public string? LastError => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public async Task<JsonNode?> SendAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
        {
            Calls.Enqueue((method, parameters?.DeepClone()));
            if (method == "ui.menu.tree")
                return MenuTree.DeepClone();
            if (method == "engine.execute" && parameters?["command"]?.GetValue<string>() == "develop.controls")
            {
                return new JsonArray(
                    new JsonObject { ["id"] = "light.exposure", ["label"] = "Exposure", ["section"] = "light", ["min"] = -5.0, ["max"] = 5.0, ["default"] = 1.25, ["step"] = 0.01, ["decimals"] = 2, ["value"] = ExposureValue },
                    new JsonObject { ["id"] = "light.contrast", ["label"] = "Contrast", ["section"] = "light", ["min"] = -100, ["max"] = 100, ["default"] = 0, ["step"] = 1, ["decimals"] = 0, ["value"] = 0 });
            }
            if (method == "engine.execute" && parameters?["command"]?.GetValue<string>() == "develop.set" && DevelopSetDelay > TimeSpan.Zero)
                await Task.Delay(DevelopSetDelay, ct);
            return null;
        }

        public IEnumerable<(string Command, JsonNode? Params)> Executed() =>
            Calls.Where(c => c.Method == "engine.execute").Select(c => (c.Params!["command"]!.GetValue<string>(), c.Params["params"]));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Condition not met");
            await Task.Delay(10);
        }
    }

    private static (MappingEngine Engine, FakeMidi Midi, FakeClient Client) Create(params MidiMapping[] mappings)
    {
        var midi = new FakeMidi();
        var client = new FakeClient();
        var engine = new MappingEngine(midi, client) { FeedbackInterval = TimeSpan.FromHours(1), InteractionIdle = TimeSpan.FromMilliseconds(150) };
        engine.SetMappings(mappings);
        return (engine, midi, client);
    }

    private static void SetMenuState(FakeClient client, params (string Id, bool Checked)[] items)
    {
        client.MenuTree = new JsonArray(
            new JsonObject
            {
                ["label"] = "Window",
                ["children"] = new JsonArray(items.Select(item => (JsonNode?)new JsonObject
                {
                    ["kind"] = "item",
                    ["id"] = item.Id,
                    ["checked"] = item.Checked,
                }).ToArray()),
            });
    }

    [Fact]
    public async Task FaderMoves_SendEveryValueInOrderInsideOneInteraction()
    {
        var map = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 7, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        client.DevelopSetDelay = TimeSpan.FromMilliseconds(2);
        await engine.RefreshControlsAsync();
        engine.Start();

        for (int v = 0; v <= 127; v++) midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 7, v));

        await WaitForAsync(
            () => client.Executed().Any(e => e.Command == "develop.endInteraction"),
            timeoutMs: 10000);
        var executed = client.Executed().Where(e => e.Command != "develop.controls").ToList();

        Assert.Equal("develop.beginInteraction", executed[0].Command);
        Assert.Equal("develop.endInteraction", executed[^1].Command);
        var sets = executed.Where(e => e.Command == "develop.set").ToList();
        Assert.Equal(128, sets.Count);
        var control = Assert.Single(engine.Controls, c => c.Id == "light.exposure");
        Assert.Equal(Enumerable.Range(0, 128).Select(v => ValueScaler.ToTarget(v, 127, map, control)),
            sets.Select(e => e.Params!["values"]!["light.exposure"]!.GetValue<double>()));
        Assert.Equal(5.0, sets[^1].Params!["values"]!["light.exposure"]!.GetValue<double>());
        engine.Dispose();
    }

    [Fact]
    public async Task OtherChannelOrNumber_IsIgnored()
    {
        var map = new MidiMapping { Channel = 2, MessageType = MidiMessageType.ControlChange, Number = 7, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        await engine.RefreshControlsAsync();
        engine.Start();

        midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 7, 100));
        midi.Receive(new MidiMessage(2, MidiMessageType.ControlChange, 8, 100));
        await Task.Delay(300);

        Assert.DoesNotContain(client.Executed(), e => e.Command == "develop.set");
        engine.Dispose();
    }

    [Fact]
    public async Task NoteOn_ExecutesCommandWithParams_NoteOffDoesNot()
    {
        var map = new MidiMapping { MessageType = MidiMessageType.Note, Number = 36, TargetKind = MappingTargetKind.Command, TargetId = "photo.rate", CommandParams = "{\"rating\": 4}" };
        var (engine, midi, client) = Create(map);

        midi.Receive(new MidiMessage(5, MidiMessageType.Note, 36, 100));
        midi.Receive(new MidiMessage(5, MidiMessageType.Note, 36, 0));
        await WaitForAsync(() => client.Executed().Any(e => e.Command == "photo.rate"));
        await Task.Delay(100);

        var rate = Assert.Single(client.Executed(), e => e.Command == "photo.rate");
        Assert.Equal(4, rate.Params!["rating"]!.GetValue<int>());
        engine.Dispose();
    }

    [Fact]
    public async Task Disabled_DoesNotSendButStillObserves()
    {
        var map = new MidiMapping { MessageType = MidiMessageType.Note, Number = 36, TargetKind = MappingTargetKind.Command, TargetId = "edit.undo" };
        var (engine, midi, client) = Create(map);
        engine.Enabled = false;
        MidiObservation? seen = null;
        engine.MessageObserved += (_, o) => seen = o;

        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 127));
        await Task.Delay(100);

        Assert.Empty(client.Executed());
        Assert.NotNull(seen);
        Assert.Single(seen!.Matched);
        engine.Dispose();
    }

    [Fact]
    public void LearnMode_CapturesNextMessageAndSkipsNoteOff()
    {
        var (engine, midi, client) = Create(new MidiMapping { MessageType = MidiMessageType.Note, Number = 36, TargetKind = MappingTargetKind.Command, TargetId = "edit.undo" });
        engine.LearnMode = true;
        var learned = new List<MidiMessage>();
        engine.Learned += (_, m) => learned.Add(m);

        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 40, 0));
        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 36, 127));
        midi.Receive(new MidiMessage(1, MidiMessageType.Note, 37, 127));

        Assert.Equal([new MidiMessage(1, MidiMessageType.Note, 36, 127)], learned);
        Assert.False(engine.LearnMode);
        Assert.Empty(client.Calls); // the learned press must not also run the mapping
        engine.Dispose();
    }

    [Fact]
    public async Task Feedback_SendsCurrentValuesOnlyWhenChanged()
    {
        var map = new MidiMapping { Channel = 3, MessageType = MidiMessageType.ControlChange, Number = 20, TargetId = "light.exposure" };
        var noFeedback = new MidiMapping { Channel = 3, MessageType = MidiMessageType.ControlChange, Number = 21, TargetId = "light.contrast", Feedback = false };
        var (engine, midi, client) = Create(map, noFeedback);

        client.ExposureValue = 0; // centre
        await engine.PushFeedbackAsync();
        await engine.PushFeedbackAsync();
        client.ExposureValue = 5;
        await engine.PushFeedbackAsync();

        Assert.Equal([new MidiMessage(3, MidiMessageType.ControlChange, 20, 64), new MidiMessage(3, MidiMessageType.ControlChange, 20, 127)], midi.Sent);
        engine.Dispose();
    }

    [Fact]
    public async Task Feedback_IsHeldBackWhileControlIsBeingMoved()
    {
        var map = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 1, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        await engine.RefreshControlsAsync();

        midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 1, 10));
        client.ExposureValue = 3;
        int requestsBefore = client.Calls.Count;
        await engine.PushFeedbackAsync();

        Assert.Empty(midi.Sent);
        Assert.Equal(requestsBefore, client.Calls.Count);
        engine.Dispose();
    }

    [Fact]
    public async Task DebugTrace_ReportsRawMappedAndAcknowledgedValues()
    {
        var map = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 7, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        client.DevelopSetDelay = TimeSpan.FromMilliseconds(10);
        engine.MinimumLogLevel = MappingLogLevel.Trace;
        var trace = new ConcurrentQueue<string>();
        engine.Diagnostic += (_, diagnostic) => trace.Enqueue(diagnostic.Message);
        await engine.RefreshControlsAsync();
        engine.Start();

        midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 7, 127));
        await WaitForAsync(() => trace.Any(line => line.Contains("ACK #", StringComparison.Ordinal)));

        var queued = Assert.Single(trace, line => line.StartsWith("QUEUED #", StringComparison.Ordinal));
        var send = Assert.Single(trace, line => line.StartsWith("SEND #", StringComparison.Ordinal));
        var ack = Assert.Single(trace, line => line.StartsWith("ACK #", StringComparison.Ordinal));
        Assert.Contains("raw=127/127 -> light.exposure=5", queued);
        Assert.Contains("queued=", send);
        Assert.Contains("request=", ack);
        Assert.Contains(queued.Split(' ')[1], send);
        Assert.Contains(send.Split(' ')[1], ack);
        engine.Dispose();
    }

    [Fact]
    public async Task DebugTrace_ReportsFeedbackValueSentBackToMidi()
    {
        var map = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 7, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        var trace = new ConcurrentQueue<string>();
        engine.Diagnostic += (_, diagnostic) => trace.Enqueue(diagnostic.Message);
        client.ExposureValue = 5;

        await engine.PushFeedbackAsync();

        Assert.Contains(trace, line => line.Contains("Feedback light.exposure=5", StringComparison.Ordinal)
            && line.Contains("CC 7", StringComparison.Ordinal));
        Assert.Equal(127, Assert.Single(midi.Sent).Value);
    }

    [Fact]
    public void DiagnosticLevel_FiltersBelowConfiguredMinimum()
    {
        var (engine, _, _) = Create();
        using (engine)
        {
            var diagnostics = new List<MappingDiagnostic>();
            engine.Diagnostic += (_, diagnostic) => diagnostics.Add(diagnostic);
            engine.MinimumLogLevel = MappingLogLevel.Warning;

            engine.WriteDiagnostic(MappingLogLevel.Debug, "debug");
            engine.WriteDiagnostic(MappingLogLevel.Warning, "warning");
            engine.WriteDiagnostic(MappingLogLevel.Error, "error");

            Assert.Equal([MappingLogLevel.Warning, MappingLogLevel.Error], diagnostics.Select(d => d.Level));
        }
    }

    [Fact]
    public async Task MappingTraceFile_WritesTimestampedLinesAndFlushesOnDispose()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LightCraftMidiTests", Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            await using (var traceFile = new MappingTraceFile(directory))
            {
                path = traceFile.FilePath;
                Assert.True(traceFile.Write(new MappingDiagnostic(DateTime.Now, MappingLogLevel.Debug, "SEND #1 develop.set")));
                Assert.True(traceFile.Write(new MappingDiagnostic(DateTime.Now, MappingLogLevel.Debug, "ACK #1")));
            }

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("[Debug]", lines[0]);
            Assert.Contains("SEND #1 develop.set", lines[0]);
            Assert.Contains("ACK #1", lines[1]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
