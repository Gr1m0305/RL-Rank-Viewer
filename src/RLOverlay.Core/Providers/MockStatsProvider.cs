using System.Text;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Providers;

/// <summary>
/// Deterministic fake data for <c>--demo</c>, the Settings "Preview" button and tests. The same player always gets
/// the same numbers. The demo roster names (Zephyr, kickoffKing, ...) get hand-picked values that match the design
/// mockups. Players whose name contains "nodata" return null, to exercise the "No data" row.
/// </summary>
public sealed class MockStatsProvider : IPlayerStatsProvider
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _minLatency;
    private readonly TimeSpan _maxLatency;

    public MockStatsProvider(TimeSpan? minLatency = null, TimeSpan? maxLatency = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _minLatency = minLatency ?? TimeSpan.FromMilliseconds(400);
        _maxLatency = maxLatency ?? TimeSpan.FromMilliseconds(1600);
        if (_maxLatency < _minLatency)
        {
            _maxLatency = _minLatency;
        }
    }

    public string Name => "Mock";

    public async Task<PlayerProfile?> GetProfileAsync(PlatformId id, CancellationToken ct)
    {
        var hash = StableHash(id.CacheKey + "|" + id.DisplayName);
        var range = (_maxLatency - _minLatency).TotalMilliseconds;
        var latency = _minLatency + TimeSpan.FromMilliseconds(range <= 0 ? 0 : hash % (uint)Math.Max(1, range));
        if (latency > TimeSpan.Zero)
        {
            await Task.Delay(latency, _time, ct).ConfigureAwait(false);
        }

        if (id.DisplayName.Contains("nodata", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Build(id, _time.GetUtcNow());
    }

    /// <summary>Synchronous profile generation (used by tests and screenshot rendering).</summary>
    public static PlayerProfile Build(PlatformId id, DateTimeOffset now)
    {
        if (Showcase.TryGetValue(id.DisplayName, out var showcase))
        {
            return BuildShowcase(id, showcase, now);
        }

        var hash = StableHash(id.CacheKey + "|" + id.DisplayName);
        var rng = new Random((int)(hash & 0x7FFFFFFF));
        var baseMmr = 350 + rng.Next(0, 1350);
        var playlists = new Dictionary<Playlist, PlaylistStats>();
        foreach (var playlist in Enum.GetValues<Playlist>())
        {
            var mmr = Math.Max(100, baseMmr + rng.Next(-160, 120) - (playlist is Playlist.Duel ? 120 : 0));
            var matches = rng.Next(20, 1600);
            var streak = rng.Next(1, 6) * (rng.Next(0, 2) == 0 ? 1 : -1);
            playlists[playlist] = playlist == Playlist.Casual
                ? new PlaylistStats(Tier: RankTier.Unranked, Mmr: mmr - 90, MatchesPlayed: matches, WinStreak: streak, Season: 21)
                : new PlaylistStats(
                    Tier: TierForMmr(mmr),
                    Division: DivisionForMmr(mmr),
                    Mmr: mmr,
                    PeakMmr: mmr + rng.Next(10, 120),
                    MatchesPlayed: matches,
                    WinStreak: streak,
                    Season: 21);
        }

        var wins = rng.Next(150, 3000);
        var lifetime = new LifetimeStats(
            Wins: wins,
            Goals: (int)(wins * (1.6 + rng.NextDouble())),
            Shots: (int)(wins * (4 + rng.NextDouble() * 2)),
            Assists: (int)(wins * (0.9 + rng.NextDouble() * 0.8)),
            Saves: (int)(wins * (1.5 + rng.NextDouble() * 1.2)),
            Mvps: (int)(wins * (0.3 + rng.NextDouble() * 0.25)),
            MatchesPlayed: wins * 2);
        return new PlayerProfile(id, playlists, lifetime, now, "Mock");
    }

    /// <summary>Approximate 2v2 MMR → tier table, only for fake data.</summary>
    public static int TierForMmr(int mmr)
    {
        int[] starts = [0, 170, 230, 290, 350, 410, 470, 530, 590, 650, 710, 770, 835, 915, 995, 1075, 1195, 1315, 1435, 1575, 1715, 1860];
        var tier = 1;
        for (var i = 0; i < starts.Length; i++)
        {
            if (mmr >= starts[i])
            {
                tier = i + 1;
            }
        }

        return tier;
    }

    private static int DivisionForMmr(int mmr) => (mmr / 20 % 4) + 1;

    private sealed record ShowcaseData(Playlist Home, int Tier, int Mmr, int Wins, int Streak, int Peak, int Goals, int Shots, int Assists, int Saves, int Mvps, int CasualMmr);

    // Values from the design mockups (docs/screenshots), keyed by display name.
    private static readonly Dictionary<string, ShowcaseData> Showcase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Zephyr"] = new(Playlist.Doubles, 17, 1284, 812, 3, 1351, 2104, 4930, 1288, 1977, 402, 1102),
        ["kickoffKing"] = new(Playlist.Doubles, 16, 1192, 540, -2, 1240, 1433, 3511, 902, 1386, 255, 987),
        ["Nova_RL"] = new(Playlist.Doubles, 18, 1402, 1210, 5, 1455, 3021, 7012, 1710, 2655, 603, 1220),
        ["flipreset.exe"] = new(Playlist.Doubles, 15, 1095, 388, -1, 1178, 1002, 2620, 640, 1104, 171, 911),
        ["AerialAce"] = new(Playlist.Standard, 19, 1528, 1490, 2, 1561, 3877, 8120, 2203, 3010, 744, 1300),
        ["SonicBoom"] = new(Playlist.Standard, 22, 1902, 2650, 7, 1934, 6101, 12040, 3012, 4410, 1288, 1650),
        ["turtleMode"] = new(Playlist.Standard, 12, 845, 301, -3, 902, 688, 1950, 410, 1203, 98, 780),
    };

    private static PlayerProfile BuildShowcase(PlatformId id, ShowcaseData s, DateTimeOffset now)
    {
        var playlists = new Dictionary<Playlist, PlaylistStats>();
        foreach (var playlist in Enum.GetValues<Playlist>())
        {
            if (playlist == Playlist.Casual)
            {
                playlists[playlist] = new PlaylistStats(Tier: RankTier.Unranked, Mmr: s.CasualMmr, WinStreak: s.Streak, PeakMmr: s.Peak, Season: 21);
                continue;
            }

            // Ranked Doubles and Standard share the mockup values; other playlists sit a little lower.
            var offset = playlist is Playlist.Doubles or Playlist.Standard ? 0 : 60 + ((int)playlist * 23 % 90);
            var mmr = s.Mmr - offset;
            playlists[playlist] = new PlaylistStats(
                Tier: offset == 0 ? s.Tier : Math.Min(s.Tier, TierForMmr(mmr)),
                Division: 2,
                Mmr: mmr,
                PeakMmr: s.Peak - offset,
                WinStreak: s.Streak,
                Season: 21);
        }

        var lifetime = new LifetimeStats(s.Wins, s.Goals, s.Shots, s.Assists, s.Saves, s.Mvps, s.Wins * 2);
        return new PlayerProfile(id, playlists, lifetime, now, "Mock");
    }

    /// <summary>FNV-1a: stable across processes (unlike string.GetHashCode).</summary>
    internal static uint StableHash(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }
}
