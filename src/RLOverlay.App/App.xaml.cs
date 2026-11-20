using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Overlay;
using RLOverlay.App.Services;
using RLOverlay.App.ViewModels;
using RLOverlay.App.Views;
using RLOverlay.Core.Config;
using RLOverlay.Core.Players;
using RLOverlay.Core.StatsApi;
using RLOverlay.Core.Updates;
using Serilog;
using Serilog.Extensions.Logging;

namespace RLOverlay.App;

/// <summary>
/// Application entry and composition root (no DI container): logging, settings, Stats API client or demo feed, lobby
/// coordinator, overlay window with the tracker.gg screen, hotkeys, game-window tracker, tray icon, updater and settings
/// window (the <see cref="ISettingsHost"/> side lives in App.Settings.cs).
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
    private PanelViewModel? _panel;
    private TrackerPanelView? _panelView;
    private FavouritesService? _favourites;
    private PanelViewModel? _favouritesPanel;
    private TrackerPanelView? _favouritesView;
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
    private UpdateService? _updates;
    private DispatcherTimer? _noticeTimer;
    private DateTime? _gameRunningSince;
    private bool _toldFullscreen;
    private bool _toldStatsApi;
    private readonly CancellationTokenSource _shutdown = new();

    internal ILoggerFactory LoggerFactory => _loggerFactory!;

    private bool HotkeysUngated => _args.UngatedHotkeys || _args.Demo;

    static App()
    {
        // The overlay is a layered (per-pixel transparent) window: every animation frame is drawn and copied to the
        // screen in full, which competes with the game. 30 frames a second is plenty for fades and the loading shimmer.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 30 });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = AppArgs.Parse(e.Args);
        if (_args.DataDir is { } dataDir)
        {
            AppPaths.UseRoot(Path.GetFullPath(dataDir));
        }

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

        // Only the normal (game) mode is single-instance; demo, screenshot and test-server (--port) runs may sit next
        // to it.
        if (!_args.Demo && _args.Port is null)
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

        // Rocket League comes first: the overlay (and the browsers it starts, see BrowserTuning) only gets the CPU the game
        // leaves free.
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Could not lower the process priority");
        }

        _store = new SettingsStore(AppPaths.SettingsFile, LoggerFactory.CreateLogger<SettingsStore>());
        _settings = _store.Load();
        _startup = new StartupRegistration(LoggerFactory.CreateLogger<StartupRegistration>());

        BuildOverlay();
        StartDataSource();
        CreateTray();
        StartNotices();
        StartUpdates();
        StartSimulatedDrag();

        if (!_settings.FirstRunCompleted && !_args.Demo)
        {
            _tray!.Notify($"{AppInfo.Name} is running",
                $"It lives here in the notification area. When you load into a match every player's tracker.gg row appears; {_settings.ToggleHotkey} shows or hides it and {_settings.CycleHotkey} switches playlists. Double-click the icon for settings.");
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

    /// <summary><c>--screenshots</c>: renders the settings window and setup off-screen and quits.</summary>
    private async Task RenderScreenshotsAsync(string directory)
    {
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            foreach (var file in ScreenshotRenderer.RenderAll(directory))
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

    private void BuildOverlay()
    {
        // Favourites: the stars next to the names and the Ctrl+F screen (kept in favourites.json).
        var favourites = new FavouritesService(
            new FavouritesStore(AppPaths.FavouritesFile, LoggerFactory.CreateLogger<FavouritesStore>()),
            LoggerFactory.CreateLogger<FavouritesService>())
        {
            AllowSamples = _args.Demo,
        };
        _favourites = favourites;
        _panel = new PanelViewModel();
        _favouritesPanel = new PanelViewModel { IsFavourites = true, FavouriteSource = () => favourites.Players };
        _favouritesPanel.PerPageChosen += OnFavouritesPerPageChosen;

        _tracker = new GameWindowTracker(LoggerFactory.CreateLogger<GameWindowTracker>());
        _links = new LinkOpener(_args.DryRunLinks, LoggerFactory.CreateLogger<LinkOpener>());
        _window = new OverlayWindow { Logger = LoggerFactory.CreateLogger<OverlayWindow>() };
        _panelView = new TrackerPanelView(url => _links.Open(url), LoggerFactory.CreateLogger<TrackerPanelView>(), favourites) { DataContext = _panel };
        _panelView.SetZoom(_settings.Scale);
        _panelView.HintClicked += OnHotkey;
        _favouritesView = new TrackerPanelView(url => _links.Open(url), LoggerFactory.CreateLogger<TrackerPanelView>(), favourites) { DataContext = _favouritesPanel };
        _favouritesView.SetZoom(_settings.Scale);
        _favouritesView.HintClicked += OnHotkey;
        _window.Panel = _panelView;
        _window.Favourites = _favouritesView;
        _window.Strip = new PlaylistStripView { DataContext = _panel.Strip };
        _window.EnsureHandle();
        _window.Configure(ScreenFor(_settings.Screen), _settings.OffsetX, _settings.OffsetY, _settings.Scale);
        _window.PlacementChanged += OnPlacementChanged;

        _coordinator = new OverlayCoordinator(_settings, _panel, _favouritesPanel, _window, LoggerFactory.CreateLogger<OverlayCoordinator>())
        {
            SampleLobby = _args.Demo,
        };
        _coordinator.PlayerSeen += favourites.Seen;
        _panel.IsSample = _args.Demo;

        _hotkeys = new HotkeyManager(_window.Source!, LoggerFactory.CreateLogger<HotkeyManager>());
        ApplyHotkeyBindings();
        _hotkeys.Pressed += OnHotkey;

        _tracker.GameForegroundChanged += _ => UpdateForegroundGate();
        _tracker.ExclusiveFullscreenDetected += OnExclusiveFullscreen;
        _tracker.Start();
        UpdateForegroundGate();
    }

    /// <summary>A shortcut was pressed, or its hint in the screen's header was clicked.</summary>
    private void OnHotkey(OverlayHotkey hotkey)
    {
        switch (hotkey)
        {
            case OverlayHotkey.TogglePanel:
                _coordinator?.TogglePanel();
                break;
            case OverlayHotkey.CyclePlaylist:
                _coordinator?.CyclePlaylist();
                break;
            case OverlayHotkey.Favourites:
                _coordinator?.ToggleFavourites();
                break;
        }
    }

    /// <summary>A page size was picked on the favourites screen: saved straight away (and shown in the settings).</summary>
    private void OnFavouritesPerPageChosen(int perPage)
    {
        var settings = _settings.Clone();
        settings.FavouritesPerPage = perPage;
        _settings = settings.Normalize();
        _store?.Save(_settings);
        (_settingsWindow?.DataContext as SettingsViewModel)?.FavouritesPerPageSaved(_settings.FavouritesPerPage);
        _log.LogInformation("Favourites: {PerPage} per page", _settings.FavouritesPerPage);
    }

    private void ApplyHotkeyBindings()
    {
        HotkeyGesture.TryParse(_settings.ToggleHotkey, out var toggle);
        HotkeyGesture.TryParse(_settings.CycleHotkey, out var cycle);
        HotkeyGesture.TryParse(_settings.FavouritesHotkey, out var favourites);
        _hotkeys!.SetBindings(toggle, cycle, favourites);
    }

    /// <summary>
    /// Hotkeys only while Rocket League is in front (so Ctrl+R still reloads your browser), and the overlay hides while
    /// another window is in front of a running game. Neither applies when the game is not running at all (testing),
    /// with <c>--ungated-hotkeys</c> / <c>--demo</c>, or during the settings Preview (shown over the settings window).
    /// Hotkeys pause while a new shortcut is being recorded.
    /// </summary>
    private void UpdateForegroundGate()
    {
        if (_tracker is null || _hotkeys is null || _window is null)
        {
            return;
        }

        var foreground = _tracker.IsGameForeground;
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
                $"Rocket League is running. Every player's tracker.gg row appears when you load into a match; {_settings.ToggleHotkey} shows or hides it.");
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
        _tray.SettingsRequested += OpenSettings;
        _tray.EnableStatsApiRequested += OpenSettings; // the Game connection section is at the top
        _tray.OpenLogsRequested += () => OpenFolder(AppPaths.LogsDir);
        _tray.QuitRequested += () => Shutdown();
        _tray.MenuOpening += () => _tray.SetToggleLabel(_window?.PanelOn == true, _settings.ToggleHotkey);
        _tray.SetToggleLabel(false, _settings.ToggleHotkey);
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

    /// <summary>
    /// GitHub release checks (see <see cref="UpdateService"/>). Demo and test (--port) runs don't check unless they name
    /// a test feed with --update-feed (which also checks after a few seconds instead of a minute).
    /// </summary>
    /// <summary>
    /// <c>--simulate-drag</c> / <c>--simulate-resize</c> (demo and <c>--port</c> runs): once the demo lobby has filled in,
    /// drag the panel (or its corner) along a fixed path with a made-up cursor and log how it went (see
    /// <see cref="OverlayWindow.SimulateDrag"/>).
    /// </summary>
    private void StartSimulatedDrag()
    {
        if (!(_args.SimulateDrag || _args.SimulateResize) || (!_args.Demo && _args.Port is null) || _window is null)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _window.SimulateDrag(TimeSpan.FromSeconds(3), resize: _args.SimulateResize);
        };
        timer.Start();
    }

    private void StartUpdates()
    {
        Uri? testFeed = _args.UpdateFeed is { } address && Uri.TryCreate(address, UriKind.Absolute, out var parsed) ? parsed : null;
        var testRun = _args.Demo || _args.Port is not null;
        var enabled = !_args.NoUpdateCheck && (!testRun || testFeed is not null);
        var options = new UpdateOptions(testFeed ?? UpdateFeed.LatestRelease, enabled, _args.SetupSandbox, _args.DataDir);
        _updates = new UpdateService(options, () => _settings.UpdateAutomatically, IsQuietMoment,
            (title, text) => _tray?.Notify(title, text), () => Shutdown(), LoggerFactory.CreateLogger<UpdateService>());
        _updates.Start(testFeed is not null ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));
    }

    /// <summary>Nothing going on that an update would interrupt: the game is closed, no Preview, settings closed.</summary>
    private bool IsQuietMoment() => _tracker?.IsGameRunning != true && _coordinator?.IsPreviewing != true && _settingsWindow is null;

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
        _updates?.Dispose();
        _settingsWindow?.Close();
        StopPreviewFeed();
        _feed?.Dispose();
        _hotkeys?.Dispose();
        _tracker?.Dispose();
        _coordinator?.Dispose();
        _tray?.Dispose();
        _panelView?.Dispose();
        _favouritesView?.Dispose();
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
