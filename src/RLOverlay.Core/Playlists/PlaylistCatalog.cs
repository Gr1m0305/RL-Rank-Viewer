namespace RLOverlay.Core.Playlists;

/// <summary>
/// Playlist names, team sizes and the mapping from Rocket League playlist ids (Stats API <c>Game.PlaylistId</c>,
/// tracker.gg segment <c>playlistId</c>). Only id 11 is confirmed by the official Stats API docs; the rest come from
/// well-known replay-parser tables. Keep every id in this one file so it is easy to fix.
/// </summary>
public static class PlaylistCatalog
{
    public static IReadOnlyList<PlaylistInfo> All { get; } =
    [
        new(Playlist.Duel, "Ranked Duel", "1v1", "1v1", "1v1 Duel", 1, true),
        new(Playlist.Doubles, "Ranked Doubles", "2v2", "2v2", "2v2 Doubles", 2, true),
        new(Playlist.Standard, "Ranked Standard", "3v3", "3v3", "3v3 Standard", 3, true),
        new(Playlist.Hoops, "Hoops", "2v2", "Hoops", "Hoops", 2, true),
        new(Playlist.Rumble, "Rumble", "3v3", "Rumble", "Rumble", 3, true),
        new(Playlist.Dropshot, "Dropshot", "3v3", "Dropshot", "Dropshot", 3, true),
        new(Playlist.SnowDay, "Snow Day", "3v3", "Snow Day", "Snow Day", 3, true),
        new(Playlist.Tournament, "Tournaments", "Tournament MMR", "Tourney", "Tournaments", null, true),
        new(Playlist.Casual, "Casual", "Casual MMR", "Casual", "Casual", null, false),
    ];

    public static PlaylistInfo Get(Playlist playlist) => All[(int)playlist];

    /// <summary>Rocket League playlist id → overlay playlist (null for private/offline/LTM playlists).</summary>
    public static Playlist? FromGameId(int playlistId) => playlistId switch
    {
        10 => Playlist.Duel,
        11 => Playlist.Doubles,
        13 => Playlist.Standard,
        27 => Playlist.Hoops,
        28 => Playlist.Rumble,
        29 => Playlist.Dropshot,
        30 => Playlist.SnowDay,
        22 or 34 => Playlist.Tournament,
        1 or 2 or 3 or 4 or 15 or 17 or 18 or 23 => Playlist.Casual,
        _ => null,
    };

    /// <summary>The tracker.gg / game playlist id that holds this playlist's rating (0 = casual "Un-Ranked").</summary>
    public static int GameIdFor(Playlist playlist) => playlist switch
    {
        Playlist.Duel => 10,
        Playlist.Doubles => 11,
        Playlist.Standard => 13,
        Playlist.Hoops => 27,
        Playlist.Rumble => 28,
        Playlist.Dropshot => 29,
        Playlist.SnowDay => 30,
        Playlist.Tournament => 34,
        Playlist.Casual => 0,
        _ => -1,
    };

    /// <summary>Players per team for a Rocket League playlist id, when known.</summary>
    public static int? TeamSizeForGameId(int playlistId) => playlistId switch
    {
        1 or 10 => 1,
        2 or 11 or 17 or 27 => 2,
        3 or 12 or 13 or 15 or 18 or 23 or 28 or 29 or 30 => 3,
        4 => 4,
        _ => null,
    };
}
