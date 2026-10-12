using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Analytics;

/// <summary>
/// Produces an anonymous visitor id: SHA-256(IP | User-Agent | salt) as 64 lowercase hex characters.
/// Same visitor gives the same hash (so unique visitors can be counted); the raw IP is never stored.
/// </summary>
public static class VisitorHasher
{
    public static string Compute(string? ipAddress, string? userAgent, string salt)
    {
        var input = $"{ipAddress ?? "unknown"}|{userAgent ?? string.Empty}|{salt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
