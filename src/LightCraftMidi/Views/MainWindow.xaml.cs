using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LightCraftMidi.Core.LightCraft;
using LightCraftMidi.Services;
using LightCraftMidi.ViewModels;

namespace LightCraftMidi.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Icon = AppIcon.CreateImageSource();
    }

    /// <summary>When false, closing only hides the window so the app keeps running in the tray.</summary>
    public bool AllowClose { get; set; }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            new SettingsWindow(vm) { Owner = this }.ShowDialog();
    }

    private void AssignItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not MainViewModel vm
            || sender is not ListBoxItem item) return;
        switch (item.DataContext)
        {
            case DevelopControl control:
                vm.AssignControlCommand.Execute(control);
                break;
            case LightCraftCommand command:
                vm.AssignCommandCommand.Execute(command);
                break;
            case string profile:
                vm.SelectedSwitchProfileName = profile;
                vm.AssignProfileCommand.Execute(null);
                break;
            case DetectedControlViewModel detected:
                vm.SelectedDetected = detected;
                vm.MapDetectedCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (DataContext is MainViewModel vm) vm.CancelLearnCommand.Execute(null);
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
