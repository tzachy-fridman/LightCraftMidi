using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Core.Mapping;
using LightCraftMidi.Core.Midi;
using LightCraftMidi.Services;
using LightCraftMidi.ViewModels;
using LightCraftMidi.Views;

namespace LightCraftMidi.Tests;

public class GlobalProfileTests
{
    [Fact]
    public void SettingsWindow_Loads_DefaultsToHiddenStartup_AndPersistsChanges()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            Assert.True(store.LoadSettings().HideWindowOnStart);
            store.SaveSettings(new AppSettings());
            var host = new AppHost(store);
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                var settings = new SettingsWindow(vm);
                settings.UpdateLayout();
                Assert.True(vm.HideWindowOnStart);
                vm.HideWindowOnStart = false;
                Assert.False(store.LoadSettings().HideWindowOnStart);
                vm.HideWindowOnStart = true;
                Assert.True(store.LoadSettings().HideWindowOnStart);
                settings.Close();

                var main = new MainWindow(vm) { AllowClose = true };
                main.Show();
                main.UpdateLayout();
                var toggle = Descendants<ToggleButton>(main)
                    .Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "MappingEnabledToggle");
                Assert.Equal(vm.MappingEnabled, toggle.IsChecked);
                toggle.IsChecked = !vm.MappingEnabled;
                Assert.Equal(toggle.IsChecked, host.Engine.Enabled);
                Assert.Equal(host.Engine.Enabled, store.LoadSettings().MappingEnabled);
                Assert.DoesNotContain(Descendants<TextBox>(main),
                    b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "LightCraftHost");
                main.Close();
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("{}", false)]
    [InlineData(" {} ", false)]
    [InlineData("{rating: 0-5}", true)]
    public void ParameterVisibility_HidesEmptyDescriptions(string? description, bool visible)
    {
        InSta(dir =>
        {
            var host = new AppHost(new ProfileStore(dir));
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                var command = new LightCraftCommand("test", "Test", [], null, description, true, false, null);
                vm.SelectedCommand = command;
                Assert.Equal(visible, vm.SelectedCommandHasParams);
                var mapping = new MappingViewModel(new MidiMapping { TargetKind = MappingTargetKind.Command }, _ => { });
                mapping.ParamsHint = command.TakesNoParams ? null : "Params: " + description;
                Assert.Equal(visible, mapping.ShowParams);
                mapping.AssignTarget(MappingTargetKind.DevelopControl, "light.exposure", "Exposure");
                Assert.False(mapping.ShowParams);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void DetectedControl_DoesNotRequireFullTravelToIdentifyCC()
    {
        var detected = new DetectedControlViewModel(new MidiMessage(1, MidiMessageType.ControlChange, 7, 64));

        Assert.Equal("Control Change (CC)", detected.Kind);
        detected.Observe(new MidiMessage(1, MidiMessageType.ControlChange, 7, 72));
        Assert.Equal("Control Change (CC)", detected.Kind);
    }

    [Fact]
    public void AddResetButton_BecomesEnabledAfterNewMappingGetsControlTarget()
    {
        InSta(dir =>
        {
            var host = new AppHost(new ProfileStore(dir));
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                vm.SelectedControl = new DevelopControl("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", 0);
                vm.TargetTabIndex = 0;

                vm.AddMappingCommand.Execute(null);

                Assert.True(vm.AddResetButtonCommand.CanExecute(null));
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void MainViewModel_LoadsMidiDiscoveryForSelectedControllerOnly()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveProfile(new MappingProfile { Name = "Default", MidiInput = "Controller A" });
            store.SaveSettings(new AppSettings
            {
                MidiControlsByDevice = new Dictionary<string, List<DiscoveredMidiControl>>
                {
                    ["Controller A"] =
                    [
                        new(2, MidiMessageType.ControlChange, 14, 81, 6, "Control Change (CC)", DateTime.Now, [64, 81]),
                    ],
                    ["Controller B"] =
                    [
                        new(3, MidiMessageType.Note, 45, 127, 2, "Button / pad (note)", DateTime.Now, [0, 127]),
                    ],
                },
                CachedDevelopControls = [new("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", 2.5)],
                CachedCommands = [new("edit.undo", "Undo", ["Edit"], "Ctrl+Z", null, true, false, null)],
            });
            var host = new AppHost(store);
            try
            {
                var vm = new MainViewModel(host, new Dialogs());

                var control = Assert.Single(vm.DetectedControls);
                Assert.Equal(2, control.Channel);
                Assert.Equal(14, control.Number);
                Assert.Equal("light.exposure", Assert.Single(vm.DevelopControls).Id);
                Assert.Equal("edit.undo", Assert.Single(vm.Commands).Id);
                Assert.Contains("Cached:", vm.DiscoverySummary);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private sealed class Dialogs : IDialogService
    {
        public string? NextName { get; set; }
        public string? Prompt(string title, string label, string initial) => NextName;
        public bool Confirm(string title, string message) => true;
        public void Inform(string title, string message) { }
    }

    [Fact]
    public void GlobalProfile_MigratesLegacyButtons_AndSurvivesReload()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveSettings(new AppSettings
            {
                ProfileButtons = [
                new MidiMapping { Number = 12, TargetKind = MappingTargetKind.LoadProfile, TargetId = "Default" }]
            });
            var global = store.LoadGlobalProfile(store.LoadSettings());
            Assert.Single(global.Mappings);
            Assert.Empty(store.LoadSettings().ProfileButtons);
            Assert.Single(store.LoadGlobalProfile(store.LoadSettings()).Mappings);
            Assert.DoesNotContain(store.LoadProfiles(), p => p.Name == ProfileStore.GlobalProfileName);
        });
    }

    [Fact]
    public void Editor_MovesSwitchToGlobal_KeepsActiveProfile_AndUsesNormalLearnAndRemove()
    {
        InSta(dir =>
        {
            var host = new AppHost(new ProfileStore(dir));
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                var window = new MainWindow(vm) { AllowClose = true };
                window.Close();
                string active = host.ActiveProfile.Name;
                vm.AddMappingCommand.Execute(null);
                vm.LearnCommand.Execute(null);
                vm.SelectedSwitchProfileName = active;
                vm.AssignProfileCommand.Execute(null);
                Assert.Equal(active, vm.SelectedProfileName);
                Assert.DoesNotContain("Global", vm.ProfileNames);
                Assert.Equal(active, host.ActiveProfile.Name);
                Assert.Empty(host.ActiveProfile.Mappings);
                var mapping = Assert.Single(host.GlobalProfile.Mappings);
                Assert.Same(mapping, vm.SelectedMapping!.Model);
                Assert.True(vm.IsLearning);
                host.Engine.HandleMessage(new MidiMessage(1, MidiMessageType.Note, 36, 100));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.False(vm.IsLearning);
                Assert.Equal(36, mapping.Number);
                Assert.Single(new ProfileStore(dir).LoadGlobalProfile(new AppSettings()).Mappings);
                Assert.Single(vm.Mappings);
                vm.RemoveMappingCommand.Execute(null);
                Assert.Empty(host.GlobalProfile.Mappings);
                Assert.Empty(new ProfileStore(dir).LoadGlobalProfile(new AppSettings()).Mappings);
                vm.AddMappingCommand.Execute(null);
                vm.CancelLearnCommand.Execute(null);
                vm.SelectedMapping!.AssignSource(new MidiMessage(1, MidiMessageType.Note, 45, 127));
                vm.SelectedMapping.AssignTarget(MappingTargetKind.Command, "edit.undo", "Undo");
                MidiObservation? observed = null;
                host.Engine.MessageObserved += (_, message) => observed = message;
                host.Engine.HandleMessage(new MidiMessage(1, MidiMessageType.Note, 45, 127));
                Assert.Equal("edit.undo", Assert.Single(observed!.Matched).TargetId);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void GlobalTargets_FollowRenameAndDeletion_AndGlobalIsProtected()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveProfile(new MappingProfile { Name = "Default" });
            store.SaveProfile(new MappingProfile { Name = "Other" });
            var host = new AppHost(store);
            try
            {
                var dialogs = new Dialogs { NextName = "Renamed" };
                var vm = new MainViewModel(host, dialogs);
                vm.SelectedSwitchProfileName = "Default";
                vm.AssignProfileCommand.Execute(null);
                vm.RenameProfileCommand.Execute(null);
                Assert.Equal("Renamed", Assert.Single(host.GlobalProfile.Mappings).TargetId);
                vm.DeleteProfileCommand.Execute(null);
                Assert.Empty(host.GlobalProfile.Mappings);
                Assert.DoesNotContain("Global", vm.ProfileNames);
                Assert.Equal("Other", host.ActiveProfile.Name);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void CombinedList_KeepsGlobalFirstAcrossProfileChanges_AndEditsCorrectOwner()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveProfile(new MappingProfile
            {
                Name = "Default",
                Mappings = [
                new MidiMapping { Number = 1, TargetId = "light.exposure" }]
            });
            store.SaveProfile(new MappingProfile
            {
                Name = "Other",
                Mappings = [
                new MidiMapping { Number = 2, TargetId = "light.contrast" }]
            });
            store.SaveProfile(new MappingProfile
            {
                Name = "Global",
                Mappings = [
                new MidiMapping { Number = 36, TargetKind = MappingTargetKind.LoadProfile, TargetId = "Other" }]
            });
            var host = new AppHost(store);
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                Assert.Equal(["Global mappings", "Profile mappings"], vm.Mappings.Select(m => m.Scope));
                Assert.Equal(2, vm.MappingsView.Groups!.Count);
                vm.SelectedMapping = vm.Mappings[0];
                vm.SelectedMapping.AssignSource(new MidiMessage(1, MidiMessageType.ControlChange, 40, 64));
                Assert.Equal(40, Assert.Single(store.LoadGlobalProfile(new AppSettings()).Mappings).Number);
                vm.SelectedProfileName = "Other";
                Assert.Equal(["Global mappings", "Profile mappings"], vm.Mappings.Select(m => m.Scope));
                Assert.Equal(40, vm.Mappings[0].Number);
                Assert.Equal(2, vm.Mappings[1].Number);
                vm.SelectedMapping = vm.Mappings[1];
                vm.RemoveMappingCommand.Execute(null);
                Assert.Empty(host.ActiveProfile.Mappings);
                Assert.Single(host.GlobalProfile.Mappings);
                vm.SelectedMapping = vm.Mappings[0];
                vm.RemoveMappingCommand.Execute(null);
                Assert.Empty(host.GlobalProfile.Mappings);
                Assert.Empty(vm.Mappings);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }
    [Fact]
    public void MappingSelection_SurvivesFocusTransferAndTargetAssignment()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveProfile(new MappingProfile { Name = "Default", Mappings = [
                new MidiMapping { Number = 1 }, new MidiMapping { Number = 2 }] });
            var host = new AppHost(store);
            MainWindow? window = null;
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                window = new MainWindow(vm) { AllowClose = true };
                window.Show();
                window.Activate();
                var grid = (DataGrid)window.FindName("MappingList");
                var selected = vm.Mappings[1];
                grid.SelectedItem = selected;
                grid.Focus();
                Assert.Same(selected, vm.SelectedMapping);
                var control = new DevelopControl("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", 0);
                vm.DevelopControls.Add(control);
                window.UpdateLayout();
                var targets = Descendants<ListView>(window).First(v => v.ItemsSource == vm.ControlsView);
                targets.SelectedItem = control;
                targets.Focus();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Same(selected, grid.SelectedItem);
                Assert.Same(selected, vm.SelectedMapping);
                var targetItem = (ListViewItem)targets.ItemContainerGenerator.ContainerFromItem(control);
                Assert.NotNull(targetItem);
                targetItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent,
                    Source = targetItem,
                });
                Assert.Equal(control.Id, selected.TargetId);
                Assert.Equal(2, vm.Mappings.Count);
                var detected = new DetectedControlViewModel(new MidiMessage(3, MidiMessageType.ControlChange, 10, 64));
                vm.SelectedDetected = detected;
                vm.MapDetectedCommand.Execute(null);
                Assert.Equal(10, selected.Number);
                Assert.Equal(3, selected.Channel);
                Assert.Equal(control.Id, selected.TargetId);
                Assert.Equal(2, vm.Mappings.Count);
                vm.TargetTabIndex = 0;
                vm.AssignTargetCommand.Execute(null);
                Assert.Same(selected, vm.SelectedMapping);
                vm.SelectedSwitchProfileName = "Default";
                vm.TargetTabIndex = 2;
                vm.AssignTargetCommand.Execute(null);
                Assert.True(selected.IsGlobal);
                Assert.Same(selected, vm.SelectedMapping);
                Assert.Same(selected, grid.SelectedItem);
            }
            finally
            {
                window?.Close();
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void MoveMapping_ReordersWithinItsGroupAndPersistsOrder()
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            store.SaveProfile(new MappingProfile
            {
                Name = "Default",
                Mappings =
                [
                    new MidiMapping { Number = 1, TargetId = "light.exposure" },
                    new MidiMapping { Number = 2, TargetId = "light.contrast" },
                ],
            });
            store.SaveProfile(new MappingProfile
            {
                Name = "Global",
                Mappings = [new MidiMapping { Number = 36, TargetKind = MappingTargetKind.LoadProfile, TargetId = "Default" }],
            });
            var host = new AppHost(store);
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                var firstProfileMapping = vm.Mappings[1];
                vm.SelectedMapping = firstProfileMapping;

                Assert.False(vm.MoveMappingUpCommand.CanExecute(null));
                Assert.True(vm.MoveMappingDownCommand.CanExecute(null));
                vm.MoveMappingDownCommand.Execute(null);

                Assert.Equal([2, 1], host.ActiveProfile.Mappings.Select(mapping => mapping.Number));
                Assert.Equal([36, 2, 1], vm.Mappings.Select(mapping => mapping.Number));
                Assert.Same(firstProfileMapping, vm.SelectedMapping);
                Assert.Equal([2, 1], Assert.Single(store.LoadProfiles()).Mappings.Select(mapping => mapping.Number));
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Add_UsesSelectedSourceAndCurrentTabTarget_WithoutLearning(int tab)
    {
        InSta(dir =>
        {
            var store = new ProfileStore(dir);
            var host = new AppHost(store);
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                vm.SelectedControl = new DevelopControl("light.exposure", "Exposure", "light", -5, 5, 0, 0.01, 2, "centered", 0);
                vm.SelectedCommand = new LightCraftCommand("edit.undo", "Undo", ["Edit"], null, "{}", true, false, null);
                vm.SelectedSwitchProfileName = host.ActiveProfile.Name;
                vm.SelectedDetected = new DetectedControlViewModel(new MidiMessage(4, MidiMessageType.Note, 45, 127));
                vm.TargetTabIndex = tab;
                vm.AddMappingCommand.Execute(null);
                var mapping = Assert.Single(vm.Mappings);
                Assert.Same(mapping, vm.SelectedMapping);
                Assert.Equal(4, mapping.Channel);
                Assert.Equal(45, mapping.Number);
                Assert.Equal(MidiMessageType.Note, mapping.MessageType);
                Assert.Equal(tab == 0 ? "light.exposure" : tab == 1 ? "edit.undo" : host.ActiveProfile.Name, mapping.TargetId);
                Assert.Equal(tab == 2, mapping.IsGlobal);
                Assert.Equal(tab, vm.TargetTabIndex);
                Assert.False(vm.IsLearning);
                Assert.False(host.Engine.LearnMode);
                var saved = tab == 2 ? store.LoadGlobalProfile(new AppSettings()) : Assert.Single(store.LoadProfiles());
                Assert.Equal(mapping.Model.Id, Assert.Single(saved.Mappings).Id);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    [Fact]
    public void Add_WithoutSelections_CreatesUnassignedMappingWithoutLearning()
    {
        InSta(dir =>
        {
            var host = new AppHost(new ProfileStore(dir));
            try
            {
                var vm = new MainViewModel(host, new Dialogs());
                vm.AddMappingCommand.Execute(null);
                var mapping = Assert.Single(vm.Mappings);
                Assert.Equal("Midi not connected", vm.MidiStatus);
                Assert.Equal("LightCraft not connected", vm.LightCraftStatus);
                Assert.False(mapping.Model.HasSource);
                Assert.False(mapping.Model.HasTarget);
                Assert.False(vm.IsLearning);
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        });
    }

    private static void InSta(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "lcmidi-global-" + Guid.NewGuid().ToString("N"));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { test(dir); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
