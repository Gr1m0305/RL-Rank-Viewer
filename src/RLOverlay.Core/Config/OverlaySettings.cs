using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Config;

/// <summary>User settings, saved as JSON in <c>%LOCALAPPDATA%\RLOverlay\settings.json</c>.</summary>
public sealed class OverlaySettings
{
    public const double MinScale = 0.75;
    public const double MaxScale = 1.5;

    /// <summary>
    /// 2 = the tracker.gg screen (Wins, Streak and Peak MMR ticked by default); files from version 1 are upgraded by
    /// <see cref="Upgrade"/> when loaded.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Playlists
    public Playlist DefaultPlaylist { get; set; } = Playlist.Doubles;

    public List<Playlist> EnabledPlaylists { get; set; } = [.. Enum.GetValues<Playlist>()];

    /// <summary>Start each match on the playlist the game reports (Game.PlaylistId) when it is enabled.</summary>
    public bool FollowMatchPlaylist { get; set; } = true;

    /// <summary>Stats shown next to each player's tracker.gg rank row.</summary>
    public StatColumns Columns { get; set; } = new();

    // Hotkeys
    public string ToggleHotkey { get; set; } = "Ctrl+R";

    public string CycleHotkey { get; set; } = "Ctrl+G";

    // Position (DIPs, relative to the top-centre anchor) and scale
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Scale { get; set; } = 1.0;

    // General
    public bool RunAtStartup { get; set; }

    /// <summary>Show a short notice when Rocket League starts and the overlay switches on.</summary>
    public bool NotifyWhenGameStarts { get; set; } = true;

    /// <summary>Overrides the Stats API port read from the game's ini (null = use the ini, default 49123).</summary>
    public int? StatsApiPort { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Clamp values into range and repair invalid lists after loading or editing.</summary>
    public OverlaySettings Normalize()
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, MinScale, MaxScale) : 1.0;
        OffsetX = double.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -4000, 4000) : 0;
        OffsetY = double.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -4000, 4000) : 0;
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

        if (StatsApiPort is { } port && (port <= 0 || port > 65535))
        {
            StatsApiPort = null;
        }

        return this;
    }

    /// <summary>
    /// Brings settings saved by an older version up to date. Version 1 (the RapidAPI panel) had every stat column on
    /// by default; the tracker.gg screen starts with just Wins, Streak and Peak MMR, since each stat widens the rows.
    /// </summary>
    public OverlaySettings Upgrade()
    {
        if (SchemaVersion < 2)
        {
            Columns = new StatColumns();
        }

        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        return this;
    }

    public OverlaySettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>
/// Which stats are shown next to each player's tracker.gg rank row. Wins, Goals, Shots, Assists, Saves and MVPs are
/// tracker.gg's lifetime tiles; Peak (peak rating) and Streak (matches and streak) are cells of the playlist's row.
/// Wins, Streak and Peak are on by default.
/// </summary>
public sealed class StatColumns
{
    public bool Wins { get; set; } = true;

    public bool Streak { get; set; } = true;

    public bool Peak { get; set; } = true;

    public bool Goals { get; set; }

    public bool Shots { get; set; }

    public bool Assists { get; set; }

    public bool Saves { get; set; }

    public bool Mvps { get; set; }
}
