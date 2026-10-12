using UrlShortener.DTOs;

namespace UrlShortener.Services;

/// <param name="Response">The short URL and its expiry.</param>
/// <param name="Created">False when an existing active link for the same URL was returned.</param>
public record CreateResult(ShortUrlResponse Response, bool Created);

/// <param name="Id">Database id of the link; the redirect needs it to record a click.</param>
/// <param name="Url">The target to redirect to.</param>
/// <param name="ExpiresAt">UTC expiry; the cache uses it to cap how long an entry may live.</param>
public record ResolvedLink(long Id, string Url, DateTime ExpiresAt);

public interface IUrlShortenerService
{
    Task<CreateResult> CreateAsync(CreateShortUrlRequest request, CancellationToken ct);

    /// <summary>Returns the link id and target URL. Throws NotFoundException or LinkExpiredException.</summary>
    Task<ResolvedLink> ResolveAsync(string code, CancellationToken ct);

    Task<ShortUrlResponse> UpdateAsync(string code, UpdateShortUrlRequest request, CancellationToken ct);

    Task DeleteAsync(string code, CancellationToken ct);

    Task<AnalyticsResponse> GetAnalyticsAsync(string code, CancellationToken ct);
}
