using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Services;

/// <summary>
/// Validates and normalizes target URLs so duplicate detection treats
/// "HTTPS://Example.com:443/a" and "https://example.com/a" as the same link.
/// </summary>
public static class UrlNormalizer
{
    public static bool TryNormalize(string? url, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Only web URLs. Blocks javascript:, data:, file:, ftp: and similar (XSS / open-redirect abuse).
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Reject credentials in the URL (http://user:pass@host), a common phishing trick.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        // AbsoluteUri lower-cases scheme and host, drops default ports, and escapes the rest.
        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>SHA-256 of the normalized URL as 64 lower-case hex characters.</summary>
    public static string ComputeHash(string normalizedUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
