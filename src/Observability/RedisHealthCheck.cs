using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UrlShortener.Observability;

/// <summary>
/// Probes Redis by reading a key through the distributed cache (spec 009, behaviour 5). Redis being down is
/// <see cref="HealthStatus.Degraded"/>, not Unhealthy: redirects fall back to SQL Server (spec 004).
/// When the cache is not Redis (no ConnectionStrings:Redis, in-memory fallback) it reports Healthy without probing.
/// Bypasses CacheHealth on purpose — the probe must see the real state even while the cache circuit is open.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private const string ProbeKey = "health:probe";

    private readonly IDistributedCache _cache;

    public RedisHealthCheck(IDistributedCache cache)
    {
        _cache = cache;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_cache is not RedisCache)
        {
            return HealthCheckResult.Healthy("In-memory cache (Redis not configured).");
        }

        try
        {
            await _cache.GetAsync(ProbeKey, cancellationToken);
            return HealthCheckResult.Healthy("Redis reachable.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // No exception attached: /health is public, and the health-check service logs the exception's full
            // stack trace on every call, so polling it while Redis is down would flood the logs.
            return HealthCheckResult.Degraded($"Redis unreachable ({ex.GetType().Name}); redirects are served from SQL Server.");
        }
    }
}
