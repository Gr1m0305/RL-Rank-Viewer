namespace RLOverlay.Core.Playlists;

public static class PlaylistCycler
{
    private static readonly Playlist[] Order = Enum.GetValues<Playlist>();

    /// <summary>
    /// The next enabled playlist after <paramref name="current"/> in cycle order, wrapping around.
    /// Returns <paramref name="current"/> when nothing else is enabled.
    /// </summary>
    public static Playlist Next(Playlist current, IReadOnlyCollection<Playlist> enabled)
    {
        if (enabled.Count == 0)
        {
            return current;
        }

        var start = Array.IndexOf(Order, current);
        for (var step = 1; step <= Order.Length; step++)
        {
            var candidate = Order[(start + step) % Order.Length];
            if (enabled.Contains(candidate))
            {
                return candidate;
            }
        }

        return current;
    }

    /// <summary>
    /// The playlist to show when a match starts: the match's own playlist when following is on and it is enabled,
    /// otherwise the default (if enabled), otherwise the first enabled playlist.
    /// </summary>
    public static Playlist ResolveInitial(
        Playlist defaultPlaylist,
        IReadOnlyCollection<Playlist> enabled,
        int? matchPlaylistId,
        bool followMatchPlaylist)
    {
        if (followMatchPlaylist && matchPlaylistId is { } id && PlaylistCatalog.FromGameId(id) is { } matchPlaylist
            && enabled.Contains(matchPlaylist))
        {
            return matchPlaylist;
        }

        if (enabled.Count == 0 || enabled.Contains(defaultPlaylist))
        {
            return defaultPlaylist;
        }

        return Order.First(enabled.Contains);
    }
}
