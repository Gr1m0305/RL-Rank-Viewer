using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RLOverlay.Core.Players;

namespace RLOverlay.Core.Providers;

public sealed record CachingOptions
{
    /// <summary>How long a fetched profile is fresh.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a failed or empty lookup is remembered before trying again.</summary>
    public TimeSpan NegativeTtl { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Global budget: at most this many provider requests per rolling minute (0 = unlimited).</summary>
    public int RequestsPerMinute { get; init; } = 30;

    /// <summary>Retries after the first attempt for 429/5xx/network errors.</summary>
    public int MaxRetries { get; init; } = 3;

    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Random extra wait as a fraction of the backoff (0 disables; tests use 0).</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>On-disk cache folder (null = memory only).</summary>
    public string? DiskDirectory { get; init; }
}

/// <summary>
/// Decorator that makes any <see cref="IPlayerStatsProvider"/> polite and fast:
/// in-memory + on-disk JSON cache keyed by platform id with a TTL, one request in flight per player, a global
/// requests-per-minute budget, exponential backoff (honouring <c>Retry-After</c>) on 429/5xx, stale data served when a
/// refresh fails, and a short negative cache. Never throws for lookup failures: returns <c>null</c> ("No data").
/// </summary>
public sealed class CachingStatsProvider : IPlayerStatsProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPlayerStatsProvider _inner;
    private readonly CachingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<PlayerProfile?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recentRequests = new();
    private readonly object _budgetLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Random _jitter = new();
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public CachingStatsProvider(IPlayerStatsProvider inner, CachingOptions? options = null, TimeProvider? time = null, ILogger<CachingStatsProvider>? logger = null)
    {
        _inner = inner;
        _options = options ?? new CachingOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CachingStatsProvider>.Instance;
    }

    public string Name => _inner.Name;

    /// <summary>Requests actually sent to the wrapped provider (diagnostics and tests).</summary>
    public int ProviderRequests => _providerRequests;

    private int _providerRequests;

    public Task<PlayerProfile?> GetProfileAsync(PlatformId id, CancellationToken ct)
    {
        if (!id.CanLookup)
        {
            return Task.FromResult<PlayerProfile?>(null);
        }

        var key = id.CacheKey;
        var now = _time.GetUtcNow();
        if (_memory.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return Task.FromResult(cached.Profile ?? cached.Stale);
        }

        if (cached is null && TryLoadFromDisk(key) is { } disk)
        {
            _memory[key] = disk;
            cached = disk;
            if (disk.ExpiresAt > now)
            {
                return Task.FromResult(disk.Profile);
            }
        }

        var stale = cached?.Profile ?? cached?.Stale;
        // Lazy guarantees a single fetch even if GetOrAdd races and builds two wrappers.
        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<PlayerProfile?>>(() => FetchAndRemoveAsync(id, k, stale)));
        return lazy.Value.WaitAsync(ct);
    }

    /// <summary>Returns a cached profile without any network request (used to re-render rows on Ctrl+G).</summary>
    public PlayerProfile? TryGetCached(PlatformId id) =>
        _memory.TryGetValue(id.CacheKey, out var entry) ? entry.Profile ?? entry.Stale : null;

    private async Task<PlayerProfile?> FetchAndRemoveAsync(PlatformId id, string key, PlayerProfile? stale)
    {
        try
        {
            // Let GetOrAdd publish this task before any work that could complete synchronously.
            await Task.Yield();
            return await FetchAsync(id, key, stale, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<PlayerProfile?> FetchAsync(PlatformId id, string key, PlayerProfile? stale, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await WaitForBudgetAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _providerRequests);
                var profile = await _inner.GetProfileAsync(id, ct).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                if (profile is null)
                {
                    _memory[key] = new CacheEntry(null, now + _options.NegativeTtl, stale);
                    return stale;
                }

                var entry = new CacheEntry(profile, now + _options.Ttl, null);
                _memory[key] = entry;
                SaveToDisk(key, entry);
                return profile;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return stale;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.MaxRetries)
            {
                var retryAfter = (ex as StatsProviderException)?.RetryAfter;
                var delay = Backoff(attempt, retryAfter);
                if (ex is StatsProviderException { StatusCode: 429 })
                {
                    // Rate limited: everyone waits, not just this player.
                    var until = _time.GetUtcNow() + delay;
                    lock (_budgetLock)
                    {
                        if (until > _cooldownUntil)
                        {
                            _cooldownUntil = until;
                        }
                    }
                }

                _logger.LogWarning("{Provider} lookup for {Player} failed ({Error}); retry {Attempt}/{Max} in {Delay}",
                    _inner.Name, id, ex.Message, attempt + 1, _options.MaxRetries, delay);
                try
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return stale;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Provider} lookup for {Player} failed: {Error}", _inner.Name, id, ex.Message);
                _memory[key] = new CacheEntry(null, _time.GetUtcNow() + _options.NegativeTtl, stale);
                return stale;
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        StatsProviderException spe => spe.IsTransient,
        HttpRequestException => true,
        TaskCanceledException => true, // HttpClient timeout
        IOException => true,
        _ => false,
    };

    internal TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromMilliseconds(_options.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt));
        var delay = retryAfter is { } ra && ra > exponential ? ra : exponential;
        if (_options.JitterFraction > 0)
        {
            double r;
            lock (_jitter)
            {
                r = _jitter.NextDouble();
            }

            delay += TimeSpan.FromMilliseconds(delay.TotalMilliseconds * _options.JitterFraction * r);
        }

        return delay > _options.MaxBackoff ? _options.MaxBackoff : delay;
    }

    private async Task WaitForBudgetAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_budgetLock)
            {
                var now = _time.GetUtcNow();
                if (_cooldownUntil > now)
                {
                    wait = _cooldownUntil - now;
                }
                else if (_options.RequestsPerMinute <= 0)
                {
                    return;
                }
                else
                {
                    while (_recentRequests.Count > 0 && now - _recentRequests.Peek() >= TimeSpan.FromMinutes(1))
                    {
                        _recentRequests.Dequeue();
                    }

                    if (_recentRequests.Count < _options.RequestsPerMinute)
                    {
                        _recentRequests.Enqueue(now);
                        return;
                    }

                    wait = _recentRequests.Peek() + TimeSpan.FromMinutes(1) - now;
                }
            }

            if (wait < TimeSpan.FromMilliseconds(1))
            {
                wait = TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private string? DiskPath(string key)
    {
        if (_options.DiskDirectory is not { } dir)
        {
            return null;
        }

        var safe = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(dir, safe + ".json");
    }

    private CacheEntry? TryLoadFromDisk(string key)
    {
        var path = DiskPath(key);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<DiskEntry>(File.ReadAllText(path), JsonOptions);
            if (stored?.Profile is null)
            {
                return null;
            }

            return new CacheEntry(stored.Profile, stored.Profile.FetchedAt + _options.Ttl, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug("Ignoring unreadable cache file {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    private void SaveToDisk(string key, CacheEntry entry)
    {
        var path = DiskPath(key);
        if (path is null || entry.Profile is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new DiskEntry(entry.Profile), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug("Could not write cache file {Path}: {Error}", path, ex.Message);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        (_inner as IDisposable)?.Dispose();
    }

    /// <param name="Profile">The cached profile, or null for a remembered failure.</param>
    /// <param name="Stale">For failures: the last good profile, still shown.</param>
    private sealed record CacheEntry(PlayerProfile? Profile, DateTimeOffset ExpiresAt, PlayerProfile? Stale);

    private sealed record DiskEntry(PlayerProfile Profile);
}
