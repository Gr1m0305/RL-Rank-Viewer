using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Overlay;
using RLOverlay.App.Services;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.Providers;
using RLOverlay.Core.StatsApi;
using Serilog;
using Serilog.Extensions.Logging;

namespace RLOverlay.App;

/// <summary>
/// Application entry and composition root (no DI container): logging, settings, stats provider, Stats API client or
/// demo feed, lobby coordinator, overlay window, hotkeys, game-window tracker, tray icon and settings window
/// (the <see cref="ISettingsHost"/> side lives in App.Settings.cs).
/// </summary>
public partial class App : Application
{
    private const int DefaultPort = StatsApiIni.DefaultPort;

    private SerilogLoggerFactory? _loggerFactory;
    private Microsoft.Extensions.Logging.ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private Mutex? _mutex;
    private EventWaitHandle? _showSettingsSignal;
    private RegisteredWaitHandle? _showSettingsWait;
    private EventWaitHandle? _quitSignal;
    private RegisteredWaitHandle? _quitWait;
    private AppArgs _args = new();
    private SettingsStore? _store;
    private OverlaySettings _settings = new();
    private HttpClient? _http;
    private RankIconService? _icons;
    private CachingStatsProvider? _provider;
    private LookupBudget? _lookupBudget;
    private PanelViewModel? _panel;
    private OverlayWindow? _window;
    private HotkeyManager? _hotkeys;
    private GameWindowTracker? _tracker;
    private OverlayCoordinator? _coordinator;
    private LinkOpener? _links;
    private ScenarioFeed? _feed;
    private StatsApiClient? _client;
    private CancellationTokenSource? _clientCts;
    private StatsConnectionState _statsState = StatsConnectionState.Disconnected;
    private int _port = DefaultPort;

    // Standby vs active: the Stats API client only runs while Rocket League is running (or with --always-connect).
    private bool _active;
    private StatsApiSetup? _setup;
    private StartupRegistration? _startup;
    private TrayIconService? _tray;
    private DispatcherTimer? _noticeTimer;
    private DateTime? _gameRunningSince;
    private bool _toldFullscreen;
    private bool _toldStatsApi;
    private readonly CancellationTokenSource _shutdown = new();

    internal ILoggerFactory LoggerFactory => _loggerFactory!;

    private bool HotkeysUngated => _args.UngatedHotkeys || _args.Demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = AppArgs.Parse(e.Args);
        SetUpLogging();
        _log.LogInformation("{App} {Version} starting (args: {Args})", AppInfo.Name, AppInfo.Version, string.Join(' ', e.Args));
        foreach (var unknown in _args.Unknown)
        {
            _log.LogWarning("Unknown argument {Arg}", unknown);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => _log.LogCritical(ev.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            _log.LogError(ev.Exception, "Unobserved task exception");
            ev.SetObserved();
        };

        _setup = new StatsApiSetup(LoggerFactory.CreateLogger<StatsApiSetup>());

        // Relaunched elevated just to edit the game's ini (see StatsApiSetup.EnableAsync).
        if (_args.EnableStatsApi)
        {
            Shutdown(_setup.RunElevatedEdit());
            return;
        }

        if (_args.ScreenshotsDir is { } shotsDir)
        {
            _ = RenderScreenshotsAsync(Path.GetFullPath(shotsDir));
            return;
        }

        // Installer / uninstaller (the setup exe is this same exe under a "...Setup.exe" name).
        if (_args.Setup || _args.Uninstall || IsSetupFileName())
        {
            RunSetup();
            return;
        }

        // Only the normal (game) mode is single-instance; demo and screenshot runs may sit next to it.
        if (!_args.Demo)
        {
            _mutex = new Mutex(initiallyOwned: true, InstanceSignals.MutexName, out var created);
            if (!created)
            {
                _log.LogInformation("Another instance is already running; asking it to open its settings");
                SignalRunningInstance();
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            ListenForSecondLaunch();
        }

        _store = new SettingsStore(AppPaths.SettingsFile, LoggerFactory.CreateLogger<SettingsStore>());
        _settings = _store.Load();
        _startup = new StartupRegistration(LoggerFactory.CreateLogger<StartupRegistration>());

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _icons = new RankIconService([AppPaths.RankOverrideDir, AppPaths.RankAssetsDir], AppPaths.RankCacheDir, _http, LoggerFactory.CreateLogger<RankIconService>());
        _icons.SetUseDownloads(_settings.DownloadRankIcons);

        BuildOverlay();
        if (_settings.DownloadRankIcons)
        {
            _icons.PrefetchOfficialIcons();
        }

        StartDataSource();
        CreateTray();
        StartNotices();

        if (!_settings.FirstRunCompleted && !_args.Demo)
        {
            _tray!.Notify($"{AppInfo.Name} is running",
                $"It lives here in the notification area. In a match, {_settings.ToggleHotkey} shows or hides the panel and {_settings.CycleHotkey} switches playlists. Double-click the icon for settings.");
            _settings.FirstRunCompleted = true;
            _store.Save(_settings);
        }

        if (_args.OpenSettings)
        {
            OpenSettings();
        }

        if (_args.ExitAfterSeconds is { } seconds)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _log.LogInformation("--exit-after elapsed; quitting");
                Shutdown();
            };
            timer.Start();
        }
    }

    /// <summary>
    /// <c>--screenshots</c>: renders the panel states off-screen and quits. With <c>--official-icons</c> the official
    /// rank icons are downloaded to the cache first (awaited here, so the UI thread stays free to receive them).
    /// </summary>
    private async Task RenderScreenshotsAsync(string directory)
    {
        try
        {
            RankIconService? icons = null;
            if (_args.OfficialIcons)
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                icons = new RankIconService([], AppPaths.RankCacheDir, _http, LoggerFactory.CreateLogger<RankIconService>());
                await icons.EnsureOfficialIconsAsync(TimeSpan.FromSeconds(60));
            }

            foreach (var file in ScreenshotRenderer.RenderAll(directory, LoggerFactory, icons))
            {
                _log.LogInformation("Screenshot written: {File}", file);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Screenshot rendering failed");
        }

        Shutdown();
    }

    private void SetUpLogging()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "rloverlay-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        _loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
        _log = _loggerFactory.CreateLogger("RLOverlay");
    }

    private void SignalRunningInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(InstanceSignals.ShowSettingsEventName);
            signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            _log.LogInformation("Could not reach the running instance: {Error}", ex.Message);
        }
    }

    private void ListenForSecondLaunch()
    {
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignals.ShowSettingsEventName);
        _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(
            _showSettingsSignal, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        // Setup (update) and uninstall ask the running copy to quit before replacing or removing the exe.
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceSignals.QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _log.LogInformation("Asked to quit by setup");
            Shutdown();
        }), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The provider the settings (or the launch flags) ask for: Mock, RocketLeagueApi or TrackerGg.</summary>
    private string EffectiveProvider =>
        _args.Demo || _args.MockProvider ? ProviderNames.Mock : _settings.Provider;

    /// <summary>Counts paid Rocket League API requests per month; one instance so the count survives provider changes.</summary>
    internal LookupBudget LookupBudget =>
        _lookupBudget ??= new LookupBudget(AppPaths.LookupUsageFile, () => _settings.MaxLookupsPerMonth);

    private IPlayerStatsProvider CreateInnerProvider(string provider) => provider switch
    {
        ProviderNames.Mock => new MockStatsProvider(),
        ProviderNames.RocketLeagueApi => new RocketLeagueApiProvider(
            _http!,
            () => ApiKeyProtector.Unprotect(_settings.ProtectedRocketLeagueApiKey),
            LookupBudget,
            LoggerFactory.CreateLogger<RocketLeagueApiProvider>()),
        _ => new TrackerGgProvider(_http!, () => ApiKeyProtector.Unprotect(_settings.ProtectedApiKey), LoggerFactory.CreateLogger<TrackerGgProvider>()),
    };

    private CachingStatsProvider CreateProvider()
    {
        var provider = EffectiveProvider;
        var options = new CachingOptions
        {
            Ttl = TimeSpan.FromMinutes(_settings.CacheTtlMinutes),
            RequestsPerMinute = _settings.RequestsPerMinute,
            // Mock data is not worth caching on disk (and the demo should show the loading state every time).
            // Each real source keeps its own disk cache so switching never shows one source's numbers as another's.
            DiskDirectory = provider switch
            {
                ProviderNames.Mock => null,
                ProviderNames.RocketLeagueApi => Path.Combine(AppPaths.ProfileCacheDir, "rocket-league-api"),
                _ => AppPaths.ProfileCacheDir,
            },
        };
        _log.LogInformation("Stats provider: {Provider}", provider);
        return new CachingStatsProvider(CreateInnerProvider(provider), options, TimeProvider.System, LoggerFactory.CreateLogger<CachingStatsProvider>());
    }

    private void BuildOverlay()
    {
        _provider = CreateProvider();
        _panel = new PanelViewModel(_icons!);
        _icons!.IconUpdated += tier => _panel.OnIconUpdated(tier);

        _tracker = new GameWindowTracker(LoggerFactory.CreateLogger<GameWindowTracker>());
        _window = new OverlayWindow();
        var panelView = new PanelView { DataContext = _panel };
        _window.Panel = panelView;
        _window.Strip = new PlaylistStripView { DataContext = _panel.Strip };
        _window.EnsureHandle();
        _window.Configure(() => _tracker.FindGameWindow(), _settings.OffsetX, _settings.OffsetY, _settings.Scale);

        _links = new LinkOpener(_args.DryRunLinks, LoggerFactory.CreateLogger<LinkOpener>());
        _coordinator = new OverlayCoordinator(_settings, _provider, _panel, _window, _links, LoggerFactory.CreateLogger<OverlayCoordinator>());
        panelView.ProfileRequested += _coordinator.OpenProfile;
        WireTrackerWindow();

        _hotkeys = new HotkeyManager(_window.Source!, LoggerFactory.CreateLogger<HotkeyManager>());
        ApplyHotkeyBindings();
        _hotkeys.Pressed += hotkey =>
        {
            switch (hotkey)
            {
                case OverlayHotkey.TogglePanel:
                    _coordinator.TogglePanel();
                    break;
                case OverlayHotkey.CyclePlaylist:
                    _coordinator.CyclePlaylist();
                    break;
                case OverlayHotkey.TrackerLobby:
                    ToggleTrackerWindow();
                    break;
            }
        };

        _tracker.GameForegroundChanged += _ => UpdateForegroundGate();
        _tracker.ExclusiveFullscreenDetected += OnExclusiveFullscreen;
        _tracker.Start();
        UpdateForegroundGate();
    }

    private void ApplyHotkeyBindings()
    {
        HotkeyGesture.TryParse(_settings.ToggleHotkey, out var toggle);
        HotkeyGesture.TryParse(_settings.CycleHotkey, out var cycle);
        HotkeyGesture.TryParse(_settings.TrackerHotkey, out var tracker);
        _hotkeys!.SetBindings(toggle, cycle, tracker);
    }

    /// <summary>
    /// Hotkeys only while Rocket League is in front (so Ctrl+R still reloads your browser), and the overlay hides while
    /// another window is in front of a running game. Neither applies when the game is not running at all (testing),
    /// with <c>--ungated-hotkeys</c> / <c>--demo</c>, or during the settings Preview (shown over the settings window).
    /// Hotkeys pause while a new shortcut is being recorded. The tracker.gg lobby window counts as the game here, so
    /// its shortcuts (and Ctrl+G) keep working after clicking it.
    /// </summary>
    private void UpdateForegroundGate()
    {
        if (_tracker is null || _hotkeys is null || _window is null)
        {
            return;
        }

        var foreground = _tracker.IsGameForeground || TrackerWindowInFront;
        var previewing = _coordinator?.IsPreviewing == true;
        _hotkeys.SetActive(!_hotkeysSuspended && (foreground || HotkeysUngated));
        _window.SetSuppressed(!previewing && !HotkeysUngated && !foreground && _tracker.IsGameRunning);
    }

    private void StartDataSource()
    {
        if (_args.Demo)
        {
            var scenario = ScenarioFeed.LoadEmbedded("demo_lobby.json");
            _feed = new ScenarioFeed(scenario, _coordinator!.Handle, _log);
            _feed.Start();
            _log.LogInformation("Demo: playing '{Scenario}'", scenario.Name);
            return;
        }

        // Standby until Rocket League starts: no connection attempts while the game is closed. The tracker watches the
        // process list (never the game process itself) and wakes the overlay up when the game appears.
        _tracker!.GameRunningChanged += OnGameRunningChanged;
        if (_args.AlwaysConnect || _tracker.IsGameRunning)
        {
            Activate(announce: false);
        }
        else
        {
            EnterStandby();
        }
    }

    private void OnGameRunningChanged(bool running)
    {
        if (running)
        {
            Activate(announce: true);
        }
        else if (!_args.AlwaysConnect)
        {
            EnterStandby();
        }
    }

    /// <summary>Rocket League is running: connect to its Stats API (the client keeps retrying until the game opens it).</summary>
    private void Activate(bool announce)
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _log.LogInformation(_args.AlwaysConnect ? "Connecting to the Stats API (--always-connect)" : "Rocket League is running: overlay on");
        StartStatsApiClient();
        UpdateTrayStatus();
        if (announce && _settings.NotifyWhenGameStarts)
        {
            _tray?.Notify($"{AppInfo.Name} is on",
                $"Rocket League is running. The panel appears when you load into a match; {_settings.ToggleHotkey} shows or hides it.");
        }
    }

    /// <summary>Rocket League closed (or isn't running yet): stop the client, clear the lobby, wait for the game.</summary>
    private void EnterStandby()
    {
        var wasActive = _active;
        _active = false;
        StopStatsApiClient();
        _statsState = StatsConnectionState.Disconnected;
        _gameRunningSince = null;
        if (_coordinator?.IsPreviewing != true)
        {
            _coordinator?.OnConnectionChanged(StatsConnectionState.Disconnected);
        }

        _log.LogInformation(wasActive ? "Rocket League closed: standby until it starts again" : "Standby: waiting for Rocket League to start");
        UpdateTrayStatus();
    }

    /// <summary>Port: command line, then settings, then the game's ini, then 49123.</summary>
    private int ResolvePort() => _args.Port ?? _settings.StatsApiPort ?? _setup!.ConfiguredPort() ?? DefaultPort;

    private void StopStatsApiClient()
    {
        _clientCts?.Cancel();
        _clientCts?.Dispose();
        _clientCts = null;
        _client = null;
    }

    private void StartStatsApiClient()
    {
        StopStatsApiClient();
        _clientCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var token = _clientCts.Token;

        _port = ResolvePort();
        var client = new StatsApiClient(new StatsApiClientOptions { Port = _port }, LoggerFactory.CreateLogger<StatsApiClient>());
        _client = client;
        var dispatcher = Dispatcher;
        var port = _port;
        client.MessageReceived += message => dispatcher.BeginInvoke(() =>
        {
            // The settings Preview owns the panel while it runs; the real lobby resumes with the next update.
            if (!token.IsCancellationRequested && _coordinator?.IsPreviewing != true)
            {
                _coordinator!.Handle(message);
            }
        });
        client.ConnectionStateChanged += state => dispatcher.BeginInvoke(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Retries while the game is closed flip Connecting/Disconnected every few seconds: log only real changes.
            if (state == StatsConnectionState.Connected || _statsState == StatsConnectionState.Connected)
            {
                _log.LogInformation("Stats API {State} (port {Port})", state, port);
            }

            _statsState = state;
            UpdateTrayStatus();
            if (_coordinator?.IsPreviewing != true)
            {
                _coordinator!.OnConnectionChanged(state);
            }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await client.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Stats API client stopped");
            }
        });
    }

    private void CreateTray()
    {
        _tray = new TrayIconService();
        _tray.TogglePanelRequested += () => _coordinator?.TogglePanel();
        _tray.TrackerLobbyRequested += ToggleTrackerWindow;
        _tray.SettingsRequested += OpenSettings;
        _tray.EnableStatsApiRequested += OpenSettings; // the Game connection section is at the top
        _tray.OpenLogsRequested += () => OpenFolder(AppPaths.LogsDir);
        _tray.QuitRequested += () => Shutdown();
        _tray.MenuOpening += () =>
        {
            _tray.SetToggleLabel(_window?.PanelOn == true, _settings.ToggleHotkey);
            _tray.SetTrackerLabel(_trackerWindow?.IsVisible == true, _settings.TrackerHotkey);
        };
        _tray.SetToggleLabel(false, _settings.ToggleHotkey);
        _tray.SetTrackerLabel(false, _settings.TrackerHotkey);
        UpdateTrayStatus();
        _tray.Show();
    }

    private void UpdateTrayStatus() => _tray?.SetStatus(ConnectionText());

    private string ConnectionText() =>
        _args.Demo ? "demo mode"
        : !_active ? "standby: switches on when Rocket League starts"
        : _statsState == StatsConnectionState.Connected ? $"connected to Rocket League (port {_port})"
        : _tracker?.IsGameRunning == true ? $"waiting for the Stats API (port {_port})"
        : $"waiting for Rocket League (port {_port})";

    /// <summary>One-off tips: exclusive fullscreen, and a running game whose Stats API never answers.</summary>
    private void StartNotices()
    {
        if (_args.Demo)
        {
            return;
        }

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) => CheckStatsApiNotice();
        _noticeTimer.Start();
    }

    private void CheckStatsApiNotice()
    {
        if (_toldStatsApi || _tracker is null)
        {
            return;
        }

        if (!_tracker.IsGameRunning || _statsState == StatsConnectionState.Connected)
        {
            _gameRunningSince = null;
            UpdateTrayStatus();
            return;
        }

        _gameRunningSince ??= DateTime.UtcNow;
        UpdateTrayStatus();
        if (DateTime.UtcNow - _gameRunningSince < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _toldStatsApi = true;
        var installs = _setup!.FindInstalls();
        var enabled = _setup.Status(installs).FirstOrDefault(f => f.Values?.HasSection == true)?.Values?.IsEnabled == true;
        if (enabled)
        {
            _tray?.Notify("Can't reach the Stats API",
                $"Rocket League is running, but nothing answers on port {_port}. If you enabled the Stats API while the game was open, restart the game.", warning: true);
        }
        else
        {
            _tray?.Notify("Rocket League's Stats API is off",
                "The overlay needs it to see who is in your match. Right-click this icon → Enable Stats API, then restart the game.", warning: true);
        }
    }

    private void OnExclusiveFullscreen()
    {
        _log.LogWarning("Rocket League is in exclusive fullscreen; the overlay cannot draw over it. Switch to Borderless or Windowed.");
        if (!_toldFullscreen)
        {
            _toldFullscreen = true;
            _tray?.Notify("Switch Rocket League to Borderless",
                "The overlay can't draw over exclusive fullscreen. In the game: Settings → Video → Display Mode → Borderless (or Windowed).", warning: true);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An overlay must not crash in the middle of a match: log and keep going.
        _log.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log.LogInformation("Exiting");
        _shutdown.Cancel();
        _noticeTimer?.Stop();
        _settingsWindow?.Close();
        _trackerWindow?.CloseForGood();
        StopPreviewFeed();
        _feed?.Dispose();
        _hotkeys?.Dispose();
        _tracker?.Dispose();
        _coordinator?.Dispose();
        _provider?.Dispose();
        (_previewProvider as IDisposable)?.Dispose();
        _http?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        _showSettingsWait?.Unregister(null);
        _showSettingsSignal?.Dispose();
        _quitWait?.Unregister(null);
        _quitSignal?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
