namespace UrlShortener.Configurations;

public class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Base URL used to build short links, for example "http://localhost:8080".</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Link lifetime in days when the caller does not specify one.</summary>
    public int DefaultExpiryDays { get; set; } = 30;

    /// <summary>Upper bound a caller may request for ExpiryDays.</summary>
    public int MaxExpiryDays { get; set; } = 365;
}
