using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Native;
using RLOverlay.App.Services;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.App;

/// <summary>The settings window's side of the app: Preview, Save, the Stats API helper, folders.</summary>
public partial class App : ISettingsHost
{
    private SettingsWindow? _settingsWindow;
    private ScenarioFeed? _previewFeed;
    private bool _hotkeysSuspended;

    /// <summary>Opens the settings window, or brings it to the front if it is already open.</summary>
    internal void OpenSettings()
    {
        if (_coordinator is null)
        {
            return;
        }

        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(new SettingsViewModel(this));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    OverlaySettings ISettingsHost.Current => _settings;

    bool ISettingsHost.RunAtStartup => _startup?.IsEnabled == true;

    IReadOnlyList<ScreenInfo> ISettingsHost.Screens() => Screens.All(withNames: true);

    string ISettingsHost.ConnectionStatus
    {
        get
        {
            var text = ConnectionText();
            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
    }

    void ISettingsHost.SetPreview(bool on, OverlaySettings settings)
    {
        if (on)
        {
            StartPreview(settings);
        }
        else
        {
            StopPreview();
        }
    }

    void ISettingsHost.PreviewSettings(OverlaySettings settings)
    {
        if (_coordinator?.IsPreviewing == true)
        {
            ApplyLook(settings);
        }
    }

    void ISettingsHost.SuspendHotkeys(bool suspended)
    {
        _hotkeysSuspended = suspended;
        UpdateForegroundGate();
    }

    /// <summary>
    /// Preview: the demo lobby (made-up players, so placeholders where tracker.gg's rows go), shown even though the game
    /// is not in front, using the settings as edited (stats, playlists, position, size). Real Stats API messages are
    /// ignored until it stops.
    /// </summary>
    private void StartPreview(OverlaySettings settings)
    {
        if (_coordinator is null || _coordinator.IsPreviewing)
        {
            return;
        }

        _feed?.Stop(); // --demo: the preview replaces the demo lobby
        ApplyLook(settings);
        _coordinator.BeginPreview();
        UpdateForegroundGate();
        _previewFeed = new ScenarioFeed(ScenarioFeed.LoadEmbedded("demo_lobby.json"), _coordinator.Handle, _log);
        _previewFeed.Start();
        _log.LogInformation("Preview started");
    }

    private void StopPreview()
    {
        if (_coordinator?.IsPreviewing != true)
        {
            return;
        }

        StopPreviewFeed();
        _coordinator.EndPreview();
        ApplyLook(_settings);
        UpdateForegroundGate();
        _log.LogInformation("Preview stopped");
    }

    private void StopPreviewFeed()
    {
        _previewFeed?.Dispose();
        _previewFeed = null;
    }

    /// <summary>The screen (stats, playlists, key caps) and its placement and size; nothing saved.</summary>
    private void ApplyLook(OverlaySettings settings)
    {
        _coordinator!.ApplySettings(settings);
        _panelView!.SetZoom(Math.Clamp(settings.Scale, OverlaySettings.MinScale, OverlaySettings.MaxScale));
        _window!.Configure(ScreenFor(settings.Screen), settings.OffsetX, settings.OffsetY, settings.Scale);
    }

    /// <summary>
    /// The overlay's screen for a Screen setting: that screen while it is connected, else the one Rocket League is on,
    /// else the main screen (see <see cref="ScreenChoice.Pick"/>).
    /// </summary>
    private Func<PixelRect> ScreenFor(string? chosen) => () => ScreenChoice.Pick(
        chosen,
        chosen is null ? [] : Screens.All(),
        () =>
        {
            var game = _tracker?.FindGameWindow() ?? IntPtr.Zero;
            return game != IntPtr.Zero ? MonitorInfo.BoundsFor(game) : null;
        },
        MonitorInfo.Primary());

    /// <summary>
    /// The screen was dragged or resized. During the Preview that goes into the settings being edited (Save keeps it,
    /// Cancel puts it back); otherwise it is saved straight away.
    /// </summary>
    private void OnPlacementChanged(double offsetX, double offsetY, double scale)
    {
        var editor = _settingsWindow?.DataContext as SettingsViewModel;
        if (_coordinator?.IsPreviewing == true && editor is not null)
        {
            editor.SetPlacement(offsetX, offsetY, scale);
            return;
        }

        var settings = _settings.Clone();
        settings.OffsetX = Math.Round(offsetX);
        settings.OffsetY = Math.Round(offsetY);
        settings.Scale = Math.Round(scale, 2);
        _settings = settings.Normalize();
        _store!.Save(_settings);
        _panelView!.SetZoom(_settings.Scale);
        _window!.Configure(ScreenFor(_settings.Screen), _settings.OffsetX, _settings.OffsetY, _settings.Scale);
        editor?.PlacementSaved(_settings.OffsetX, _settings.OffsetY, _settings.Scale);
        _log.LogInformation("Screen moved to {X}, {Y} from the top-right corner at {Size:0} %", _settings.OffsetX, _settings.OffsetY, _settings.Scale * 100);
    }

    void ISettingsHost.Save(OverlaySettings settings, bool runAtStartup)
    {
        var old = _settings;
        _settings = settings;
        _store!.Save(settings);
        _log.LogInformation("Settings saved (screen: {Screen})", settings.Screen ?? "Rocket League's");

        ApplyLook(settings);
        ApplyHotkeyBindings();

        if (_active && _args.Port is null && settings.StatsApiPort != old.StatsApiPort)
        {
            _statsState = StatsConnectionState.Disconnected;
            StartStatsApiClient();
        }

        if (_startup is not null && runAtStartup != _startup.IsEnabled)
        {
            _startup.Set(runAtStartup);
        }

        _tray?.SetToggleLabel(_window?.PanelOn == true, settings.ToggleHotkey);
        UpdateTrayStatus();
    }

    UpdateState ISettingsHost.UpdateState => _updates?.State ?? UpdateState.Off;

    event Action? ISettingsHost.UpdateStateChanged
    {
        add
        {
            if (_updates is not null)
            {
                _updates.StateChanged += value;
            }
        }

        remove
        {
            if (_updates is not null)
            {
                _updates.StateChanged -= value;
            }
        }
    }

    Task ISettingsHost.CheckForUpdatesAsync() => _updates?.CheckAsync() ?? Task.CompletedTask;

    void ISettingsHost.InstallUpdate()
    {
        _settingsWindow?.Close(); // its edits are saved or dropped like any close; the overlay restarts in the notification area
        _updates?.InstallNow();
    }

    (string Summary, IReadOnlyList<StatsApiFileStatus> Files) ISettingsHost.StatsApiStatus()
    {
        var installs = _setup!.FindInstalls();
        var files = _setup.Status(installs);
        return (StatsApiSetup.Describe(installs, files), files);
    }

    async Task<StatsApiEnableOutcome> ISettingsHost.EnableStatsApiAsync()
    {
        var installs = _setup!.FindInstalls();
        var files = _setup.Status(installs);
        var (edit, create) = RocketLeagueLocator.PlanIniEdit(installs, StatsApiSetup.DocumentsDir);
        var needsChange = edit.Any(p => files.FirstOrDefault(f => f.Path == p)?.Values?.IsEnabled != true) || create is not null;
        if (needsChange)
        {
            var list = string.Join(Environment.NewLine, edit.Append(create).OfType<string>().Select(p => "• " + p));
            var answer = MessageBox.Show(_settingsWindow!,
                "This turns on Rocket League's Stats API (PacketSendRate and Port in [TAGame.MatchStatsExporter_TA]) in:" +
                Environment.NewLine + Environment.NewLine + list + Environment.NewLine + Environment.NewLine +
                "Each existing file is backed up next to it first (.bak-date). Nothing else is changed. Restart the game afterwards." +
                Environment.NewLine + Environment.NewLine + "Continue?",
                "Enable Stats API", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                return new StatsApiEnableOutcome(false, false, false, "Nothing was changed.", []);
            }
        }

        var outcome = await _setup.EnableAsync();
        _gameRunningSince = null;
        if (outcome.AnyChange && _active && _args.Port is null && _settings.StatsApiPort is null
            && _setup.ConfiguredPort() is { } port && port != _port)
        {
            StartStatsApiClient();
        }

        return outcome;
    }

    void ISettingsHost.OpenFolder(string path) => OpenFolder(path);

    internal void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning("Could not open {Path}: {Error}", path, ex.Message);
        }
    }
}
