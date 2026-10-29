using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;

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
        public LightCraftConnectionState State { get; set; } = LightCraftConnectionState.Connected;
        public string? LastError => null;
        public event EventHandler? StateChanged { add { } remove { } }

        public Task<JsonNode?> SendAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
        {
            Calls.Enqueue((method, parameters?.DeepClone()));
            if (method == "engine.execute" && parameters?["command"]?.GetValue<string>() == "develop.controls")
            {
                return Task.FromResult<JsonNode?>(new JsonArray(
                    new JsonObject { ["id"] = "light.exposure", ["label"] = "Exposure", ["section"] = "light", ["min"] = -5.0, ["max"] = 5.0, ["default"] = 0.0, ["step"] = 0.01, ["decimals"] = 2, ["value"] = ExposureValue },
                    new JsonObject { ["id"] = "light.contrast", ["label"] = "Contrast", ["section"] = "light", ["min"] = -100, ["max"] = 100, ["default"] = 0, ["step"] = 1, ["decimals"] = 0, ["value"] = 0 }));
            }
            return Task.FromResult<JsonNode?>(null);
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

    [Fact]
    public async Task FaderMoves_AreCoalescedIntoDevelopSetInsideOneInteraction()
    {
        var map = new MidiMapping { Channel = 1, MessageType = MidiMessageType.ControlChange, Number = 7, TargetId = "light.exposure" };
        var (engine, midi, client) = Create(map);
        await engine.RefreshControlsAsync();
        engine.Start();

        for (int v = 0; v <= 127; v++) midi.Receive(new MidiMessage(1, MidiMessageType.ControlChange, 7, v));

        await WaitForAsync(() => client.Executed().Any(e => e.Command == "develop.endInteraction"));
        var executed = client.Executed().Where(e => e.Command != "develop.controls").ToList();

        Assert.Equal("develop.beginInteraction", executed[0].Command);
        Assert.Equal("develop.endInteraction", executed[^1].Command);
        var sets = executed.Where(e => e.Command == "develop.set").ToList();
        Assert.InRange(sets.Count, 1, 128);
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
        await engine.PushFeedbackAsync();

        Assert.Empty(midi.Sent);
        engine.Dispose();
    }
}
