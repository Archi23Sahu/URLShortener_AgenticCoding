using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Observability;
using UrlShortener.Services;

namespace UrlShortener.Caching;

/// <summary>
/// Cache-aside decorator for the redirect path.
///   hit  : code -> (id, url, expiry) comes from Redis, SQL is not touched.
///   miss : the real service reads SQL, then the result is stored in Redis.
/// Update and Delete remove the entry so a redirect never follows an old target.
/// The cache is an optimisation only: any Redis problem is logged and the request is served from SQL.
/// Failed lookups (404, 410) are not cached.
/// </summary>
public sealed class CachedUrlShortenerService : IUrlShortenerService
{
    private const string KeyPrefix = "link:";

    private readonly IUrlShortenerService _inner;
    private readonly IDistributedCache _cache;
    private readonly CacheHealth _health;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxTtl;
    private readonly UrlShortenerMetrics _metrics;
    private readonly ILogger<CachedUrlShortenerService> _logger;

    public CachedUrlShortenerService(
        IUrlShortenerService inner,
        IDistributedCache cache,
        CacheHealth health,
        TimeProvider clock,
        IOptions<CacheOptions> options,
        UrlShortenerMetrics metrics,
        ILogger<CachedUrlShortenerService> logger)
    {
        _inner = inner;
        _cache = cache;
        _health = health;
        _clock = clock;
        _maxTtl = TimeSpan.FromMinutes(Math.Max(1, options.Value.TtlMinutes));
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<ResolvedLink> ResolveAsync(string code, CancellationToken ct)
    {
        var (cached, lookup) = await TryGetAsync(code, ct);
        if (cached is not null && cached.ExpiresAt > Now())
        {
            _metrics.CacheLookup(CacheLookupResult.Hit);
            _logger.LogDebug("Cache hit for {Code}", code);
            return cached;
        }

        // An entry past its expiry counts as a miss: the database decides (410).
        _metrics.CacheLookup(lookup == CacheLookupResult.Hit ? CacheLookupResult.Miss : lookup);
        _logger.LogDebug("Cache miss for {Code}", code);

        // Throws NotFoundException / LinkExpiredException, which are deliberately not cached.
        var link = await _inner.ResolveAsync(code, ct);
        await TrySetAsync(code, link, ct);
        return link;
    }

    public async Task<ShortUrlResponse> UpdateAsync(string code, UpdateShortUrlRequest request, CancellationToken ct)
    {
        var response = await _inner.UpdateAsync(code, request, ct);
        await TryRemoveAsync(code);
        return response;
    }

    public async Task DeleteAsync(string code, CancellationToken ct)
    {
        await _inner.DeleteAsync(code, ct);
        await TryRemoveAsync(code);
    }

    public Task<CreateResult> CreateAsync(CreateShortUrlRequest request, CancellationToken ct) =>
        _inner.CreateAsync(request, ct);

    public Task<AnalyticsResponse> GetAnalyticsAsync(string code, CancellationToken ct) =>
        _inner.GetAnalyticsAsync(code, ct);

    // ---- cache helpers: none of these may throw (except for the caller's own cancellation) ----

    private async Task<(ResolvedLink? Link, CacheLookupResult Result)> TryGetAsync(string code, CancellationToken ct)
    {
        if (!_health.IsAvailable)
        {
            return (null, CacheLookupResult.Bypassed);
        }

        try
        {
            var bytes = await _cache.GetAsync(Key(code), ct);
            var link = bytes is null ? null : JsonSerializer.Deserialize<ResolvedLink>(bytes);
            return (link, link is null ? CacheLookupResult.Miss : CacheLookupResult.Hit);
        }
        catch (JsonException ex)
        {
            // An unreadable entry is treated as a miss and overwritten by the fresh lookup.
            _logger.LogWarning(ex, "Ignoring unreadable cache entry for {Code}", code);
            return (null, CacheLookupResult.Error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Failed(ex, "read", code);
            return (null, CacheLookupResult.Error);
        }
    }

    private async Task TrySetAsync(string code, ResolvedLink link, CancellationToken ct)
    {
        if (!_health.IsAvailable)
        {
            return;
        }

        // Never cache longer than the link lives.
        var remaining = link.ExpiresAt - Now();
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = remaining < _maxTtl ? remaining : _maxTtl
        };

        try
        {
            await _cache.SetAsync(Key(code), JsonSerializer.SerializeToUtf8Bytes(link), options, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Failed(ex, "write", code);
        }
    }

    private async Task TryRemoveAsync(string code)
    {
        // Invalidation must run even if the circuit is open: a stale entry is the worse outcome.
        // CancellationToken.None because the database change already happened.
        try
        {
            await _cache.RemoveAsync(Key(code), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _health.RecordFailure();
            _logger.LogWarning(ex,
                "Could not invalidate cache entry for {Code}; it can stay stale for up to {Minutes} minute(s)",
                code, _maxTtl.TotalMinutes);
        }
    }

    private void Failed(Exception ex, string operation, string code)
    {
        _health.RecordFailure();
        _logger.LogWarning(ex, "Cache {Operation} failed for {Code}; using the database", operation, code);
    }

    private static string Key(string code) => KeyPrefix + code;

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;
}
