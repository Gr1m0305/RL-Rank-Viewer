using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RLOverlay.App.Services;
using RLOverlay.Core.Config;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>What the settings window needs from the app (implemented by <c>App</c>).</summary>
public interface ISettingsHost
{
    OverlaySettings Current { get; }

    bool HasApiKey { get; }

    bool RunAtStartup { get; }

    /// <summary>"Connected to Rocket League" / "Waiting for Rocket League" / "Demo".</summary>
    string ConnectionStatus { get; }

    /// <summary>Starts or stops the on-screen Preview (demo lobby with test data) using these settings.</summary>
    void SetPreview(bool on, OverlaySettings settings);

    /// <summary>Applies look and placement live while the Preview runs.</summary>
    void PreviewSettings(OverlaySettings settings);

    /// <summary>Global hotkeys off while a new shortcut is being recorded.</summary>
    void SuspendHotkeys(bool suspended);

    void Save(OverlaySettings settings, string? newApiKey, bool clearApiKey, bool runAtStartup);

    (string Summary, IReadOnlyList<StatsApiFileStatus> Files) StatsApiStatus();

    Task<StatsApiEnableOutcome> EnableStatsApiAsync();

    void OpenFolder(string path);

    void OpenUrl(string url);

    void ClearRankIconCache();
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

/// <summary>One row of the "Game connection" file list.</summary>
public sealed record StatsApiFileRow(string Path, string State, bool IsOn);

/// <summary>
/// The settings window. Edits a copy of the settings; nothing changes until Save (except the live Preview, which is
/// rolled back on Cancel).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string ApiKeyPortalUrl = "https://tracker.gg/developers";

    private static readonly string[] LiveProperties =
    [
        nameof(OffsetX), nameof(OffsetY), nameof(ScalePercent), nameof(ToggleHotkey), nameof(CycleHotkey), nameof(DefaultPlaylist),
    ];

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
            new("Goals", "Goals", c.Goals), new("Shots", "Shots", c.Shots), new("Assists", "Assists", c.Assists),
            new("Saves", "Saves", c.Saves), new("Mvps", "MVPs", c.Mvps),
        ];
        foreach (var column in Columns)
        {
            column.PropertyChanged += (_, _) => LiveApply();
        }

        RebuildDefaultChoices();
        _defaultPlaylist = DefaultChoices.FirstOrDefault(o => o.Playlist == _original.DefaultPlaylist) ?? DefaultChoices.FirstOrDefault();
        _followMatchPlaylist = _original.FollowMatchPlaylist;
        _toggleHotkey = Display(_original.ToggleHotkey, "Ctrl+R");
        _cycleHotkey = Display(_original.CycleHotkey, "Ctrl+G");
        _offsetX = _original.OffsetX;
        _offsetY = _original.OffsetY;
        _scalePercent = Math.Round(_original.Scale * 100);
        _useMockData = _original.Provider == ProviderNames.Mock;
        _hasStoredKey = host.HasApiKey;
        _cacheTtlMinutes = _original.CacheTtlMinutes;
        _requestsPerMinute = _original.RequestsPerMinute;
        _downloadRankIcons = _original.DownloadRankIcons;
        _notifyWhenGameStarts = _original.NotifyWhenGameStarts;
        _runAtStartup = host.RunAtStartup;
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

    public double MinScalePercent => OverlaySettings.MinScale * 100;

    public double MaxScalePercent => OverlaySettings.MaxScale * 100;

    [ObservableProperty]
    private PlaylistOption? _defaultPlaylist;

    [ObservableProperty]
    private bool _followMatchPlaylist;

    [ObservableProperty]
    private string _toggleHotkey;

    [ObservableProperty]
    private string _cycleHotkey;

    /// <summary>"toggle" / "cycle" while that shortcut is being recorded, else null.</summary>
    [ObservableProperty]
    private string? _recording;

    [ObservableProperty]
    private double _offsetX;

    [ObservableProperty]
    private double _offsetY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScaleText))]
    private double _scalePercent;

    public string ScaleText => $"{ScalePercent:0} %";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseTrackerGg))]
    private bool _useMockData;

    public bool UseTrackerGg
    {
        get => !UseMockData;
        set => UseMockData = !value;
    }

    /// <summary>A newly typed key (empty = keep the stored one).</summary>
    [ObservableProperty]
    private string _apiKeyInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyStatus))]
    private bool _hasStoredKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyStatus))]
    private bool _clearKey;

    public string KeyStatus => ClearKey ? "The saved key will be removed when you save."
        : HasStoredKey ? "A key is saved (encrypted for your Windows account). Type a new one to replace it."
        : "No key saved yet.";

    [ObservableProperty]
    private int _cacheTtlMinutes;

    [ObservableProperty]
    private int _requestsPerMinute;

    [ObservableProperty]
    private bool _downloadRankIcons;

    [ObservableProperty]
    private bool _notifyWhenGameStarts;

    [ObservableProperty]
    private bool _runAtStartup;

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
        if (target == "toggle")
        {
            ToggleHotkey = gesture.ToString();
        }
        else
        {
            CycleHotkey = gesture.ToString();
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
        bool Col(string key) => Columns.First(c => c.Key == key).IsOn;
        s.Columns = new StatColumns
        {
            Wins = Col("Wins"), Streak = Col("Streak"), Peak = Col("Peak"), Goals = Col("Goals"),
            Shots = Col("Shots"), Assists = Col("Assists"), Saves = Col("Saves"), Mvps = Col("Mvps"),
        };
        s.ToggleHotkey = ToggleHotkey;
        s.CycleHotkey = CycleHotkey;
        s.OffsetX = Math.Round(OffsetX);
        s.OffsetY = Math.Round(OffsetY);
        s.Scale = Math.Round(ScalePercent) / 100;
        s.Provider = UseMockData ? ProviderNames.Mock : ProviderNames.TrackerGg;
        s.CacheTtlMinutes = CacheTtlMinutes;
        s.RequestsPerMinute = RequestsPerMinute;
        s.DownloadRankIcons = DownloadRankIcons;
        s.NotifyWhenGameStarts = NotifyWhenGameStarts;
        s.StatsApiPort = int.TryParse(PortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;
        return s.Normalize();
    }

    /// <summary>Null when the edits can be saved, else what to fix.</summary>
    public string? Validate()
    {
        if (!HotkeyGesture.TryParse(ToggleHotkey, out var toggle) || !HotkeyGesture.TryParse(CycleHotkey, out var cycle))
        {
            return "Each shortcut needs Ctrl, Alt, Shift or Win plus a key.";
        }

        if (toggle == cycle)
        {
            return "The two shortcuts must be different.";
        }

        var port = PortText.Trim();
        if (port.Length > 0 && !(int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535))
        {
            return "The port must be a number from 1 to 65535 (or empty to read it from the game's ini).";
        }

        // A missing API key is allowed: rows show "No data" until one is added.
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

        var key = ApiKeyInput.Trim();
        _host.Save(Build(), key.Length > 0 ? key : null, ClearKey && key.Length == 0, RunAtStartup);
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
    private void ResetPosition()
    {
        _suppressLive = true;
        OffsetX = 0;
        OffsetY = 0;
        ScalePercent = 100;
        _suppressLive = false;
        LiveApply();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        ApiKeyInput = "";
        ClearKey = true;
    }

    partial void OnApiKeyInputChanged(string value)
    {
        if (value.Trim().Length > 0)
        {
            ClearKey = false;
        }
    }

    [RelayCommand]
    private void GetApiKey() => _host.OpenUrl(ApiKeyPortalUrl);

    [RelayCommand]
    private void OpenIconsFolder() => _host.OpenFolder(AppPaths.RankOverrideDir);

    [RelayCommand]
    private void RedownloadIcons()
    {
        _host.ClearRankIconCache();
        EnableMessage = null;
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

    private static string Display(string? text, string fallback) =>
        HotkeyGesture.TryParse(text, out var g) ? g.ToString() : fallback;
}
