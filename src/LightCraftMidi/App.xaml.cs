using System.Windows;
using System.Windows.Threading;
using LightCraftMidi.Services;
using LightCraftMidi.ViewModels;
using LightCraftMidi.Views;

namespace LightCraftMidi;

public partial class App : Application
{
    private const string MutexName = @"Local\LightCraftMidi.SingleInstance";
    private const string ShowEventName = @"Local\LightCraftMidi.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private AppHost? _host;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running in the tray: ask that instance to show its window.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);

        DispatcherUnhandledException += OnUnhandledException;

        _host = new AppHost();
        _viewModel = new MainViewModel(_host, new DialogService(() => _window is { IsVisible: true } ? _window : null));
        _window = new MainWindow(_viewModel);

        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => ShowWindow();
        _tray.ReconnectRequested += (_, _) => _viewModel.Reconnect();
        _tray.EnabledChanged += (_, enabled) => _viewModel.MappingEnabled = enabled;
        _tray.ExitRequested += (_, _) => ExitApp();
        _viewModel.StatusChanged += OnStatusChanged;
        _tray.Update(_viewModel.TrayStatus, _viewModel.LightCraftConnected, _viewModel.MappingEnabled);

        _host.Start();

        if (!_host.Settings.HideWindowOnStart || e.Args.Contains("--show"))
            ShowWindow();
        else
            _tray.ShowBalloon("LightCraft MIDI", "Running in the background. Double-click the tray icon to configure.");
    }

    private void ShowWindow()
    {
        if (!_isExiting) _window?.ShowAndActivate();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (!_isExiting && _viewModel is { } viewModel)
            _tray?.Update(viewModel.TrayStatus, viewModel.LightCraftConnected, viewModel.MappingEnabled);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "LightCraft MIDI", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private async void ExitApp()
    {
        if (_isExiting) return;
        _isExiting = true;
        if (_viewModel != null) _viewModel.StatusChanged -= OnStatusChanged;
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _tray?.Dispose();
        _tray = null;
        if (_viewModel != null) await _viewModel.DisposeAsync();
        if (_host != null) await _host.DisposeAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
