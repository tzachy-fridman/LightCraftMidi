using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;
using LightCraftMidi.Services;

namespace LightCraftMidi.ViewModels;

public interface IDialogService
{
    string? Prompt(string title, string label, string initial);
    bool Confirm(string title, string message);
    void Inform(string title, string message);
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppHost _host;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentQueue<MidiObservation> _incoming = new();
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _midiCacheTimer;
    private Dictionary<string, DevelopControl> _controlIndex = [];
    private Dictionary<string, LightCraftCommand> _commandIndex = [];
    private MappingViewModel? _learningMapping;
    private bool _loadingProfile;
    private bool _diagnosticSubscribed;
    private MappingTraceFile? _traceFile;
    private string? _detectedMidiDevice;
    private bool _midiDiscoveryDirty;

    public MainViewModel(AppHost host, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _lightCraftHost = host.Settings.Host;
        _port = host.Settings.Port;
        _mappingEnabled = host.Settings.MappingEnabled;
        _hideWindowOnStart = host.Settings.HideWindowOnStart;
        _logLevel = host.Settings.LogLevel;
        host.Engine.MinimumLogLevel = _logLevel;

        ControlsView = CollectionViewSource.GetDefaultView(DevelopControls);
        ControlsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DevelopControl.SectionLabel)));
        ControlsView.Filter = o => Filter((DevelopControl)o);
        CommandsView = CollectionViewSource.GetDefaultView(Commands);
        CommandsView.GroupDescriptions.Add(new PropertyGroupDescription(null, new CommandGroupConverter()));
        CommandsView.Filter = o => Filter((LightCraftCommand)o);

        LoadCachedLightCraftDiscovery();
        _midiCacheTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _midiCacheTimer.Tick += (_, _) => SaveDetectedMidiControls();

        foreach (var p in host.Profiles) ProfileNames.Add(p.Name);
        MappingsView = CollectionViewSource.GetDefaultView(Mappings);
        MappingsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MappingViewModel.Scope)));
        foreach (var p in host.Profiles) SwitchProfileNames.Add(p.Name);
        RefreshDevices();
        LoadProfile(host.ActiveProfile);

        host.Client.StateChanged += (_, _) => _dispatcher.BeginInvoke(OnLightCraftStateChanged);
        host.Midi.StateChanged += (_, _) => _dispatcher.BeginInvoke(() => { RefreshDevices(); UpdateStatus(); });
        host.Engine.MessageObserved += (_, o) => _incoming.Enqueue(o);
        host.Engine.Learned += (_, m) => _dispatcher.BeginInvoke(() => OnLearned(m));
        host.Engine.ProfileRequested += (_, name) => _dispatcher.BeginInvoke(() =>
        {
            var profile = _host.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (profile == null) LastMessage = $"Profile \"{name}\" no longer exists.";
            else if (profile != _host.ActiveProfile) LoadProfile(profile);
        });
        host.Engine.Log += (_, msg) =>
        {
            host.Engine.WriteDiagnostic(MappingLogLevel.Error, msg);
            _dispatcher.BeginInvoke(() => LastMessage = $"{DateTime.Now:HH:mm:ss}  {msg}");
        };

        _drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => DrainIncoming(), _dispatcher);
        _drainTimer.Start();
        InitializeLogging();
        UpdateStatus();
        if (host.Client.State == LightCraftConnectionState.Connected) _ = RefreshDiscoveryAsync();
    }

    /// <summary>Raised when the one-line status (for the tray) changes.</summary>
    public event EventHandler? StatusChanged;

    // ---------- Connection ----------

    [ObservableProperty] private string _lightCraftHost;

    [ObservableProperty] private int _port;
    [ObservableProperty] private string _lightCraftStatus = "LightCraft not connected";
    [ObservableProperty] private bool _lightCraftConnected;
    [ObservableProperty] private string _midiStatus = "Midi not connected";
    [ObservableProperty] private bool _midiConnected;
    [ObservableProperty] private string? _lastMessage;
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _discoverySummary = "Not discovered yet";

    public string TrayStatus => $"LightCraft: {(LightCraftConnected ? "connected" : "offline")} · MIDI: {(MidiConnected ? _host.Midi.DesiredInput : "offline")}";

    [ObservableProperty] private bool _mappingEnabled;
    [ObservableProperty] private bool _hideWindowOnStart;

    partial void OnHideWindowOnStartChanged(bool value)
    {
        _host.Settings.HideWindowOnStart = value;
        _host.SaveSettings();
    }

    partial void OnMappingEnabledChanged(bool value)
    {
        _host.Engine.Enabled = value;
        _host.Settings.MappingEnabled = value;
        _host.SaveSettings();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ApplyConnection()
    {
        _host.Settings.Host = string.IsNullOrWhiteSpace(LightCraftHost) ? "127.0.0.1" : LightCraftHost.Trim();
        _host.Settings.Port = Port is > 0 and < 65536 ? Port : 7980;
        _host.SaveSettings();
        _host.Client.Reconnect(_host.Settings.Host, _host.Settings.Port);
    }

    public void Reconnect()
    {
        _host.Client.Reconnect();
        _host.Midi.SelectDevices(null, null);
        _host.Midi.SelectDevices(_host.ActiveProfile.MidiInput, _host.ActiveProfile.MidiOutput);
    }

    private void OnLightCraftStateChanged()
    {
        UpdateStatus();
        TestMappingCommand.NotifyCanExecuteChanged();
        if (_host.Client.State == LightCraftConnectionState.Connected) _ = RefreshDiscoveryAsync();
    }

    private void LoadCachedLightCraftDiscovery()
    {
        var controls = _host.Settings.CachedDevelopControls;
        var commands = _host.Settings.CachedCommands;
        _host.Engine.UpdateControls(controls);
        foreach (var control in controls) DevelopControls.Add(control with { Value = null });
        foreach (var command in commands.OrderBy(CommandGroup).ThenBy(c => c.Label)) Commands.Add(command);
        _controlIndex = controls.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _commandIndex = commands.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        DiscoverySummary = controls.Count == 0 && commands.Count == 0
            ? "Connect to LightCraft to discover its controls and commands"
            : $"Cached: {controls.Count} develop controls · {commands.Count} commands";
        ValidateAll();
    }

    private void UpdateStatus()
    {
        var c = _host.Client;
        LightCraftConnected = c.State == LightCraftConnectionState.Connected;
        LightCraftStatus = LightCraftConnected
            ? $"LightCraft connected to {c.Host}:{c.Port}"
            : "LightCraft not connected";

        var m = _host.Midi;
        MidiConnected = m.IsInputOpen;
        MidiStatus = m.IsInputOpen
            ? $"MIDI: {m.DesiredInput}" + (m.DesiredOutput == null ? "" : m.IsOutputOpen ? $" · feedback to {m.DesiredOutput}" : $" · waiting for output {m.DesiredOutput}")
            : "Midi not connected";
        OnPropertyChanged(nameof(TrayStatus));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- LightCraft discovery ----------

    public ObservableCollection<DevelopControl> DevelopControls { get; } = [];
    public ObservableCollection<LightCraftCommand> Commands { get; } = [];
    public ICollectionView ControlsView { get; }
    public ICollectionView CommandsView { get; }

    [ObservableProperty] private string _targetSearch = "";
    [ObservableProperty] private bool _showAllCommands;
    [ObservableProperty] private DevelopControl? _selectedControl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedCommandHasParams))] private LightCraftCommand? _selectedCommand;
    public bool SelectedCommandHasParams => SelectedCommand is { TakesNoParams: false };
    [ObservableProperty] private int _targetTabIndex;

    partial void OnTargetSearchChanged(string value)
    {
        ControlsView.Refresh();
        CommandsView.Refresh();
    }

    partial void OnShowAllCommandsChanged(bool value) => CommandsView.Refresh();

    private static readonly HashSet<string> InternalCommandPrefixes = ["develop.beginInteraction", "develop.endInteraction", "develop.cancelInteraction", "develop.set", "develop.adjust", "develop.controls", "develop.get"];

    private bool Filter(DevelopControl c) =>
        Matches(TargetSearch, c.Label, c.Id, c.SectionLabel);

    private bool Filter(LightCraftCommand c)
    {
        // Without "show all", hide the pure query/automation commands that make no sense on a button.
        if (!ShowAllCommands && (c.Menu.Count == 0 && c.Shortcut == null || InternalCommandPrefixes.Contains(c.Id))) return false;
        return Matches(TargetSearch, c.Label, c.Id, c.MenuPath);
    }

    private static bool Matches(string search, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Any(f => f.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    public static string CommandGroup(LightCraftCommand c) => c.Menu.Count > 0 ? c.Menu[0] : "Other";

    [RelayCommand]
    private async Task RefreshDiscoveryAsync()
    {
        if (_host.Client.State != LightCraftConnectionState.Connected)
        {
            DiscoverySummary = "Connect to LightCraft to discover its controls and commands";
            return;
        }
        IsDiscovering = true;
        DiscoverySummary = "Refreshing LightCraft controls and commands…";
        try
        {
            var controls = await _host.Client.GetControlsAsync();
            var commands = await _host.Client.GetCommandsAsync();
            _host.Engine.UpdateControls(controls);

            DevelopControls.Clear();
            foreach (var c in controls) DevelopControls.Add(c);
            Commands.Clear();
            foreach (var c in commands.OrderBy(CommandGroup).ThenBy(c => c.Label)) Commands.Add(c);
            _controlIndex = controls.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            _commandIndex = commands.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            _host.Settings.CachedDevelopControls = controls.Select(c => c with { Value = null }).ToList();
            _host.Settings.CachedCommands = commands.ToList();
            try
            {
                _host.SaveSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastMessage = "Could not save LightCraft discovery cache: " + ex.Message;
            }
            DiscoverySummary = $"{controls.Count} develop controls · {commands.Count} commands";
            ValidateAll();
        }
        catch (LightCraftException ex)
        {
            DiscoverySummary = "Discovery failed: " + ex.Message;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    // ---------- MIDI devices ----------

    public ObservableCollection<string> InputDevices { get; } = [];
    public ObservableCollection<string> OutputDevices { get; } = [];
    [ObservableProperty] private string? _selectedInput;
    [ObservableProperty] private string? _selectedOutput;

    public const string NoDevice = "(none)";

    [RelayCommand]
    private void RefreshDevices()
    {
        Sync(InputDevices, [NoDevice, .. _host.Midi.GetInputDevices(), .. Missing(_host.ActiveProfile.MidiInput, _host.Midi.GetInputDevices())]);
        Sync(OutputDevices, [NoDevice, .. _host.Midi.GetOutputDevices(), .. Missing(_host.ActiveProfile.MidiOutput, _host.Midi.GetOutputDevices())]);
        SelectedInput = _host.ActiveProfile.MidiInput ?? NoDevice;
        SelectedOutput = _host.ActiveProfile.MidiOutput ?? NoDevice;

        // Remembered devices that are currently unplugged stay selectable.
        static IEnumerable<string> Missing(string? name, IReadOnlyList<string> present) =>
            name != null && !present.Contains(name) ? [name] : [];
    }

    private static void Sync(ObservableCollection<string> target, IList<string> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    partial void OnSelectedInputChanged(string? value) => ApplyDevices();
    partial void OnSelectedOutputChanged(string? value) => ApplyDevices();

    private void ApplyDevices()
    {
        if (_loadingProfile || SelectedInput == null || SelectedOutput == null) return;
        var input = SelectedInput == NoDevice ? null : SelectedInput;
        var output = SelectedOutput == NoDevice ? null : SelectedOutput;
        var p = _host.ActiveProfile;
        if (p.MidiInput == input && p.MidiOutput == output) return;
        if (!string.Equals(p.MidiInput, input, StringComparison.OrdinalIgnoreCase))
            LoadDetectedMidiControls(input);
        p.MidiInput = input;
        p.MidiOutput = output;
        _host.Midi.SelectDevices(input, output);
        _host.SaveActiveProfile();
        UpdateStatus();
    }

    // ---------- Profiles ----------

    public ObservableCollection<string> ProfileNames { get; } = [];
    public ObservableCollection<string> SwitchProfileNames { get; } = [];
    public string ActiveProfileText => "Active: " + _host.ActiveProfile.Name;
    [ObservableProperty] private string? _selectedSwitchProfileName;
    [ObservableProperty] private string? _selectedProfileName;
    [ObservableProperty] private bool _feedbackEnabled;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenLogCommand))] private string? _logFilePath;
    [ObservableProperty] private MappingLogLevel _logLevel;

    public IReadOnlyList<MappingLogLevel> LogLevels { get; } = Enum.GetValues<MappingLogLevel>();

    private void InitializeLogging()
    {
        try
        {
            _traceFile = CreateTraceFile();
            _host.Engine.Diagnostic += OnDiagnostic;
            _diagnosticSubscribed = true;
            LogFilePath = _traceFile.FilePath;
            _host.Engine.WriteDiagnostic(MappingLogLevel.Information, $"Logging started; minimum level={LogLevel}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastMessage = "Could not create log file: " + ex.Message;
        }
    }

    partial void OnLogLevelChanged(MappingLogLevel value)
    {
        _host.Engine.MinimumLogLevel = value;
        _host.Settings.LogLevel = value;
        _host.SaveSettings();
        _traceFile?.Write(new MappingDiagnostic(DateTime.Now, MappingLogLevel.Information, $"Minimum log level changed to {value}"));
    }

    private MappingTraceFile CreateTraceFile()
    {
        var file = new MappingTraceFile();
        file.WriteFailed += OnTraceWriteFailed;
        return file;
    }

    private void OnDiagnostic(object? sender, MappingDiagnostic diagnostic)
    {
        if (_traceFile?.Write(diagnostic) != true) return;
    }

    private void OnTraceWriteFailed(object? sender, Exception exception)
    {
        _dispatcher.BeginInvoke(() =>
        {
            UnsubscribeDiagnostic();
            LastMessage = "Log file write failed: " + exception.Message;
        });
    }

    private bool CanOpenLog() => LogFilePath is { } path && File.Exists(path);

    [RelayCommand(CanExecute = nameof(CanOpenLog))]
    private void OpenLog()
    {
        if (LogFilePath is not { } path || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastMessage = "Could not open log: " + ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveDetectedMidiControls();
        UnsubscribeDiagnostic();
        if (_traceFile != null)
        {
            _traceFile.WriteFailed -= OnTraceWriteFailed;
            await _traceFile.DisposeAsync();
            _traceFile = null;
        }
    }

    private void UnsubscribeDiagnostic()
    {
        if (!_diagnosticSubscribed) return;
        _host.Engine.Diagnostic -= OnDiagnostic;
        _diagnosticSubscribed = false;
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        if (_loadingProfile || value == null) return;
        var profile = _host.Profiles.FirstOrDefault(p => p.Name == value);
        if (profile != null) LoadProfile(profile);
    }

    partial void OnFeedbackEnabledChanged(bool value)
    {
        if (_loadingProfile) return;
        _host.ActiveProfile.FeedbackEnabled = value;
        _host.SaveActiveProfile();
    }

    private void LoadProfile(MappingProfile profile)
    {
        CancelLearn();
        _loadingProfile = true;
        try
        {
            _host.ApplyProfile(profile);
            LoadDetectedMidiControls(profile.MidiInput);
            SelectedProfileName = profile.Name;
            FeedbackEnabled = profile.FeedbackEnabled;
            Mappings.Clear();
            foreach (var m in _host.GlobalProfile.Mappings)
                Mappings.Add(new MappingViewModel(m, OnMappingChanged) { IsGlobal = true });
            foreach (var m in profile.Mappings) Mappings.Add(new MappingViewModel(m, OnMappingChanged));
            SelectedMapping = Mappings.FirstOrDefault();
        }
        finally
        {
            _loadingProfile = false;
        }
        RefreshDevices();
        OnPropertyChanged(nameof(ActiveProfileText));
        foreach (var d in DetectedControls) d.MappedTo = MappedTargets(d);
        ValidateAll();
        UpdateStatus();
    }

    private string? AskProfileName(string title, string initial)
    {
        var name = _dialogs.Prompt(title, "Profile name:", initial)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (string.Equals(name, ProfileStore.GlobalProfileName, StringComparison.OrdinalIgnoreCase)
            || _host.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ProfileStore.FileNameFor(p.Name), ProfileStore.FileNameFor(name), StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Inform("Profile exists", $"A profile named \"{name}\" already exists.");
            return null;
        }
        return name;
    }

    [RelayCommand]
    private void NewProfile()
    {
        var name = AskProfileName("New profile", "My controller");
        if (name == null) return;
        var profile = new MappingProfile { Name = name, MidiInput = _host.ActiveProfile.MidiInput, MidiOutput = _host.ActiveProfile.MidiOutput };
        AddProfile(profile);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        var name = AskProfileName("Duplicate profile", _host.ActiveProfile.Name + " copy");
        if (name == null) return;
        var copy = _host.ActiveProfile.Clone(name);
        copy.Mappings.RemoveAll(m => m.TargetKind == MappingTargetKind.LoadProfile);
        AddProfile(copy);
    }

    private void AddProfile(MappingProfile profile)
    {
        _host.Profiles.Add(profile);
        _host.Store.SaveProfile(profile);
        ProfileNames.Add(profile.Name);
        SwitchProfileNames.Add(profile.Name);
        LoadProfile(profile);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var current = _host.ActiveProfile;
        var name = AskProfileName("Rename profile", current.Name);
        if (name == null) return;
        int idx = ProfileNames.IndexOf(current.Name);
        string oldName = current.Name;
        _host.Store.RenameProfile(current, name);
        _loadingProfile = true;
        ProfileNames[idx] = name;
        SwitchProfileNames[SwitchProfileNames.IndexOf(oldName)] = name;
        SelectedProfileName = name;
        _loadingProfile = false;
        foreach (var b in _host.GlobalProfile.Mappings.Where(b => b.TargetKind == MappingTargetKind.LoadProfile && b.TargetId == oldName))
            b.TargetId = name;
        _host.Settings.ActiveProfile = name;
        OnPropertyChanged(nameof(ActiveProfileText));
        _host.SaveGlobalProfile();
        _host.SaveSettings();
        LoadProfile(current);
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var current = _host.ActiveProfile;
        if (_host.Profiles.Count <= 1)
        {
            _dialogs.Inform("Delete profile", "The last profile cannot be deleted.");
            return;
        }
        if (!_dialogs.Confirm("Delete profile", $"Delete profile \"{current.Name}\" and its {current.Mappings.Count} mappings? Global buttons that load it will also be removed.")) return;
        _host.GlobalProfile.Mappings.RemoveAll(b => b.TargetKind == MappingTargetKind.LoadProfile && b.TargetId == current.Name);
        _host.SaveGlobalProfile();
        _host.Store.DeleteProfile(current.Name);
        _host.Profiles.Remove(current);
        ProfileNames.Remove(current.Name);
        SwitchProfileNames.Remove(current.Name);
        LoadProfile(_host.Profiles[0]);
    }

    // ---------- Mappings ----------

    [RelayCommand]
    private void AssignProfile()
    {
        if (SelectedSwitchProfileName == null) return;
        var vm = SelectedMapping ?? NewMapping();
        vm.AssignTarget(MappingTargetKind.LoadProfile, SelectedSwitchProfileName, SelectedSwitchProfileName);
        vm = SelectedMapping ?? vm;
        if (!vm.Model.HasSource) StartLearn(vm);
    }

    private void SaveMappings()
    {
        _host.SaveGlobalProfile();
        _host.SaveActiveProfile();
    }

    public ObservableCollection<MappingViewModel> Mappings { get; } = [];
    public ICollectionView MappingsView { get; }
    [ObservableProperty, NotifyCanExecuteChangedFor(
        nameof(RemoveMappingCommand),
        nameof(LearnCommand),
        nameof(AddResetButtonCommand),
        nameof(MoveMappingUpCommand),
        nameof(MoveMappingDownCommand))]
    private MappingViewModel? _selectedMapping;
    [ObservableProperty] private bool _isLearning;

    public IReadOnlyList<CommandTrigger> Triggers { get; } = Enum.GetValues<CommandTrigger>();

    partial void OnSelectedMappingChanged(MappingViewModel? value)
    {
        TestMappingCommand.NotifyCanExecuteChanged();
        AddResetButtonCommand.NotifyCanExecuteChanged();
        MoveMappingUpCommand.NotifyCanExecuteChanged();
        MoveMappingDownCommand.NotifyCanExecuteChanged();
        if (value == null) return;
        TargetTabIndex = value.IsProfileSwitch ? 2 : value.IsCommand ? 1 : 0;
        if (value.IsProfileSwitch) SelectedSwitchProfileName = value.TargetId;
        if ((value.IsControl || value.IsResetControl) && _controlIndex.TryGetValue(value.TargetId, out var c)) SelectedControl = c;
        if (value.IsCommand && _commandIndex.TryGetValue(value.TargetId, out var cmd)) SelectedCommand = cmd;
    }

    private bool CanAddResetButton() => SelectedMapping is { IsControl: true, Model.HasTarget: true };

    [RelayCommand(CanExecute = nameof(CanAddResetButton))]
    private void AddResetButton()
    {
        if (SelectedMapping is not { IsControl: true } sliderMapping) return;

        CancelLearn();
        var model = new MidiMapping
        {
            TargetKind = MappingTargetKind.ResetControl,
            TargetId = sliderMapping.Model.TargetId,
            TargetLabel = sliderMapping.Model.TargetLabel,
            Feedback = false,
        };
        var profile = sliderMapping.IsGlobal ? _host.GlobalProfile : _host.ActiveProfile;
        profile.Mappings.Add(model);
        var resetMapping = new MappingViewModel(model, OnMappingChanged) { IsGlobal = sliderMapping.IsGlobal };
        Mappings.Add(resetMapping);
        SelectedMapping = resetMapping;
        Validate(resetMapping);
        SaveMappings();
        StartLearn(resetMapping);
    }

    private void OnMappingChanged(MappingViewModel vm)
    {
        TestMappingCommand.NotifyCanExecuteChanged();
        AddResetButtonCommand.NotifyCanExecuteChanged();
        if (vm.IsProfileSwitch && !vm.IsGlobal)
        {
            _host.ActiveProfile.Mappings.Remove(vm.Model);
            _host.GlobalProfile.Mappings.Add(vm.Model);
            vm.IsGlobal = true;
            Mappings.Remove(vm);
            Mappings.Insert(_host.GlobalProfile.Mappings.Count - 1, vm);
            MappingsView.Refresh();
            SelectedMapping = vm;
            LastMessage = "Profile-switch mapping moved to Global.";
        }
        Validate(vm);
        foreach (var other in Mappings.Where(m => m != vm)) Validate(other);
        foreach (var d in DetectedControls) d.MappedTo = MappedTargets(d);
        SaveMappings();
    }

    [RelayCommand]
    private void AddMapping()
    {
        var tab = TargetTabIndex;
        var control = SelectedControl;
        var command = SelectedCommand;
        var profile = SelectedSwitchProfileName;
        var source = SelectedDetected;
        CancelLearn();
        var vm = NewMapping();
        if (source != null)
            vm.AssignSource(new MidiMessage(source.Channel, source.Type, source.Number, 0));
        switch (tab)
        {
            case 0 when control != null:
                vm.AssignTarget(MappingTargetKind.DevelopControl, control.Id, $"{control.SectionLabel} › {control.Label}");
                break;
            case 1 when command != null:
                vm.AssignTarget(MappingTargetKind.Command, command.Id, command.Label);
                break;
            case 2 when profile != null:
                vm.AssignTarget(MappingTargetKind.LoadProfile, profile, profile);
                break;
        }
        TargetTabIndex = tab;
    }

    private MappingViewModel NewMapping()
    {
        var model = new MidiMapping();
        _host.ActiveProfile.Mappings.Add(model);
        var vm = new MappingViewModel(model, OnMappingChanged);
        Mappings.Add(vm);
        SelectedMapping = vm;
        Validate(vm);
        SaveMappings();
        return vm;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveMapping()
    {
        if (SelectedMapping is not { } vm) return;
        if (vm == _learningMapping) CancelLearn();
        int idx = Mappings.IndexOf(vm);
        (vm.IsGlobal ? _host.GlobalProfile : _host.ActiveProfile).Mappings.Remove(vm.Model);
        Mappings.Remove(vm);
        SelectedMapping = Mappings.Count == 0 ? null : Mappings[Math.Min(idx, Mappings.Count - 1)];
        OnMappingChangedAfterRemove();
    }

    private bool CanMoveMappingUp() => CanMoveMapping(-1);
    private bool CanMoveMappingDown() => CanMoveMapping(1);

    private bool CanMoveMapping(int direction)
    {
        if (SelectedMapping is not { } mapping) return false;
        var mappings = (mapping.IsGlobal ? _host.GlobalProfile : _host.ActiveProfile).Mappings;
        int index = mappings.IndexOf(mapping.Model);
        return index >= 0 && index + direction >= 0 && index + direction < mappings.Count;
    }

    [RelayCommand(CanExecute = nameof(CanMoveMappingUp))]
    private void MoveMappingUp() => MoveSelectedMapping(-1);

    [RelayCommand(CanExecute = nameof(CanMoveMappingDown))]
    private void MoveMappingDown() => MoveSelectedMapping(1);

    private void MoveSelectedMapping(int direction)
    {
        if (SelectedMapping is not { } mapping) return;
        var ownerMappings = (mapping.IsGlobal ? _host.GlobalProfile : _host.ActiveProfile).Mappings;
        int ownerIndex = ownerMappings.IndexOf(mapping.Model);
        int targetOwnerIndex = ownerIndex + direction;
        if (ownerIndex < 0 || targetOwnerIndex < 0 || targetOwnerIndex >= ownerMappings.Count) return;

        int listIndex = Mappings.IndexOf(mapping);
        if (listIndex < 0) return;
        int targetListIndex = listIndex + direction;
        if (targetListIndex < 0 || targetListIndex >= Mappings.Count
            || Mappings[targetListIndex].IsGlobal != mapping.IsGlobal) return;

        ownerMappings.RemoveAt(ownerIndex);
        ownerMappings.Insert(targetOwnerIndex, mapping.Model);
        Mappings.Move(listIndex, targetListIndex);
        MappingsView.Refresh();
        SelectedMapping = mapping;
        SaveMappings();
        MoveMappingUpCommand.NotifyCanExecuteChanged();
        MoveMappingDownCommand.NotifyCanExecuteChanged();
    }

    private void OnMappingChangedAfterRemove()
    {
        foreach (var m in Mappings) Validate(m);
        foreach (var d in DetectedControls) d.MappedTo = MappedTargets(d);
        SaveMappings();
    }

    private bool HasSelection() => SelectedMapping != null;

    private bool CanTestMapping() =>
        LightCraftConnected && SelectedMapping is { Model.HasTarget: true, IsProfileSwitch: false };

    [RelayCommand(CanExecute = nameof(CanTestMapping))]
    private async Task TestMappingAsync()
    {
        if (SelectedMapping is not { } vm) return;
        try
        {
            int? rawValue = null;
            if (vm.IsControl)
            {
                var detected = DetectedControls.FirstOrDefault(d => vm.Model.Matches(
                    new MidiMessage(d.Channel, d.Type, d.Number, d.LastValue)));
                if (detected == null)
                    throw new LightCraftException("Move the mapped MIDI control once before testing it.");
                rawValue = detected.LastValue;
            }

            await _host.Engine.TestMappingAsync(vm.Model, rawValue);
            LastMessage = $"Tested {vm.TargetText}.";
        }
        catch (Exception ex) when (ex is LightCraftException or JsonException or InvalidOperationException)
        {
            LastMessage = "Test failed: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Learn()
    {
        if (SelectedMapping == null) return;
        if (_learningMapping == SelectedMapping) CancelLearn();
        else StartLearn(SelectedMapping);
    }

    private void StartLearn(MappingViewModel vm)
    {
        CancelLearn();
        _learningMapping = vm;
        vm.IsLearning = true;
        IsLearning = true;
        _host.Engine.LearnMode = true;
    }

    [RelayCommand]
    private void CancelLearn()
    {
        _host.Engine.LearnMode = false;
        if (_learningMapping != null) _learningMapping.IsLearning = false;
        _learningMapping = null;
        IsLearning = false;
    }

    private void OnLearned(MidiMessage m)
    {
        var vm = _learningMapping;
        if (vm != null && vm.IsProfileSwitch && m.Type == MidiMessageType.PitchBend)
        {
            LastMessage = "Profile switching needs a note or CC button, not pitch bend. Press a button to learn.";
            _host.Engine.LearnMode = true;
            return;
        }
        _learningMapping = null;
        IsLearning = false;
        if (vm == null) return;
        vm.AssignSource(m);
        // Point the user at the kind of target that suits the control they touched.
        if (!vm.Model.HasTarget)
        {
            bool button = m.Type == MidiMessageType.Note;
            vm.TargetKind = button ? MappingTargetKind.Command : MappingTargetKind.DevelopControl;
            TargetTabIndex = button ? 1 : 0;
        }
        SelectedMapping = vm;
    }

    [RelayCommand]
    private void AssignTarget()
    {
        switch (TargetTabIndex)
        {
            case 0: AssignControl(SelectedControl); break;
            case 1: AssignCommand(SelectedCommand); break;
            case 2: AssignProfile(); break;
        }
    }

    [RelayCommand]
    private void AssignControl(DevelopControl? control)
    {
        control ??= SelectedControl;
        if (control == null) return;
        var vm = SelectedMapping ?? NewMapping();
        vm.AssignTarget(vm.IsResetControl ? MappingTargetKind.ResetControl : MappingTargetKind.DevelopControl,
            control.Id, $"{control.SectionLabel} › {control.Label}");
        if (!vm.Model.HasSource && !IsLearning) StartLearn(vm);
    }

    [RelayCommand]
    private void AssignCommand(LightCraftCommand? command)
    {
        command ??= SelectedCommand;
        if (command == null) return;
        var vm = SelectedMapping ?? NewMapping();
        vm.AssignTarget(MappingTargetKind.Command, command.Id, command.Label, command.TakesNoParams ? null : vm.Model.CommandParams);
        if (!vm.Model.HasSource && !IsLearning) StartLearn(vm);
    }

    private void ValidateAll()
    {
        foreach (var m in Mappings) Validate(m);
    }

    /// <summary>Flags mappings that will not do what the user probably expects.</summary>
    private void Validate(MappingViewModel vm)
    {
        var m = vm.Model;
        var warnings = new List<string>();
        vm.ParamsHint = null;
        vm.RangeHint = null;

        if (!m.HasSource) warnings.Add("No MIDI source — click Learn and move a control.");
        if (!m.HasTarget) warnings.Add("No target — pick a control, command or profile on the right.");
        if (vm.IsProfileSwitch)
        {
            if (!SwitchProfileNames.Contains(vm.TargetId)) warnings.Add("Select an existing profile.");
            if (vm.MessageType == MidiMessageType.PitchBend) warnings.Add("Use a note or CC button to switch profiles.");
        }
        if (m.HasSource && _host.GlobalProfile.Mappings.Any(b => b != m && b.TargetKind == MappingTargetKind.LoadProfile && b.HasSource && b.HasTarget
            && b.MessageType == m.MessageType && b.Number == m.Number
            && (b.Channel == 0 || m.Channel == 0 || b.Channel == m.Channel)))
            warnings.Add("This source is reserved by a global profile button and will not run this mapping.");

        if (m.HasSource && m.HasTarget)
        {
            int dupes = Mappings.Count(o => o != vm && o.Model.HasSource && o.Model.Channel == m.Channel && o.Model.MessageType == m.MessageType && o.Model.Number == m.Number);
            if (dupes > 0) warnings.Add($"Same MIDI source is used by {dupes} other mapping(s).");
        }

        if ((m.TargetKind is MappingTargetKind.DevelopControl or MappingTargetKind.ResetControl) && m.HasTarget)
        {
            if (_controlIndex.TryGetValue(m.TargetId, out var c))
            {
                if (vm.IsResetControl)
                {
                    vm.RangeHint = $"Button press sets {c.Format(c.Default)} (default)";
                    if (m.MessageType == MidiMessageType.PitchBend) warnings.Add("Use a note or button-style CC to reset a control.");
                    var source = DetectedControls.FirstOrDefault(d => m.Matches(new MidiMessage(d.Channel, d.Type, d.Number, 0)));
                    if (source?.IsContinuous == true) warnings.Add("This source looks like a knob/fader; use a button so resetting does not happen while turning.");
                }
                else
                {
                    vm.RangeHint = $"Full range {c.RangeText} (default {c.Format(c.Default)})";
                    if (m.RangeMin is { } lo && m.RangeMax is { } hi && lo >= hi) warnings.Add("Range minimum must be below maximum.");
                    if (m.MessageType == MidiMessageType.Note) warnings.Add("Buttons/notes only send on/off — the slider will jump between its ends. Use a knob or fader.");
                }
            }
            else if (_controlIndex.Count > 0) warnings.Add($"LightCraft has no develop control \"{m.TargetId}\".");
        }

        if (m.TargetKind == MappingTargetKind.Command && m.HasTarget)
        {
            if (_commandIndex.TryGetValue(m.TargetId, out var cmd))
            {
                if (!cmd.TakesNoParams) vm.ParamsHint = "Params: " + cmd.ParamsDoc;
            }
            else if (_commandIndex.Count > 0) warnings.Add($"LightCraft has no command \"{m.TargetId}\".");

            if (!string.IsNullOrWhiteSpace(m.CommandParams))
            {
                try
                {
                    if (JsonNode.Parse(m.CommandParams) is not JsonObject) warnings.Add("Params must be a JSON object, e.g. {\"rating\": 4}.");
                }
                catch (JsonException)
                {
                    warnings.Add("Params are not valid JSON.");
                }
            }
            var detected = DetectedControls.FirstOrDefault(d => m.Matches(new MidiMessage(d.Channel, d.Type, d.Number, 0)));
            if (m.MessageType == MidiMessageType.PitchBend || detected?.IsContinuous == true)
                warnings.Add("This looks like a fader/knob — the command will fire on every movement.");
        }

        vm.Warning = warnings.Count == 0 ? null : string.Join("\n", warnings);
    }

    // ---------- Detected MIDI controls ----------

    public ObservableCollection<DetectedControlViewModel> DetectedControls { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(MapDetectedCommand))]
    private DetectedControlViewModel? _selectedDetected;

    [RelayCommand(CanExecute = nameof(HasDetected))]
    private void MapDetected()
    {
        if (SelectedDetected is not { } d) return;
        var vm = SelectedMapping ?? NewMapping();
        vm.AssignSource(new MidiMessage(d.Channel, d.Type, d.Number, 0));
        if (!vm.Model.HasTarget)
        {
            vm.TargetKind = d.IsContinuous ? MappingTargetKind.DevelopControl : MappingTargetKind.Command;
            TargetTabIndex = d.IsContinuous ? 0 : 1;
        }
    }

    private bool HasDetected() => SelectedDetected != null;

    private void DrainIncoming()
    {
        if (_incoming.IsEmpty) return;
        var activeIds = new HashSet<Guid>();
        bool observedMessages = false;
        int budget = 500;
        while (budget-- > 0 && _incoming.TryDequeue(out var o))
        {
            observedMessages = true;
            var m = o.Message;
            var detected = DetectedControls.FirstOrDefault(d => d.Matches(m));
            if (detected == null)
            {
                detected = new DetectedControlViewModel(m);
                detected.MappedTo = MappedTargets(detected);
                DetectedControls.Add(detected);
            }
            else detected.Observe(m);
            foreach (var mapping in o.Matched) activeIds.Add(mapping.Id);
        }
        foreach (var mapping in Mappings) mapping.IsActive = activeIds.Contains(mapping.Model.Id);
        if (observedMessages && !string.IsNullOrWhiteSpace(_detectedMidiDevice))
        {
            _midiDiscoveryDirty = true;
            _midiCacheTimer.Stop();
            _midiCacheTimer.Start();
        }
        TestMappingCommand.NotifyCanExecuteChanged();
    }

    private void LoadDetectedMidiControls(string? device)
    {
        SaveDetectedMidiControls();
        _detectedMidiDevice = device;
        DetectedControls.Clear();
        if (!string.IsNullOrWhiteSpace(device))
        {
            var cached = _host.Settings.MidiControlsByDevice
                .FirstOrDefault(entry => string.Equals(entry.Key, device, StringComparison.OrdinalIgnoreCase)).Value;
            if (cached != null)
            {
                foreach (var snapshot in cached)
                {
                    var detected = new DetectedControlViewModel(snapshot) { MappedTo = MappedTargets(snapshot) };
                    DetectedControls.Add(detected);
                }
            }
        }
    }

    private string? MappedTargets(DiscoveredMidiControl detected) =>
        MappedTargets(detected.Channel, detected.Type, detected.Number);

    private string? MappedTargets(DetectedControlViewModel d)
        => MappedTargets(d.Channel, d.Type, d.Number);

    private string? MappedTargets(int channel, MidiMessageType type, int number)
    {
        var probe = new MidiMessage(channel, type, number, 0);
        var global = _host.GlobalProfile.Mappings.Where(m => m.TargetKind == MappingTargetKind.LoadProfile && m.HasTarget && m.Matches(probe)).ToArray();
        var names = (global.Length > 0 ? global : _host.GlobalProfile.Mappings.Concat(_host.ActiveProfile.Mappings))
            .Where(m => m.HasTarget && m.Matches(probe)).Select(m =>
                m.TargetKind == MappingTargetKind.LoadProfile ? "Load profile: " + m.TargetId
                : m.TargetKind == MappingTargetKind.ResetControl ? "Reset: " + (m.TargetLabel ?? m.TargetId)
                : m.TargetLabel ?? m.TargetId).ToArray();
        return names.Length == 0 ? null : string.Join(", ", names);
    }

    private void SaveDetectedMidiControls()
    {
        _midiCacheTimer.Stop();
        if (!_midiDiscoveryDirty) return;
        if (string.IsNullOrWhiteSpace(_detectedMidiDevice))
        {
            _midiDiscoveryDirty = false;
            return;
        }

        var key = _host.Settings.MidiControlsByDevice.Keys
            .FirstOrDefault(existing => string.Equals(existing, _detectedMidiDevice, StringComparison.OrdinalIgnoreCase));
        if (key != null && !string.Equals(key, _detectedMidiDevice, StringComparison.Ordinal))
            _host.Settings.MidiControlsByDevice.Remove(key);
        _host.Settings.MidiControlsByDevice[_detectedMidiDevice] = DetectedControls.Select(d => d.ToSnapshot()).ToList();
        try
        {
            _host.SaveSettings();
            _midiDiscoveryDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastMessage = "Could not save discovered MIDI controls: " + ex.Message;
        }
    }

    private sealed class CommandGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is LightCraftCommand c ? CommandGroup(c) : "Other";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
