using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Analytics;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Repositories;
using UrlShortener.Services;

namespace UrlShortener.IntegrationTests;

public static class TestHelpers
{
    public const string ChromeAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>A URL no other test uses, so duplicate detection never links two tests together.</summary>
    public static string UniqueUrl(string host = "example.com") => $"https://{host}/{Guid.NewGuid():N}";

    public static string CodeOf(ShortUrlResponse response) => response.ShortUrl[(response.ShortUrl.LastIndexOf('/') + 1)..];

    public static async Task<(string Code, ShortUrlResponse Body)> CreateAsync(HttpClient client, string? url = null, int? expiryDays = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/shorten", new { url = url ?? UniqueUrl(), expiryDays });
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ShortUrlResponse>())!;
        return (CodeOf(body), body);
    }

    /// <summary>Opens the short link like a browser would, with optional User-Agent and country header.</summary>
    public static Task<HttpResponseMessage> VisitAsync(HttpClient client, string code, string? userAgent = null, string? country = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/" + code);
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (country is not null)
        {
            request.Headers.TryAddWithoutValidation("CF-IPCountry", country);
        }

        return client.SendAsync(request);
    }

    /// <summary>Inserts a link directly in the database (used to create already-expired links).</summary>
    public static async Task<string> InsertLinkAsync(ApiFactory factory, DateTime expiresAtUtc, string url = "https://example.com/expired")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var code = "t" + Guid.NewGuid().ToString("N")[..9];
        db.ShortUrls.Add(new ShortUrl
        {
            Code = code,
            OriginalUrl = url,
            OriginalUrlHash = UrlNormalizer.ComputeHash(url),
            CreatedAt = DateTime.UtcNow.AddDays(-40),
            ExpiresAt = expiresAtUtc
        });
        await db.SaveChangesAsync();
        return code;
    }

    /// <summary>Database id of the link with this short code.</summary>
    public static async Task<long> LinkIdAsync(ApiFactory factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ShortUrls.AsNoTracking().Where(x => x.Code == code).Select(x => x.Id).SingleAsync();
    }

    /// <summary>Number of stored click events for one link.</summary>
    public static async Task<int> CountClicksAsync(ApiFactory factory, long shortUrlId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClickEvents.AsNoTracking().CountAsync(c => c.ShortUrlId == shortUrlId);
    }

    /// <summary>A click as the tracker would queue it, for tests that feed IClickQueue directly.</summary>
    public static ClickRecord QueuedClick(long shortUrlId) =>
        new(shortUrlId, DateTime.UtcNow, new string('a', 64), "US", "Chrome");

    /// <summary>Clicks are written by a background worker, so poll until the condition holds or time runs out.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        T value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            value = await read();
        }

        return value;
    }
}
