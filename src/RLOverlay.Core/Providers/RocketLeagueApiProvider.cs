using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RLOverlay.Core.Players;
using RLOverlay.Core.Playlists;

namespace RLOverlay.Core.Providers;

/// <summary>
/// The third-party "Rocket League" API by TonyKun7 on RapidAPI (https://rapidapi.com/TonyKun7/api/rocket-league10),
/// with the user's own RapidAPI key. It returns each player's current rank, division and MMR per playlist, plus
/// their peak MMR.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It is not an official Psyonix or Tracker Network API, and it doesn't say where its data comes from.
/// The user chose to use it for this private app.</item>
/// <item>Request: <c>GET https://rocket-league10.p.rapidapi.com/stats/{platform}/{username}</c> with the headers
/// <c>X-RapidAPI-Key</c> and <c>X-RapidAPI-Host</c>. Platforms: steam, epic, xbox, ps4, switch. 404 = not found.</item>
/// <item>The response shape comes from the provider's own example client (TonyKun7/rocketleague-discord-bot,
/// <c>src/types/RocketLeague.ts</c>): <c>username</c>, <c>lifetime{wins,goals,saves,assists,shots,mvps}</c> and
/// <c>ranked[]</c>/<c>additional[]</c> entries with <c>playlistName, playlistId, tier, division, rating,
/// peakRating, peakTier, peakDivision, matchesPlayed</c>. The exact tier/division strings and whether Steam
/// players are found by SteamID64 or by name are not documented, so parsing is lenient and Steam tries the id
/// first, then the name.</item>
/// <item>Every HTTP request counts against the plan (free: 10 a day; paid plans are monthly with overage charges),
/// so each one first takes a slot from <see cref="LookupBudget"/>.</item>
/// </list>
/// </remarks>
public sealed class RocketLeagueApiProvider : IPlayerStatsProvider
{
    public const string Host = "rocket-league10.p.rapidapi.com";
    public const string BaseUrl = "https://" + Host + "/";
    public const string SourceName = "Rocket League API";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly LookupBudget? _budget;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public RocketLeagueApiProvider(
        HttpClient http,
        Func<string?> apiKey,
        LookupBudget? budget = null,
        ILogger<RocketLeagueApiProvider>? logger = null,
        TimeProvider? time = null)
    {
        _http = http;
        _apiKey = apiKey;
        _budget = budget;
        _logger = logger ?? NullLogger<RocketLeagueApiProvider>.Instance;
        _time = time ?? TimeProvider.System;
    }

    public string Name => SourceName;

    /// <summary>steam | epic | xbox | ps4 | switch.</summary>
    public static string? PlatformSlug(Platform platform) => platform switch
    {
        Platform.Steam => "steam",
        Platform.Epic => "epic",
        Platform.Xbox => "xbox",
        Platform.PlayStation => "ps4",
        Platform.Switch => "switch",
        _ => null,
    };

    /// <summary>
    /// What to look the player up by, in order: Steam tries the SteamID64 and then the name; every other platform
    /// uses the display name (the Stats API's Epic/PSN/Xbox uids are account ids, not names).
    /// </summary>
    public static IReadOnlyList<string> Identifiers(PlatformId id)
    {
        var name = id.DisplayName.Trim();
        var list = new List<string>();
        if (id.Platform == Platform.Steam && id.Uid.Length > 0)
        {
            list.Add(id.Uid);
        }

        if (name.Length > 0 && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(name);
        }

        if (list.Count == 0 && id.Uid.Length > 0)
        {
            list.Add(id.Uid);
        }

        return list;
    }

    public static string StatsUrl(string platformSlug, string identifier) =>
        $"{BaseUrl}stats/{platformSlug}/{Uri.EscapeDataString(identifier)}";

    public async Task<PlayerProfile?> GetProfileAsync(PlatformId id, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new StatsProviderException("No Rocket League API key saved (Settings → Player stats).");
        }

        if (!id.CanLookup || PlatformSlug(id.Platform) is not { } slug)
        {
            return null;
        }

        foreach (var identifier in Identifiers(id))
        {
            if (_budget is not null && !_budget.TryTake(out var used, out var limit))
            {
                throw new StatsProviderException(
                    $"Monthly lookup limit reached ({used} of {limit}). Raise it in Settings → Player stats.");
            }

            var profile = await LookUpAsync(id, slug, identifier, key.Trim(), ct).ConfigureAwait(false);
            if (profile is not null)
            {
                return profile;
            }
        }

        return null;
    }

    private async Task<PlayerProfile?> LookUpAsync(PlatformId id, string slug, string identifier, string key, CancellationToken ct)
    {
        var url = StatsUrl(slug, identifier);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-RapidAPI-Key", key);
        request.Headers.Add("X-RapidAPI-Host", Host);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RLOverlay", "0.2"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Rocket League API: no player {Identifier} on {Platform}", identifier, slug);
            return null;
        }

        if (response.StatusCode == (HttpStatusCode)429)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            if (body.Contains("quota", StringComparison.OrdinalIgnoreCase))
            {
                // The plan's daily/monthly allowance is used up: retrying only makes more failed calls.
                throw new StatsProviderException(
                    "The RapidAPI plan's request allowance is used up (" + Trim(body) + ").", status);
            }

            throw new StatsProviderException("Rocket League API: too many requests", status, isTransient: true, retryAfter: RetryAfter(response));
        }

        if (status >= 500)
        {
            throw new StatsProviderException($"Rocket League API returned {status} {response.ReasonPhrase}", status, isTransient: true, retryAfter: RetryAfter(response));
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            var hint = status is 401 or 403
                ? " — check the key, and that you subscribed to the Rocket League API on RapidAPI"
                : "";
            throw new StatsProviderException($"Rocket League API returned {status} {response.ReasonPhrase}{hint} ({Trim(body)})", status);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return RocketLeagueApiParser.Parse(id, doc.RootElement, _time.GetUtcNow());
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private static string Trim(string body)
    {
        var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 160 ? text[..160] + "…" : text;
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

/// <summary>Maps a Rocket League API (RapidAPI) <c>/stats</c> response to <see cref="PlayerProfile"/>. Lenient.</summary>
public static class RocketLeagueApiParser
{
    public static PlayerProfile? Parse(PlatformId id, JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var playlists = new Dictionary<Playlist, PlaylistStats>();
        foreach (var listName in new[] { "ranked", "additional" })
        {
            if (!TryGet(root, listName, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in list.EnumerateArray())
            {
                var playlist = PlaylistFor(entry);
                if (playlist is null || playlists.ContainsKey(playlist.Value))
                {
                    continue;
                }

                playlists[playlist.Value] = new PlaylistStats(
                    Tier: RankTier.Parse(Text(entry, "tier")),
                    Division: RankTier.ParseDivision(Text(entry, "division")),
                    Mmr: Number(entry, "rating"),
                    PeakMmr: Number(entry, "peakRating"),
                    MatchesPlayed: Number(entry, "matchesPlayed"));
            }
        }

        var lifetime = TryGet(root, "lifetime", out var life)
            ? new LifetimeStats(
                Wins: Number(life, "wins"),
                Goals: Number(life, "goals"),
                Shots: Number(life, "shots"),
                Assists: Number(life, "assists"),
                Saves: Number(life, "saves"),
                Mvps: Number(life, "mvps"))
            : new LifetimeStats();

        if (playlists.Count == 0 && !TryGet(root, "username", out _))
        {
            return null; // Not a stats response at all.
        }

        return new PlayerProfile(id, playlists, lifetime, now, RocketLeagueApiProvider.SourceName);
    }

    /// <summary>
    /// By the game's playlist id (as tracker.gg uses them; 0 = the casual "Un-Ranked" rating), else by the playlist
    /// name. Casual 1v1/2v2/3v3 ids only count when the name says casual, so they never pass for the ranked ones.
    /// </summary>
    public static Playlist? PlaylistFor(JsonElement entry)
    {
        var name = Text(entry, "playlistName");
        if (TryGet(entry, "playlistId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var playlistId))
        {
            if (playlistId == 0)
            {
                return Playlist.Casual;
            }

            switch (PlaylistCatalog.FromGameId(playlistId))
            {
                case Playlist.Casual:
                    return PlaylistForName(name) == Playlist.Casual ? Playlist.Casual : null;
                case { } known:
                    return known;
            }
        }

        return PlaylistForName(name);
    }

    public static Playlist? PlaylistForName(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Length == 0)
        {
            return null;
        }

        if (n.Contains("un-ranked") || n.Contains("unranked") || n.Contains("casual"))
        {
            return Playlist.Casual;
        }

        if (n.Contains("4v4") || n.Contains("quad") || n.Contains("heatseeker") || n.Contains("chaos"))
        {
            return null;
        }

        return n switch
        {
            _ when n.Contains("1v1") || n.Contains("duel") => Playlist.Duel,
            _ when n.Contains("2v2") || n.Contains("double") => Playlist.Doubles,
            _ when n.Contains("3v3") || n.Contains("standard") => Playlist.Standard,
            _ when n.Contains("hoops") => Playlist.Hoops,
            _ when n.Contains("rumble") => Playlist.Rumble,
            _ when n.Contains("dropshot") => Playlist.Dropshot,
            _ when n.Contains("snow") => Playlist.SnowDay,
            _ when n.Contains("tournament") => Playlist.Tournament,
            _ => null,
        };
    }

    private static string? Text(JsonElement element, string name) =>
        !TryGet(element, name, out var value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : value.ValueKind == JsonValueKind.Number ? value.GetRawText()
        : null;

    private static int? Number(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
        {
            return (int)Math.Round(d);
        }

        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? (int)Math.Round(s)
            : null;
    }

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
