using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Players;

/// <summary>
/// One playlist's data for a player. Every value is optional: providers fill what they have, the UI shows "–" for null.
/// </summary>
/// <param name="Tier">0–22, see <see cref="RankTier"/>.</param>
/// <param name="Division">1–4.</param>
/// <param name="WinStreak">Positive = current win streak, negative = loss streak.</param>
/// <param name="RankIconUrl">Icon URL served by the provider for this tier, if any.</param>
public sealed record PlaylistStats(
    int? Tier = null,
    int? Division = null,
    int? Mmr = null,
    int? PeakMmr = null,
    int? MatchesPlayed = null,
    int? Wins = null,
    int? WinStreak = null,
    int? Goals = null,
    int? Shots = null,
    int? Assists = null,
    int? Saves = null,
    int? Mvps = null,
    string? RankIconUrl = null,
    int? Season = null);

/// <summary>Account-wide totals (tracker.gg reports wins/goals/shots/assists/saves/MVPs per account).</summary>
public sealed record LifetimeStats(
    int? Wins = null,
    int? Goals = null,
    int? Shots = null,
    int? Assists = null,
    int? Saves = null,
    int? Mvps = null,
    int? MatchesPlayed = null);

public sealed record PlayerProfile(
    PlatformId Id,
    IReadOnlyDictionary<Playlist, PlaylistStats> Playlists,
    LifetimeStats Lifetime,
    DateTimeOffset FetchedAt,
    string Source)
{
    public PlaylistStats? For(Playlist playlist) => Playlists.TryGetValue(playlist, out var stats) ? stats : null;

    /// <summary>The latest season number any playlist reported.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? Season => Playlists.Values.Select(p => p.Season).Max();
}
