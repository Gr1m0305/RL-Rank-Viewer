using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Providers;

/// <summary>
/// Tracker Network's official developer API, with the user's own API key (header <c>TRN-Api-Key</c>).
/// </summary>
/// <remarks>
/// Be honest about the reality (see docs/STATS_API_NOTES.md and README):
/// <list type="bullet">
/// <item>Rocket League is not in Tracker Network's list of supported titles for the public API, a maintainer has said
/// they are not allowed to offer a Rocket League API, and the developer portal returned 404 in November 2026.
/// With a public key this provider will most likely get 401/403/404 and the overlay shows "No data".</item>
/// <item>The tracker.gg website's internal endpoints are Cloudflare-protected and the Terms of Service restrict
/// scraping. This provider does not scrape and does not try to get around that protection.</item>
/// <item>The response shape below follows Tracker Network's v2 "standard profile" format used by their other titles
/// (<c>data.segments[]</c> with <c>stats.tier/division/rating</c>). It is unverified for Rocket League.</item>
/// </list>
/// The <see cref="IPlayerStatsProvider"/> abstraction exists so a sanctioned source can replace this later.
/// </remarks>
public sealed class TrackerGgProvider : IPlayerStatsProvider
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public TrackerGgProvider(HttpClient http, Func<string?> apiKey, ILogger<TrackerGgProvider>? logger = null, TimeProvider? time = null)
    {
        _http = http;
        _apiKey = apiKey;
        _logger = logger ?? NullLogger<TrackerGgProvider>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "tracker.gg";

    public async Task<PlayerProfile?> GetProfileAsync(PlatformId id, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new StatsProviderException("No tracker.gg API key configured (Settings → Data provider).");
        }

        var url = TrackerUrls.ApiProfile(id);
        if (url is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("TRN-Api-Key", key.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RLOverlay", "0.1"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("tracker.gg: no profile for {Player} ({Url})", id, url);
            return null;
        }

        if (response.StatusCode == (HttpStatusCode)429 || status >= 500)
        {
            throw new StatsProviderException(
                $"tracker.gg returned {status} {response.ReasonPhrase}",
                status,
                isTransient: true,
                retryAfter: RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            // 401/403 most likely mean the key has no Rocket League access (see remarks).
            throw new StatsProviderException(
                $"tracker.gg returned {status} {response.ReasonPhrase}" +
                (status is 401 or 403 ? " — the API key is invalid or has no Rocket League access" : ""),
                status);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return TrackerGgParser.Parse(id, doc.RootElement, _time.GetUtcNow());
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}

/// <summary>Maps a Tracker Network v2 profile response to <see cref="PlayerProfile"/>. Best-effort and lenient.</summary>
public static class TrackerGgParser
{
    public static PlayerProfile? Parse(PlatformId id, JsonElement root, DateTimeOffset now)
    {
        if (!TryGet(root, "data", out var data) || !TryGet(data, "segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var playlists = new Dictionary<Playlist, PlaylistStats>();
        var lifetime = new LifetimeStats();
        foreach (var segment in segments.EnumerateArray())
        {
            var type = TryGet(segment, "type", out var t) ? t.GetString() : null;
            TryGet(segment, "stats", out var stats);

            if (string.Equals(type, "overview", StringComparison.OrdinalIgnoreCase))
            {
                lifetime = new LifetimeStats(
                    Wins: Stat(stats, "wins"),
                    Goals: Stat(stats, "goals"),
                    Shots: Stat(stats, "shots"),
                    Assists: Stat(stats, "assists"),
                    Saves: Stat(stats, "saves"),
                    Mvps: Stat(stats, "mVPs") ?? Stat(stats, "mvps"),
                    MatchesPlayed: Stat(stats, "matchesPlayed"));
                continue;
            }

            if (!string.Equals(type, "playlist", StringComparison.OrdinalIgnoreCase)
                || !TryGet(segment, "attributes", out var attributes)
                || !TryGet(attributes, "playlistId", out var pid) || !pid.TryGetInt32(out var playlistId))
            {
                continue;
            }

            var playlist = PlaylistForTrackerId(playlistId);
            if (playlist is null)
            {
                continue;
            }

            int? season = TryGet(attributes, "season", out var s) && s.TryGetInt32(out var sv) ? sv : null;
            var division = Stat(stats, "division");
            playlists[playlist.Value] = new PlaylistStats(
                Tier: Stat(stats, "tier"),
                Division: division is null ? null : division + 1, // tracker.gg divisions are 0-based
                Mmr: Stat(stats, "rating"),
                PeakMmr: Stat(stats, "peakRating"),
                MatchesPlayed: Stat(stats, "matchesPlayed"),
                Wins: Stat(stats, "wins"),
                WinStreak: Streak(stats),
                Goals: Stat(stats, "goals"),
                Shots: Stat(stats, "shots"),
                Assists: Stat(stats, "assists"),
                Saves: Stat(stats, "saves"),
                Mvps: Stat(stats, "mVPs") ?? Stat(stats, "mvps"),
                RankIconUrl: IconUrl(stats),
                Season: season);
        }

        return new PlayerProfile(id, playlists, lifetime, now, "tracker.gg");
    }

    /// <summary>tracker.gg uses the game's playlist ids; 0 is the casual "Un-Ranked" segment.</summary>
    private static Playlist? PlaylistForTrackerId(int id) => id switch
    {
        0 => Playlist.Casual,
        _ => PlaylistCatalog.FromGameId(id) is { } p && p != Playlist.Casual ? p : null,
    };

    private static int? Stat(JsonElement stats, string name)
    {
        if (!TryGet(stats, name, out var stat))
        {
            return null;
        }

        if (stat.ValueKind == JsonValueKind.Number)
        {
            return stat.TryGetDouble(out var raw) ? (int)Math.Round(raw) : null;
        }

        if (TryGet(stat, "value", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
        {
            return (int)Math.Round(d);
        }

        return null;
    }

    private static int? Streak(JsonElement stats)
    {
        var value = Stat(stats, "winStreak");
        if (value is null || !TryGet(stats, "winStreak", out var streak))
        {
            return value;
        }

        var kind = TryGet(streak, "metadata", out var meta) && TryGet(meta, "type", out var type) ? type.GetString() : null;
        var display = TryGet(streak, "displayValue", out var dv) ? dv.GetString() : null;
        var isLoss = string.Equals(kind, "loss", StringComparison.OrdinalIgnoreCase)
                     || (display?.StartsWith('L') ?? false) || (display?.StartsWith('-') ?? false);
        return isLoss ? -Math.Abs(value.Value) : Math.Abs(value.Value);
    }

    private static string? IconUrl(JsonElement stats) =>
        TryGet(stats, "tier", out var tier) && TryGet(tier, "metadata", out var meta) && TryGet(meta, "iconUrl", out var url)
            && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var p in element.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }
}
