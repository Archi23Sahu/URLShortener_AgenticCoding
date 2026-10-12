using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.Analytics;

public interface ICountryResolver
{
    /// <summary>Returns an upper-case ISO 3166-1 alpha-2 code, or null when unknown.</summary>
    string? Resolve(HttpContext context);
}

/// <summary>
/// Reads the country from a header set by a CDN or reverse proxy (default CF-IPCountry).
/// Without such a proxy the country is simply null. A GeoIP database (for example MaxMind)
/// can replace this class later without touching any other code.
/// </summary>
public sealed class HeaderCountryResolver : ICountryResolver
{
    private readonly string _headerName;

    public HeaderCountryResolver(IOptions<AnalyticsOptions> options)
    {
        _headerName = options.Value.CountryHeader;
    }

    public string? Resolve(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_headerName) ||
            !context.Request.Headers.TryGetValue(_headerName, out var values))
        {
            return null;
        }

        var value = values.ToString().Trim();

        // Accept exactly two letters. Cloudflare sends "XX" (unknown) and "T1" (Tor); those are
        // not real countries, and anything else is rejected so arbitrary text never reaches the CHAR(2) column.
        if (value.Length != 2 || !value.All(char.IsAsciiLetter))
        {
            return null;
        }

        value = value.ToUpperInvariant();
        return value is "XX" or "T1" ? null : value;
    }
}
