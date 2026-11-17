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
/// demo feed, lobby coordinator, overlay window, hotkeys and the game-window tracker.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\RLOverlay.SingleInstance";

    private SerilogLoggerFactory? _loggerFactory;
    private Microsoft.Extensions.Logging.ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private Mutex? _mutex;
    private AppArgs _args = new();
    private SettingsStore? _store;
    private OverlaySettings _settings = new();
    private HttpClient? _http;
    private RankIconService? _icons;
    private CachingStatsProvider? _provider;
    private PanelViewModel? _panel;
    private OverlayWindow? _window;
    private HotkeyManager? _hotkeys;
    private GameWindowTracker? _tracker;
    private OverlayCoordinator? _coordinator;
    private ScenarioFeed? _feed;
    private StatsApiClient? _client;
    private readonly CancellationTokenSource _shutdown = new();

    internal ILoggerFactory LoggerFactory => _loggerFactory!;

    private bool HotkeysUngated => _args.UngatedHotkeys || _args.Demo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = AppArgs.Parse(e.Args);
        SetUpLogging();
        _log.LogInformation("RLOverlay {Version} starting (args: {Args})", typeof(App).Assembly.GetName().Version, string.Join(' ', e.Args));
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

        // Only the normal (game) mode is single-instance; demo and screenshot runs may sit next to it.
        if (!_args.Demo && _args.ScreenshotsDir is null)
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
            if (!created)
            {
                _log.LogInformation("Another instance is already running; exiting");
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }
        }

        _store = new SettingsStore(AppPaths.SettingsFile, LoggerFactory.CreateLogger<SettingsStore>());
        _settings = _store.Load();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _icons = new RankIconService([AppPaths.RankOverrideDir, AppPaths.RankAssetsDir], AppPaths.RankCacheDir, _http, LoggerFactory.CreateLogger<RankIconService>());

        BuildOverlay();
        StartDataSource();

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

    private IPlayerStatsProvider CreateInnerProvider(bool mock) =>
        mock
            ? new MockStatsProvider()
            : new TrackerGgProvider(_http!, () => ApiKeyProtector.Unprotect(_settings.ProtectedApiKey), LoggerFactory.CreateLogger<TrackerGgProvider>());

    private CachingStatsProvider CreateProvider()
    {
        var mock = _args.Demo || _args.MockProvider || _settings.Provider == ProviderNames.Mock;
        var options = new CachingOptions
        {
            Ttl = TimeSpan.FromMinutes(_settings.CacheTtlMinutes),
            RequestsPerMinute = _settings.RequestsPerMinute,
            // Mock data is not worth caching on disk (and the demo should show the loading state every time).
            DiskDirectory = mock ? null : AppPaths.ProfileCacheDir,
        };
        _log.LogInformation("Stats provider: {Provider}", mock ? "Mock" : "tracker.gg");
        return new CachingStatsProvider(CreateInnerProvider(mock), options, TimeProvider.System, LoggerFactory.CreateLogger<CachingStatsProvider>());
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

        var links = new LinkOpener(_args.DryRunLinks, LoggerFactory.CreateLogger<LinkOpener>());
        _coordinator = new OverlayCoordinator(_settings, _provider, _panel, _window, links, LoggerFactory.CreateLogger<OverlayCoordinator>());
        panelView.ProfileRequested += _coordinator.OpenProfile;

        _hotkeys = new HotkeyManager(_window.Source!, LoggerFactory.CreateLogger<HotkeyManager>());
        ApplyHotkeyBindings();
        _hotkeys.Pressed += hotkey =>
        {
            if (hotkey == OverlayHotkey.TogglePanel)
            {
                _coordinator.TogglePanel();
            }
            else
            {
                _coordinator.CyclePlaylist();
            }
        };

        _tracker.GameForegroundChanged += _ => UpdateForegroundGate();
        _tracker.ExclusiveFullscreenDetected += () =>
            _log.LogWarning("Rocket League is in exclusive fullscreen; the overlay cannot draw over it. Switch to Borderless or Windowed.");
        _tracker.Start();
        UpdateForegroundGate();
    }

    private void ApplyHotkeyBindings()
    {
        HotkeyGesture.TryParse(_settings.ToggleHotkey, out var toggle);
        HotkeyGesture.TryParse(_settings.CycleHotkey, out var cycle);
        _hotkeys!.SetBindings(toggle, cycle);
    }

    /// <summary>
    /// Hotkeys only while Rocket League is in front (so Ctrl+R still reloads your browser), and the overlay hides while
    /// another window is in front of a running game. Neither applies when the game is not running at all (testing) or
    /// with <c>--ungated-hotkeys</c> / <c>--demo</c>.
    /// </summary>
    private void UpdateForegroundGate()
    {
        if (_tracker is null || _hotkeys is null || _window is null)
        {
            return;
        }

        var foreground = _tracker.IsGameForeground;
        _hotkeys.SetActive(foreground || HotkeysUngated);
        _window.SetSuppressed(!HotkeysUngated && !foreground && _tracker.IsGameRunning);
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

        var port = _args.Port ?? _settings.StatsApiPort ?? 49123;
        _client = new StatsApiClient(new StatsApiClientOptions { Port = port }, LoggerFactory.CreateLogger<StatsApiClient>());
        var dispatcher = Dispatcher;
        _client.MessageReceived += message => dispatcher.BeginInvoke(() => _coordinator!.Handle(message));
        _client.ConnectionStateChanged += state => dispatcher.BeginInvoke(() =>
        {
            _log.LogInformation("Stats API {State} (port {Port})", state, port);
            _coordinator!.OnConnectionChanged(state);
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.RunAsync(_shutdown.Token).ConfigureAwait(false);
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
        _feed?.Dispose();
        _hotkeys?.Dispose();
        _tracker?.Dispose();
        _coordinator?.Dispose();
        _provider?.Dispose();
        _http?.Dispose();
        _window?.Close();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
