using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Entities;
using UrlShortener.Repositories;
using UrlShortener.Services;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Deletes rely on the SQL cascade from ShortUrls to ClickEvents, so they are proven against real SQL Server.
/// The shared test database is used by other classes in parallel: assertions only look at this test's own ids.
/// </summary>
public class DeletionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DeletionTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    // ---- DELETE endpoint (DEL-AC-03) -------------------------------------------------------------

    [Fact]
    public async Task Delete_LinkWithClicks_RemovesItsClickEvents()
    {
        var (code, _) = await CreateAsync(_client);
        var id = await LinkIdAsync(_factory, code);
        (await VisitAsync(_client, code, ChromeAgent, "IN")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, ChromeAgent, "US")).StatusCode.Should().Be(HttpStatusCode.Found);

        // Wait until both clicks are stored, so none is still queued when the link goes away.
        var stored = await EventuallyAsync(() => CountClicksAsync(_factory, id), n => n >= 2);
        stored.Should().Be(2);

        var response = await _client.DeleteAsync($"/api/v1/shorten/{code}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountClicksAsync(_factory, id)).Should().Be(0);
    }

    // ---- Expired link cleanup (CLN-AC-08) --------------------------------------------------------

    [Fact]
    public async Task Cleanup_DeletesOnlyLinksPastGraceAndTheirClicks()
    {
        // Default grace period is 7 days; the cleaner deletes links with ExpiresAt < now - 7 days.
        // Both expired links sit 5 minutes either side of that cutoff (the margin absorbs the time
        // between this line and the cleaner reading its own clock).
        var now = DateTime.UtcNow;
        var pastGraceCode = await InsertLinkAsync(_factory, now.AddDays(-7).AddMinutes(-5), UniqueUrl());
        var inGraceCode = await InsertLinkAsync(_factory, now.AddDays(-7).AddMinutes(5), UniqueUrl());
        var activeCode = await InsertLinkAsync(_factory, now.AddDays(1), UniqueUrl());

        var pastGraceId = await LinkIdAsync(_factory, pastGraceCode);
        var inGraceId = await LinkIdAsync(_factory, inGraceCode);
        var activeId = await LinkIdAsync(_factory, activeCode);

        var pastGraceClick = await InsertClickAsync(pastGraceId);
        var inGraceClick = await InsertClickAsync(inGraceId);
        var activeClick = await InsertClickAsync(activeId);

        using (var scope = _factory.Services.CreateScope())
        {
            var cleaner = scope.ServiceProvider.GetRequiredService<ExpiredLinkCleaner>();
            await cleaner.RunOnceAsync(CancellationToken.None);
        }

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var remainingLinks = await db.ShortUrls.AsNoTracking()
            .Where(x => x.Id == pastGraceId || x.Id == inGraceId || x.Id == activeId)
            .Select(x => x.Id)
            .ToListAsync();
        var remainingClicks = await db.ClickEvents.AsNoTracking()
            .Where(c => c.Id == pastGraceClick || c.Id == inGraceClick || c.Id == activeClick)
            .Select(c => c.Id)
            .ToListAsync();

        remainingLinks.Should().BeEquivalentTo(new[] { inGraceId, activeId });
        remainingClicks.Should().BeEquivalentTo(new[] { inGraceClick, activeClick });
    }

    [Fact]
    public async Task Cleanup_LinkExpiredInsideGracePeriod_StillAnswers410()
    {
        // Expired 6 days ago, inside the default 7-day grace: the pass must leave it alone.
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddDays(-6), UniqueUrl());

        using (var scope = _factory.Services.CreateScope())
        {
            var cleaner = scope.ServiceProvider.GetRequiredService<ExpiredLinkCleaner>();
            await cleaner.RunOnceAsync(CancellationToken.None);
        }

        var response = await VisitAsync(_client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    // ---- Oldest expired links go first (CLN-AC-10) -----------------------------------------------

    [Fact]
    public async Task DeleteExpired_MoreExpiredThanBatchSize_DeletesOldestFirst()
    {
        // 2001 dates are older than anything other tests insert, so the cutoff only touches this test's rows.
        var oldest = await InsertLinkAsync(_factory, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), UniqueUrl());
        var middle = await InsertLinkAsync(_factory, new DateTime(2001, 1, 2, 0, 0, 0, DateTimeKind.Utc), UniqueUrl());
        var newest = await InsertLinkAsync(_factory, new DateTime(2001, 1, 3, 0, 0, 0, DateTimeKind.Utc), UniqueUrl());

        int deleted;
        using (var scope = _factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IShortUrlRepository>();
            deleted = await repository.DeleteExpiredAsync(
                new DateTime(2001, 1, 10, 0, 0, 0, DateTimeKind.Utc), batchSize: 2, CancellationToken.None);
        }

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.ShortUrls
            .Where(x => x.Code == oldest || x.Code == middle || x.Code == newest)
            .ToListAsync();

        deleted.Should().Be(2);
        remaining.Select(x => x.Code).Should().BeEquivalentTo(new[] { newest });

        // Leave nothing behind for the next run.
        db.ShortUrls.RemoveRange(remaining);
        await db.SaveChangesAsync();
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>Inserts one click directly, bypassing the click writer. Returns the ClickEvent id.</summary>
    private async Task<long> InsertClickAsync(long shortUrlId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var click = new ClickEvent
        {
            ShortUrlId = shortUrlId,
            ClickedAt = DateTime.UtcNow.AddDays(-10),
            VisitorHash = new string('a', 64),
            Country = "US",
            Browser = "Chrome"
        };
        db.ClickEvents.Add(click);
        await db.SaveChangesAsync();
        return click.Id;
    }
}
