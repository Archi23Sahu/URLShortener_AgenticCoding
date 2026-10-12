using UrlShortener.Entities;
using UrlShortener.Repositories;

namespace UrlShortener.Analytics;

/// <summary>Writes queued clicks to ClickEvents. Separate from the background service so its failure handling can be unit tested.</summary>
public interface IClickEventWriter
{
    Task InsertAsync(IReadOnlyCollection<ClickRecord> clicks, CancellationToken ct);
}

/// <summary>
/// Uses a fresh DI scope (and DbContext) per call: DbContext is not thread-safe and must not live
/// for the whole lifetime of the singleton background service.
/// </summary>
public sealed class ClickEventWriter : IClickEventWriter
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ClickEventWriter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task InsertAsync(IReadOnlyCollection<ClickRecord> clicks, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.ClickEvents.AddRange(clicks.Select(c => new ClickEvent
        {
            ShortUrlId = c.ShortUrlId,
            ClickedAt = c.ClickedAt,
            VisitorHash = c.VisitorHash,
            Country = c.Country,
            Browser = c.Browser
        }));

        await db.SaveChangesAsync(ct);
    }
}
