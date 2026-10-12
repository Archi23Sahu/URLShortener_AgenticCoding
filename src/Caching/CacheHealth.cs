using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.Caching;

/// <summary>
/// A tiny circuit breaker. After a cache failure the cache is skipped for a cooldown period, so a
/// dead Redis costs a burst of slow requests per cooldown (those already in flight, or arriving before the next
/// failure re-opens the breaker) instead of a timeout on every redirect.
/// Registered as a singleton so all requests share the state.
/// </summary>
public sealed class CacheHealth
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _cooldown;
    private long _retryAtTicks; // UTC ticks; 0 means "healthy"

    public CacheHealth(TimeProvider clock, IOptions<CacheOptions> options)
    {
        _clock = clock;
        _cooldown = TimeSpan.FromSeconds(Math.Max(1, options.Value.FailureCooldownSeconds));
    }

    public bool IsAvailable => _clock.GetUtcNow().UtcTicks >= Interlocked.Read(ref _retryAtTicks);

    public void RecordFailure() =>
        Interlocked.Exchange(ref _retryAtTicks, _clock.GetUtcNow().Add(_cooldown).UtcTicks);
}
