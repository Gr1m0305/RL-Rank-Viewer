using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RLOverlay.App.Services;
using RLOverlay.Core.Config;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>What the settings window needs from the app (implemented by <c>App</c>).</summary>
public interface ISettingsHost
{
    OverlaySettings Current { get; }

    bool RunAtStartup { get; }

    /// <summary>"Connected to Rocket League" / "Waiting for Rocket League" / "Demo".</summary>
    string ConnectionStatus { get; }

    /// <summary>The screens connected now, for the Screen choice.</summary>
    IReadOnlyList<ScreenInfo> Screens();

    /// <summary>Starts or stops the on-screen Preview (made-up lobby) using these settings.</summary>
    void SetPreview(bool on, OverlaySettings settings);

    /// <summary>Applies look and placement live while the Preview runs.</summary>
    void PreviewSettings(OverlaySettings settings);

    /// <summary>Global hotkeys off while a new shortcut is being recorded.</summary>
    void SuspendHotkeys(bool suspended);

    void Save(OverlaySettings settings, bool runAtStartup);

    (string Summary, IReadOnlyList<StatsApiFileStatus> Files) StatsApiStatus();

    Task<StatsApiEnableOutcome> EnableStatsApiAsync();

    void OpenFolder(string path);

    /// <summary>What the update checker last found.</summary>
    UpdateState UpdateState { get; }

    event Action? UpdateStateChanged;

    Task CheckForUpdatesAsync();

    /// <summary>Installs the downloaded update now (closes the settings window; the overlay restarts).</summary>
    void InstallUpdate();
}

public sealed partial class PlaylistOption(Playlist playlist, string label, bool isEnabled) : ObservableObject
{
    public Playlist Playlist { get; } = playlist;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isEnabled = isEnabled;
}

public sealed partial class ColumnOption(string key, string label, bool isOn) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isOn = isOn;
}

/// <summary>One entry of the Screen choice: a device name (null = Rocket League's screen) and its label.</summary>
public sealed record ScreenOption(string? DeviceName, string Label);

/// <summary>One row of the "Game connection" file list.</summary>
public sealed record StatsApiFileRow(string Path, string State, bool IsOn);

/// <summary>
/// The settings window. Edits a copy of the settings; nothing changes until Save (except the live Preview, which is
/// rolled back on Cancel).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] LiveProperties =
    [
        nameof(OffsetX), nameof(OffsetY), nameof(ScalePercent), nameof(ToggleHotkey), nameof(CycleHotkey), nameof(FavouritesHotkey),
        nameof(DefaultPlaylist), nameof(SelectedScreen), nameof(KeepOpenAtKickoff), nameof(FavouritesPerPage),
    ];

    /// <summary>The first Screen entry, and the default.</summary>
    public const string GameScreenLabel = "Rocket League's screen";

    private readonly ISettingsHost _host;
    private readonly OverlaySettings _original;
    private bool _suppressLive;

    public SettingsViewModel(ISettingsHost host)
    {
        _host = host;
        _original = host.Current.Clone();

        Playlists = new ObservableCollection<PlaylistOption>(
            PlaylistCatalog.All.Select(p => new PlaylistOption(p.Playlist, p.SettingsLabel, _original.EnabledPlaylists.Contains(p.Playlist))));
        foreach (var option in Playlists)
        {
            option.PropertyChanged += OnPlaylistToggled;
        }

        var c = _original.Columns;
        Columns =
        [
            new("Wins", "Wins", c.Wins), new("Streak", "Streak", c.Streak), new("Peak", "Peak MMR", c.Peak),
            new("AllTimePeak", "All-time peak", c.AllTimePeak), new("Goals", "Goals", c.Goals), new("Shots", "Shots", c.Shots), new("Assists", "Assists", c.Assists),
            new("Saves", "Saves", c.Saves), new("Mvps", "MVPs", c.Mvps),
        ];
        foreach (var column in Columns)
        {
            column.PropertyChanged += (_, _) => LiveApply();
        }

        RebuildDefaultChoices();
        _defaultPlaylist = DefaultChoices.FirstOrDefault(o => o.Playlist == _original.DefaultPlaylist) ?? DefaultChoices.FirstOrDefault();
        _followMatchPlaylist = _original.FollowMatchPlaylist;
        _keepOpenAtKickoff = _original.KeepOpenAtKickoff;
        _toggleHotkey = Display(_original.ToggleHotkey, "Ctrl+R");
        _cycleHotkey = Display(_original.CycleHotkey, "Ctrl+G");
        _favouritesHotkey = Display(_original.FavouritesHotkey, "Ctrl+F");
        _favouritesPerPage = FavouritePages.NormalizeSize(_original.FavouritesPerPage);
        _offsetX = _original.OffsetX;
        _offsetY = _original.OffsetY;
        _scalePercent = Math.Round(_original.Scale * 100);
        Screens = BuildScreenOptions(host.Screens(), _original.Screen);
        _selectedScreen = Screens.FirstOrDefault(o => string.Equals(o.DeviceName, _original.Screen, StringComparison.OrdinalIgnoreCase)) ?? Screens[0];
        _notifyWhenGameStarts = _original.NotifyWhenGameStarts;
        _updateAutomatically = _original.UpdateAutomatically;
        _runAtStartup = host.RunAtStartup;
        ShowUpdateState();
        host.UpdateStateChanged += OnUpdateStateChanged;
        _portText = _original.StatsApiPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        RefreshStatsApi();

        PropertyChanged += (_, e) =>
        {
            if (LiveProperties.Contains(e.PropertyName))
            {
                LiveApply();
            }
        };
    }

    /// <summary>Raised when the window should close (true = saved).</summary>
    public event Action<bool>? CloseRequested;

    public ObservableCollection<PlaylistOption> Playlists { get; }

    public ObservableCollection<PlaylistOption> DefaultChoices { get; } = [];

    public IReadOnlyList<ColumnOption> Columns { get; }

    public ObservableCollection<StatsApiFileRow> StatsApiFiles { get; } = [];

    /// <summary>Rocket League's screen first, then every connected screen (and the saved one if it is unplugged).</summary>
    public IReadOnlyList<ScreenOption> Screens { get; }

    [ObservableProperty]
    private ScreenOption? _selectedScreen;

    public double MinScalePercent => OverlaySettings.MinScale * 100;

    public double MaxScalePercent => OverlaySettings.MaxScale * 100;

    [ObservableProperty]
    private PlaylistOption? _defaultPlaylist;

    [ObservableProperty]
    private bool _followMatchPlaylist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepOpenHint))]
    private string _toggleHotkey;

    [ObservableProperty]
    private bool _keepOpenAtKickoff;

    /// <summary>Under "Keep the lobby open when the match starts", with the show / hide shortcut as edited.</summary>
    public string KeepOpenHint =>
        $"It normally hides at kickoff. With this on it stays until you press {ToggleHotkey} or leave the match, handy on a second screen.";

    [ObservableProperty]
    private string _cycleHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouritesHint))]
    private string _favouritesHotkey;

    /// <summary>Favourites screen: players per page.</summary>
    [ObservableProperty]
    private int _favouritesPerPage;

    /// <summary>The page sizes on offer.</summary>
    public IReadOnlyList<int> PerPageChoices => FavouritePages.Sizes;

    /// <summary>Under the FAVOURITES card, with the shortcut as edited.</summary>
    public string FavouritesHint =>
        $"{FavouritesHotkey} shows everyone you saved with the star next to their name in the lobby.";

    /// <summary>"toggle" / "cycle" / "favourites" while that shortcut is being recorded, else null.</summary>
    [ObservableProperty]
    private string? _recording;

    /// <summary>Set by dragging the screen (no slider): DIPs from the top-right corner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementText))]
    private double _offsetX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementText))]
    private double _offsetY;

    /// <summary>Where the screen is now, in words.</summary>
    public string PlacementText => OffsetX == 0 && OffsetY == 0
        ? "Now: top-right corner."
        : $"Now: moved {Math.Abs(OffsetX):0} px {(OffsetX <= 0 ? "left" : "right")} and {Math.Abs(OffsetY):0} px {(OffsetY >= 0 ? "down" : "up")} from the top-right corner.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleText))]
    private double _scalePercent;

    public string ScaleText => $"{ScalePercent:0} %";

    [ObservableProperty]
    private bool _notifyWhenGameStarts;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _updateAutomatically;

    [ObservableProperty]
    private string _updateText = "";

    /// <summary>"Install 0.5.1 now" when an update is downloaded, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateReady))]
    private string? _installUpdateText;

    public bool HasUpdateReady => InstallUpdateText is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _canCheckForUpdates;

    [ObservableProperty]
    private string _portText;

    [ObservableProperty]
    private string _statsApiSummary = "";

    [ObservableProperty]
    private bool _statsApiOn;

    [ObservableProperty]
    private string _connectionStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableStatsApiCommand))]
    private bool _isEnabling;

    [ObservableProperty]
    private string? _enableMessage;

    [ObservableProperty]
    private bool _isPreviewing;

    [ObservableProperty]
    private string? _error;

    /// <summary>Called by the window when a shortcut was pressed while recording.</summary>
    public void SetHotkey(string target, HotkeyGesture gesture)
    {
        switch (target)
        {
            case "toggle":
                ToggleHotkey = gesture.ToString();
                break;
            case "favourites":
                FavouritesHotkey = gesture.ToString();
                break;
            default:
                CycleHotkey = gesture.ToString();
                break;
        }

        Error = null;
    }

    public void StartRecording(string target)
    {
        Recording = target;
        _host.SuspendHotkeys(true);
    }

    public void StopRecording()
    {
        if (Recording is null)
        {
            return;
        }

        Recording = null;
        _host.SuspendHotkeys(false);
    }

    /// <summary>The settings as currently edited (normalised).</summary>
    public OverlaySettings Build()
    {
        var s = _original.Clone();
        s.EnabledPlaylists = Playlists.Where(p => p.IsEnabled).Select(p => p.Playlist).ToList();
        s.DefaultPlaylist = DefaultPlaylist?.Playlist ?? s.EnabledPlaylists.FirstOrDefault(Playlist.Doubles);
        s.FollowMatchPlaylist = FollowMatchPlaylist;
        s.KeepOpenAtKickoff = KeepOpenAtKickoff;
        bool Col(string key) => Columns.First(c => c.Key == key).IsOn;
        s.Columns = new StatColumns
        {
            Wins = Col("Wins"), Streak = Col("Streak"), Peak = Col("Peak"), AllTimePeak = Col("AllTimePeak"), Goals = Col("Goals"),
            Shots = Col("Shots"), Assists = Col("Assists"), Saves = Col("Saves"), Mvps = Col("Mvps"),
        };
        s.ToggleHotkey = ToggleHotkey;
        s.CycleHotkey = CycleHotkey;
        s.FavouritesHotkey = FavouritesHotkey;
        s.FavouritesPerPage = FavouritesPerPage;
        s.OffsetX = Math.Round(OffsetX);
        s.OffsetY = Math.Round(OffsetY);
        s.Scale = Math.Round(ScalePercent) / 100;
        s.Screen = SelectedScreen?.DeviceName;
        s.NotifyWhenGameStarts = NotifyWhenGameStarts;
        s.UpdateAutomatically = UpdateAutomatically;
        s.StatsApiPort = int.TryParse(PortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;
        return s.Normalize();
    }

    /// <summary>Null when the edits can be saved, else what to fix.</summary>
    public string? Validate()
    {
        if (!HotkeyGesture.TryParse(ToggleHotkey, out var toggle) || !HotkeyGesture.TryParse(CycleHotkey, out var cycle)
            || !HotkeyGesture.TryParse(FavouritesHotkey, out var favourites))
        {
            return "Each shortcut needs Ctrl, Alt, Shift or Win plus a key.";
        }

        if (toggle == cycle || toggle == favourites || cycle == favourites)
        {
            return "The shortcuts must all be different.";
        }

        var port = PortText.Trim();
        if (port.Length > 0 && !(int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535))
        {
            return "The port must be a number from 1 to 65535 (or empty to read it from the game's ini).";
        }

        return null;
    }

    [RelayCommand]
    private void Save()
    {
        StopRecording();
        if (Validate() is { } problem)
        {
            Error = problem;
            return;
        }

        if (IsPreviewing)
        {
            IsPreviewing = false;
            _host.SetPreview(false, Build());
        }

        _host.Save(Build(), RunAtStartup);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        StopRecording();
        EndPreview();
        CloseRequested?.Invoke(false);
    }

    /// <summary>Called when the window closes by any route: the Preview never outlives the window.</summary>
    public void OnClosed()
    {
        StopRecording();
        EndPreview();
        _host.UpdateStateChanged -= OnUpdateStateChanged;
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdates() => _host.CheckForUpdatesAsync();

    [RelayCommand]
    private void InstallUpdate()
    {
        StopRecording();
        EndPreview();
        _host.InstallUpdate();
    }

    private void OnUpdateStateChanged() => ShowUpdateState();

    private void ShowUpdateState()
    {
        var state = _host.UpdateState;
        UpdateText = state.Text;
        InstallUpdateText = state.ReadyVersion is { } version ? $"Install {version} now" : null;
        CanCheckForUpdates = state.CanCheck;
    }

    [RelayCommand]
    private void TogglePreview()
    {
        IsPreviewing = !IsPreviewing;
        _host.SetPreview(IsPreviewing, IsPreviewing ? Build() : _original);
    }

    private void EndPreview()
    {
        if (IsPreviewing)
        {
            IsPreviewing = false;
            _host.SetPreview(false, _original);
        }
    }

    [RelayCommand]
    private void ResetPosition() => SetPlacement(0, 0, OverlaySettings.DefaultScale);

    /// <summary>The screen was dragged or resized during the Preview: that becomes the edited position and size.</summary>
    public void SetPlacement(double offsetX, double offsetY, double scale)
    {
        _suppressLive = true;
        OffsetX = Math.Round(offsetX);
        OffsetY = Math.Round(offsetY);
        ScalePercent = Math.Round(scale * 100);
        _suppressLive = false;
        LiveApply();
    }

    /// <summary>The screen was dragged or resized in a match and saved: show it here too, so Save and Cancel keep it.</summary>
    public void PlacementSaved(double offsetX, double offsetY, double scale)
    {
        _original.OffsetX = offsetX;
        _original.OffsetY = offsetY;
        _original.Scale = scale;
        SetPlacement(offsetX, offsetY, scale);
    }

    /// <summary>A page size was picked on the favourites screen and saved: show it here too, so Save and Cancel keep it.</summary>
    public void FavouritesPerPageSaved(int perPage)
    {
        _original.FavouritesPerPage = perPage;
        _suppressLive = true;
        FavouritesPerPage = perPage;
        _suppressLive = false;
    }

    [RelayCommand]
    private void OpenLogs() => _host.OpenFolder(AppPaths.LogsDir);

    [RelayCommand(CanExecute = nameof(CanEnableStatsApi))]
    private async Task EnableStatsApi()
    {
        IsEnabling = true;
        EnableMessage = null;
        try
        {
            var outcome = await _host.EnableStatsApiAsync();
            var restart = outcome.NeedsRestart
                ? StatsApiSetup.IsGameRunning()
                    ? Environment.NewLine + "Rocket League is running: restart the game so it picks this up."
                    : Environment.NewLine + "It takes effect the next time you start Rocket League."
                : "";
            EnableMessage = outcome.Message + restart;
        }
        finally
        {
            IsEnabling = false;
            RefreshStatsApi();
        }
    }

    private bool CanEnableStatsApi() => !IsEnabling;

    [RelayCommand]
    private void RefreshStatsApi()
    {
        var (summary, files) = _host.StatsApiStatus();
        StatsApiSummary = summary;
        StatsApiOn = files.FirstOrDefault(f => f.Values?.HasSection == true)?.Values?.IsEnabled == true;
        StatsApiFiles.Clear();
        foreach (var file in files.Where(f => f.Exists))
        {
            var v = file.Values!;
            var state = !v.HasSection ? "no Stats API section"
                : v.IsEnabled ? $"on · {v.PacketSendRate:0.##}/s · port {v.EffectivePort}"
                : "off";
            StatsApiFiles.Add(new StatsApiFileRow(file.Path, state, v.IsEnabled));
        }

        ConnectionStatus = _host.ConnectionStatus;
    }

    private void OnPlaylistToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlaylistOption.IsEnabled))
        {
            return;
        }

        // At least one playlist stays in the Ctrl+G cycle.
        if (!Playlists.Any(p => p.IsEnabled) && sender is PlaylistOption last)
        {
            last.IsEnabled = true;
            return;
        }

        var current = DefaultPlaylist?.Playlist;
        RebuildDefaultChoices();
        DefaultPlaylist = DefaultChoices.FirstOrDefault(o => o.Playlist == current) ?? DefaultChoices.FirstOrDefault();
        LiveApply();
    }

    private void RebuildDefaultChoices()
    {
        DefaultChoices.Clear();
        foreach (var option in Playlists.Where(p => p.IsEnabled))
        {
            DefaultChoices.Add(option);
        }
    }

    private void LiveApply()
    {
        if (IsPreviewing && !_suppressLive)
        {
            _host.PreviewSettings(Build());
        }
    }

    private static List<ScreenOption> BuildScreenOptions(IReadOnlyList<ScreenInfo> screens, string? saved)
    {
        var options = new List<ScreenOption> { new(null, GameScreenLabel) };
        options.AddRange(screens.Select(s => new ScreenOption(s.DeviceName, s.Label)));
        if (saved is not null && ScreenChoice.Find(saved, screens) is null)
        {
            // Unplugged for now: kept, so saving doesn't lose the choice (the overlay uses the game's screen meanwhile).
            var number = new ScreenInfo(saved, null, default, false).Number;
            options.Add(new ScreenOption(saved, (number is { } n ? $"Screen {n}" : saved) + " · not connected"));
        }

        return options;
    }

    private static string Display(string? text, string fallback) =>
        HotkeyGesture.TryParse(text, out var g) ? g.ToString() : fallback;
}
