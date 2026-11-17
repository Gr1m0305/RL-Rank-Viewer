using RLOverlay.Core.Players;

namespace RLOverlay.Core.Providers;

/// <summary>
/// A source of player rank/MMR/stats. Implementations: <see cref="TrackerGgProvider"/>, <see cref="MockStatsProvider"/>.
/// Wrap with <see cref="CachingStatsProvider"/> for caching, rate limiting and retries.
/// </summary>
/// <remarks>
/// Contract: return <c>null</c> when the player does not exist at the provider. Throw
/// <see cref="StatsProviderException"/> for HTTP-level failures (with <see cref="StatsProviderException.IsTransient"/>
/// set for 429/5xx). Other exceptions (network errors) are treated as transient by the cache.
/// </remarks>
public interface IPlayerStatsProvider
{
    string Name { get; }

    Task<PlayerProfile?> GetProfileAsync(PlatformId id, CancellationToken ct);
}

public sealed class StatsProviderException : Exception
{
    public StatsProviderException(string message, int? statusCode = null, bool isTransient = false, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public int? StatusCode { get; }

    /// <summary>True for rate limiting (429) and server errors (5xx): worth retrying with backoff.</summary>
    public bool IsTransient { get; }

    /// <summary>Server-requested wait (<c>Retry-After</c>), if any.</summary>
    public TimeSpan? RetryAfter { get; }
}
