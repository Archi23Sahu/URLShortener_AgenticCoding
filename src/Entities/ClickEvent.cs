namespace UrlShortener.Entities;

public class ClickEvent
{
    public long Id { get; set; }

    public long ShortUrlId { get; set; }

    public ShortUrl ShortUrl { get; set; } = null!;

    public DateTime ClickedAt { get; set; }

    /// <summary>
    /// SHA-256 hex (64 chars) of IP + User-Agent + salt.
    /// Lets us count unique visitors without storing raw IP addresses.
    /// </summary>
    public string VisitorHash { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 alpha-2 country code (for example "US"). Null when unknown.</summary>
    public string? Country { get; set; }

    /// <summary>Browser family detected from the User-Agent (for example "Chrome"). Null for clicks recorded before this column existed.</summary>
    public string? Browser { get; set; }
}
