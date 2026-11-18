namespace RLOverlay.Core.Playlists;

/// <summary>Playlists the panel can display. Declaration order is the Ctrl+G cycle order.</summary>
public enum Playlist
{
    Duel,
    Doubles,
    Standard,
    Hoops,
    Rumble,
    Dropshot,
    SnowDay,
    Heatseeker,
    Tournament,
    Casual,
}

/// <param name="Title">Header title, e.g. "Ranked Doubles".</param>
/// <param name="Subtitle">Muted text after the title, e.g. "2v2" or "Casual MMR".</param>
/// <param name="StripLabel">Label in the Ctrl+G playlist strip, e.g. "2v2", "Tourney".</param>
/// <param name="SettingsLabel">Label in the settings window, e.g. "2v2 Doubles".</param>
/// <param name="TeamSize">Players per team, when fixed.</param>
public sealed record PlaylistInfo(
    Playlist Playlist,
    string Title,
    string Subtitle,
    string StripLabel,
    string SettingsLabel,
    int? TeamSize,
    bool IsRanked);
