using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Config;

/// <summary>User settings, saved as JSON in <c>%LOCALAPPDATA%\RLOverlay\settings.json</c>.</summary>
public sealed class OverlaySettings
{
    /// <summary>Smallest size: below this tracker.gg's rows get hard to read.</summary>
    public const double MinScale = 0.75;
    public const double MaxScale = 1.5;

    /// <summary>Size for new settings and after Reset.</summary>
    public const double DefaultScale = 0.85;

    /// <summary>
    /// 4 = Heatseeker playlist and automatic updates; 3 = top-right placement and the smaller default size, Follow the
    /// match's playlist off; 2 = the tracker.gg screen (Wins, Streak and Peak MMR ticked by default). Older files are
    /// upgraded by <see cref="Upgrade"/> when loaded.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Playlists
    public Playlist DefaultPlaylist { get; set; } = Playlist.Doubles;

    public List<Playlist> EnabledPlaylists { get; set; } = [.. Enum.GetValues<Playlist>()];

    /// <summary>Start each match on the playlist the game reports (Game.PlaylistId) when it is enabled. Off by default.</summary>
    public bool FollowMatchPlaylist { get; set; }

    /// <summary>Stats shown next to each player's tracker.gg rank row.</summary>
    public StatColumns Columns { get; set; } = new();

    /// <summary>
    /// Keep the lobby on screen when the match starts instead of hiding it at kickoff: it stays until the show / hide
    /// shortcut, or until the match is left. Off by default (missing in older files, so no new schema version).
    /// </summary>
    public bool KeepOpenAtKickoff { get; set; }

    // Hotkeys
    public string ToggleHotkey { get; set; } = "Ctrl+R";

    public string CycleHotkey { get; set; } = "Ctrl+G";

    /// <summary>Opens and closes the favourites screen. Missing in older files, so no new schema version.</summary>
    public string FavouritesHotkey { get; set; } = "Ctrl+F";

    /// <summary>Players on each page of the favourites screen: 5 (the default) or 10.</summary>
    public int FavouritesPerPage { get; set; } = FavouritePages.DefaultSize;

    // Position (DIPs from the top-right corner, see PanelPlacement; set by dragging the screen) and size
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Scale { get; set; } = DefaultScale;

    /// <summary>
    /// Windows' device name of the screen the overlay is shown on (<c>\\.\DISPLAY2</c>); null = the screen Rocket League
    /// is on (the default). A chosen screen that isn't connected falls back to the game's screen (see
    /// <see cref="ScreenChoice"/>).
    /// </summary>
    public string? Screen { get; set; }

    // General
    public bool RunAtStartup { get; set; }

    /// <summary>Show a short notice when Rocket League starts and the overlay switches on.</summary>
    public bool NotifyWhenGameStarts { get; set; } = true;

    /// <summary>
    /// Check GitHub for new releases, download them in the background and install them while Rocket League is closed.
    /// </summary>
    public bool UpdateAutomatically { get; set; } = true;

    /// <summary>Overrides the Stats API port read from the game's ini (null = use the ini, default 49123).</summary>
    public int? StatsApiPort { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Clamp values into range and repair invalid lists after loading or editing.</summary>
    public OverlaySettings Normalize()
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, MinScale, MaxScale) : DefaultScale;
        OffsetX = double.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -4000, 4000) : 0;
        OffsetY = double.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -4000, 4000) : 0;
        Screen = string.IsNullOrWhiteSpace(Screen) ? null : Screen.Trim();
        EnabledPlaylists = (EnabledPlaylists ?? []).Where(Enum.IsDefined).Distinct().OrderBy(p => p).ToList();
        if (EnabledPlaylists.Count == 0)
        {
            EnabledPlaylists.Add(Enum.IsDefined(DefaultPlaylist) ? DefaultPlaylist : Playlist.Doubles);
        }

        if (!Enum.IsDefined(DefaultPlaylist))
        {
            DefaultPlaylist = Playlist.Doubles;
        }

        Columns ??= new StatColumns();
        if (!HotkeyGesture.TryParse(ToggleHotkey, out _))
        {
            ToggleHotkey = "Ctrl+R";
        }

        if (!HotkeyGesture.TryParse(CycleHotkey, out _))
        {
            CycleHotkey = "Ctrl+G";
        }

        // Never moved to another key by itself: only Settings changes a shortcut.
        if (!HotkeyGesture.TryParse(FavouritesHotkey, out _))
        {
            FavouritesHotkey = "Ctrl+F";
        }

        FavouritesPerPage = FavouritePages.NormalizeSize(FavouritesPerPage);

        if (StatsApiPort is { } port && (port <= 0 || port > 65535))
        {
            StatsApiPort = null;
        }

        return this;
    }

    /// <summary>
    /// Brings settings saved by an older version up to date. Version 1 (the RapidAPI panel) had every stat column on
    /// by default; the tracker.gg screen starts with just Wins, Streak and Peak MMR, since each stat widens the rows.
    /// Version 2 measured the position from the top centre: the screen now starts in the top-right corner at the new
    /// default size, and Follow the match's playlist starts off. Version 3 had no Heatseeker playlist: it joins the
    /// Ctrl+G cycle like the others.
    /// </summary>
    public OverlaySettings Upgrade()
    {
        if (SchemaVersion < 2)
        {
            Columns = new StatColumns();
        }

        if (SchemaVersion < 3)
        {
            OffsetX = 0;
            OffsetY = 0;
            Scale = DefaultScale;
            FollowMatchPlaylist = false;
        }

        if (SchemaVersion < 4 && EnabledPlaylists is { Count: > 0 } enabled && !enabled.Contains(Playlist.Heatseeker))
        {
            enabled.Add(Playlist.Heatseeker);
        }

        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        return this;
    }

    public OverlaySettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>
/// Which stats are shown next to each player's tracker.gg rank row. Wins, Goals, Shots, Assists, Saves and MVPs are
/// tracker.gg's lifetime tiles; Peak (this season's peak rating) and Streak (matches and streak) are cells of the
/// playlist's row; AllTimePeak is the "Best" rating from the profile's Peak Rating card, which tracker.gg only has for
/// Ranked Duel, Doubles and Standard. Wins, Streak and Peak are on by default.
/// </summary>
public sealed class StatColumns
{
    public bool Wins { get; set; } = true;

    public bool Streak { get; set; } = true;

    public bool Peak { get; set; } = true;

    public bool AllTimePeak { get; set; }

    public bool Goals { get; set; }

    public bool Shots { get; set; }

    public bool Assists { get; set; }

    public bool Saves { get; set; }

    public bool Mvps { get; set; }
}
