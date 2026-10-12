namespace UrlShortener.Entities;

public class ShortUrl
{
    public long Id { get; set; }

    /// <summary>Base62 short code, for example "Ax7KfP". Case-sensitive.</summary>
    public string Code { get; set; } = string.Empty;

    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (64 chars) of the normalized original URL. Used for duplicate detection.</summary>
    public string OriginalUrlHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Concurrency token for optimistic locking.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ICollection<ClickEvent> ClickEvents { get; set; } = new List<ClickEvent>();
}
