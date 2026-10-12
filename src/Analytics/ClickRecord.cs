namespace UrlShortener.Analytics;

/// <summary>One redirect, captured on the request path and written to the database later.</summary>
public record ClickRecord(long ShortUrlId, DateTime ClickedAt, string VisitorHash, string? Country, string? Browser = null);
