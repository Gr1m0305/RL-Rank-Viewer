using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Config;

/// <summary>User settings, saved as JSON in <c>%LOCALAPPDATA%\RLOverlay\settings.json</c>.</summary>
public sealed class OverlaySettings
{
    public const double MinScale = 0.75;
    public const double MaxScale = 1.5;

    public int SchemaVersion { get; set; } = 1;

    // Playlists
    public Playlist DefaultPlaylist { get; set; } = Playlist.Doubles;

    public List<Playlist> EnabledPlaylists { get; set; } = [.. Enum.GetValues<Playlist>()];

    /// <summary>Start each match on the playlist the game reports (Game.PlaylistId) when it is enabled.</summary>
    public bool FollowMatchPlaylist { get; set; } = true;

    // Columns
    public StatColumns Columns { get; set; } = new();

    // Hotkeys
    public string ToggleHotkey { get; set; } = "Ctrl+R";

    public string CycleHotkey { get; set; } = "Ctrl+G";

    /// <summary>Opens or closes the lobby window that shows each player's row from tracker.gg.</summary>
    public string TrackerHotkey { get; set; } = "Ctrl+T";

    // Position (DIPs, relative to the top-centre anchor) and scale
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Scale { get; set; } = 1.0;

    // Data provider
    public string Provider { get; set; } = ProviderNames.RocketLeagueApi;

    /// <summary>tracker.gg API key, encrypted with Windows DPAPI (current user), base64. Never stored in plain text.</summary>
    public string? ProtectedApiKey { get; set; }

    /// <summary>RapidAPI key for the Rocket League API, encrypted the same way as <see cref="ProtectedApiKey"/>.</summary>
    public string? ProtectedRocketLeagueApiKey { get; set; }

    /// <summary>
    /// At most this many Rocket League API requests per calendar month (0 = no limit). Paid RapidAPI plans charge for
    /// requests over their allowance, so this defaults to the Pro plan's 2,000.
    /// </summary>
    public int MaxLookupsPerMonth { get; set; } = 2000;

    public int CacheTtlMinutes { get; set; } = 10;

    public int RequestsPerMinute { get; set; } = 30;

    /// <summary>
    /// Download the official in-game rank icons once (from Tracker Network's CDN) into the local cache.
    /// Off = only the user's own files in the ranks folder, else built-in placeholders.
    /// </summary>
    public bool DownloadRankIcons { get; set; } = true;

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
        CacheTtlMinutes = Math.Clamp(CacheTtlMinutes, 1, 24 * 60);
        RequestsPerMinute = Math.Clamp(RequestsPerMinute, 1, 600);
        MaxLookupsPerMonth = Math.Clamp(MaxLookupsPerMonth, 0, 1_000_000);
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

        if (!HotkeyGesture.TryParse(TrackerHotkey, out _))
        {
            TrackerHotkey = "Ctrl+T";
        }

        if (Provider is not (ProviderNames.RocketLeagueApi or ProviderNames.TrackerGg or ProviderNames.Mock))
        {
            Provider = ProviderNames.RocketLeagueApi;
        }

        if (StatsApiPort is { } port && (port <= 0 || port > 65535))
        {
            StatsApiPort = null;
        }

        return this;
    }

    public OverlaySettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>Which stat columns follow MMR. All on by default; the panel shrinks when some are off.</summary>
public sealed class StatColumns
{
    public bool Wins { get; set; } = true;

    public bool Streak { get; set; } = true;

    public bool Peak { get; set; } = true;

    public bool Goals { get; set; } = true;

    public bool Shots { get; set; } = true;

    public bool Assists { get; set; } = true;

    public bool Saves { get; set; } = true;

    public bool Mvps { get; set; } = true;
}

public static class ProviderNames
{
    /// <summary>The Rocket League API on RapidAPI (live rank, MMR and peak MMR).</summary>
    public const string RocketLeagueApi = "RocketLeagueApi";

    public const string TrackerGg = "TrackerGg";
    public const string Mock = "Mock";
}
