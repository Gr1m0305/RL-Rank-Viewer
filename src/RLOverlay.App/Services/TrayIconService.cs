using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RLOverlay.App.Services;

/// <summary>
/// The notification-area icon (WinForms <c>NotifyIcon</c>): show/hide the panel, settings, the Stats API helper, logs,
/// quit; double-click opens settings. Also shows the one-off balloon tips.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public static readonly Uri IconUri = new("pack://application:,,,/RLOverlay;component/Assets/app.ico");

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Drawing.Icon _image;

    public TrayIconService()
    {
        using (var stream = Application.GetResourceStream(IconUri)!.Stream)
        {
            _image = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _toggle = new Forms.ToolStripMenuItem("Show panel", null, (_, _) => TogglePanelRequested?.Invoke());
        var settings = new Forms.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke())
        {
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold),
        };
        menu.Items.Add(_toggle);
        menu.Items.Add(settings);
        menu.Items.Add(new Forms.ToolStripMenuItem("Enable Stats API…", null, (_, _) => EnableStatsApiRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Open logs folder", null, (_, _) => OpenLogsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit", null, (_, _) => QuitRequested?.Invoke()));
        menu.Opening += (_, _) => MenuOpening?.Invoke();

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "RL Overlay", ContextMenuStrip = menu };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                SettingsRequested?.Invoke();
            }
        };
    }

    public event Action? TogglePanelRequested;

    public event Action? SettingsRequested;

    public event Action? EnableStatsApiRequested;

    public event Action? OpenLogsRequested;

    public event Action? QuitRequested;

    /// <summary>Raised just before the menu opens, so its labels can be refreshed.</summary>
    public event Action? MenuOpening;

    public void Show() => _icon.Visible = true;

    /// <summary>Updates the first menu item, e.g. "Hide panel (Ctrl+R)".</summary>
    public void SetToggleLabel(bool panelShown, string hotkey) =>
        _toggle.Text = $"{(panelShown ? "Hide" : "Show")} panel ({hotkey})";

    /// <summary>The hover text (Windows allows up to 127 characters).</summary>
    public void SetStatus(string status)
    {
        var text = $"RL Overlay · {status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }
}
