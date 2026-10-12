namespace UrlShortener.Configurations;

public class AnalyticsOptions
{
    public const string SectionName = "Analytics";

    /// <summary>
    /// Secret mixed into the visitor hash so hashes cannot be reversed with a lookup table of IPs.
    /// Override in every real environment (env var Analytics__Salt). Changing it resets unique-visitor counts.
    /// </summary>
    public string Salt { get; set; } = "dev-only-change-me";

    /// <summary>Max clicks waiting in memory. When full, new clicks are dropped (the redirect is never slowed).</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Max clicks written to the database in one batch.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Longest a click waits before being written, even if the batch is not full.</summary>
    public int FlushIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Request header carrying a 2-letter country code, set by a CDN or proxy
    /// (Cloudflare: CF-IPCountry). Empty or missing header means "country unknown".
    /// </summary>
    public string CountryHeader { get; set; } = "CF-IPCountry";
}
