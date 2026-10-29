using System.Windows.Forms;

namespace LightCraftMidi.Services;

/// <summary>Notification-area icon with the app's context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Drawing.Icon _activeIcon = AppIcon.Create(true);
    private readonly System.Drawing.Icon _inactiveIcon = AppIcon.Create(false);

    public TrayIcon()
    {
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Mapping enabled") { CheckOnClick = true, Checked = true };
        _enabledItem.CheckedChanged += (_, _) => EnabledChanged?.Invoke(this, _enabledItem.Checked);

        var openItem = new ToolStripMenuItem("Open configuration…");
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var reconnectItem = new ToolStripMenuItem("Reconnect");
        reconnectItem.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([openItem, _statusItem, new ToolStripSeparator(), _enabledItem, reconnectItem, new ToolStripSeparator(), exitItem]);

        _icon = new NotifyIcon
        {
            Icon = _activeIcon,
            Text = "LightCraft MIDI",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ReconnectRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<bool>? EnabledChanged;

    public void Update(string status, bool connected, bool enabled)
    {
        _statusItem.Text = status;
        if (_enabledItem.Checked != enabled) _enabledItem.Checked = enabled;
        _icon.Icon = connected && enabled ? _activeIcon : _inactiveIcon;
        var tip = "LightCraft MIDI — " + status;
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _activeIcon.Dispose();
        _inactiveIcon.Dispose();
    }
}
