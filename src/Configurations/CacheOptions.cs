namespace UrlShortener.Configurations;

public class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Longest a redirect lookup stays cached. The real lifetime is the smaller of this and the
    /// link's remaining time, so an expired link is never served from cache. It is also the upper
    /// bound on how stale an entry can be if an invalidation is ever missed.
    /// </summary>
    public int TtlMinutes { get; set; } = 10;

    /// <summary>After a Redis error, skip the cache for this long instead of waiting on timeouts for every request.</summary>
    public int FailureCooldownSeconds { get; set; } = 30;
}
