namespace UrlShortener.DTOs;

/// <param name="Url">Absolute http or https URL to shorten.</param>
/// <param name="ExpiryDays">Optional lifetime in days. Defaults to Shortener:DefaultExpiryDays.</param>
public record CreateShortUrlRequest(string Url, int? ExpiryDays = null);

/// <param name="Url">New absolute http or https target.</param>
/// <param name="ExpiryDays">Optional. When set, expiry is reset to now plus this many days. When omitted, expiry is unchanged.</param>
public record UpdateShortUrlRequest(string Url, int? ExpiryDays = null);

public record ShortUrlResponse(string ShortUrl, DateTime ExpiresAt);

public record AnalyticsResponse(long Clicks, long UniqueVisitors, IReadOnlyDictionary<string, long> Countries);
