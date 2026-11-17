using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Services;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.Providers;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.App;

/// <summary>The settings window's side of the app: Preview, Save, the Stats API helper, folders.</summary>
public partial class App : ISettingsHost
{
    private SettingsWindow? _settingsWindow;
    private ScenarioFeed? _previewFeed;
    private IPlayerStatsProvider? _previewProvider;
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

    bool ISettingsHost.HasApiKey => !string.IsNullOrEmpty(_settings.ProtectedApiKey);

    bool ISettingsHost.HasRocketLeagueApiKey => !string.IsNullOrEmpty(_settings.ProtectedRocketLeagueApiKey);

    int ISettingsHost.LookupsThisMonth => LookupBudget.UsedThisMonth;

    bool ISettingsHost.RunAtStartup => _startup?.IsEnabled == true;

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
    /// Preview: the demo lobby with test data, shown even though the game is not in front, using the settings as edited
    /// (columns, playlists, position, size). Real Stats API messages are ignored until it stops.
    /// </summary>
    private void StartPreview(OverlaySettings settings)
    {
        if (_coordinator is null || _coordinator.IsPreviewing)
        {
            return;
        }

        _feed?.Stop(); // --demo: the preview replaces the demo lobby
        ApplyLook(settings);
        _previewProvider = new MockStatsProvider();
        _coordinator.BeginPreview(_previewProvider);
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

    /// <summary>Panel (columns, playlists, key caps) and placement; nothing saved, no provider change.</summary>
    private void ApplyLook(OverlaySettings settings)
    {
        _coordinator!.ApplySettings(settings);
        _window!.Configure(() => _tracker!.FindGameWindow(), settings.OffsetX, settings.OffsetY, settings.Scale);
    }

    void ISettingsHost.Save(OverlaySettings settings, ApiKeyEdit trackerGgKey, ApiKeyEdit rocketLeagueApiKey, bool runAtStartup)
    {
        settings.ProtectedApiKey = Apply(trackerGgKey, settings.ProtectedApiKey);
        settings.ProtectedRocketLeagueApiKey = Apply(rocketLeagueApiKey, settings.ProtectedRocketLeagueApiKey);

        var old = _settings;
        _settings = settings;
        _store!.Save(settings);
        _log.LogInformation("Settings saved");

        ApplyLook(settings);
        ApplyHotkeyBindings();

        if (settings.Provider != old.Provider || settings.ProtectedApiKey != old.ProtectedApiKey
            || settings.ProtectedRocketLeagueApiKey != old.ProtectedRocketLeagueApiKey
            || settings.CacheTtlMinutes != old.CacheTtlMinutes || settings.RequestsPerMinute != old.RequestsPerMinute)
        {
            var previous = _provider;
            _provider = CreateProvider();
            _coordinator!.ReplaceProvider(_provider);
            previous?.Dispose();
        }

        _icons!.SetUseDownloads(settings.DownloadRankIcons);
        if (settings.DownloadRankIcons && !old.DownloadRankIcons)
        {
            _icons.PrefetchOfficialIcons();
        }

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

    void ISettingsHost.OpenUrl(string url)
    {
        // Only the two fixed "get a key" links; profile links go through LinkOpener.
        if (url is SettingsViewModel.ApiKeyPortalUrl or SettingsViewModel.RocketLeagueApiUrl)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>A new key is encrypted and replaces the saved one; Clear removes it; otherwise it is kept.</summary>
    private static string? Apply(ApiKeyEdit edit, string? saved) =>
        edit.Clear ? null
        : edit.NewKey is { } key ? ApiKeyProtector.Protect(key) ?? saved
        : saved;

    void ISettingsHost.ClearRankIconCache()
    {
        _icons!.ClearDownloads();
        if (_settings.DownloadRankIcons)
        {
            _icons.PrefetchOfficialIcons();
        }
    }
}
