using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Analytics;
using UrlShortener.DTOs;
using UrlShortener.Repositories;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Drives the click writer directly through the singleton IClickQueue: going through HTTP cannot
/// produce a click for a link that does not exist, and cannot control what is still queued at shutdown.
/// </summary>
public class ClickPipelineTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ClickPipelineTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task FlushBatch_ClickForMissingLink_OtherClicksAreStored()
    {
        var (code, _) = await CreateAsync(_client);
        var linkId = await LinkIdAsync(_factory, code);
        var queue = _factory.Services.GetRequiredService<IClickQueue>();

        // CLK-AC-09: all three are enqueued synchronously so they land in the same flush window;
        // the missing link (-1) makes the batch insert fail with an FK violation.
        // Assumes ApiFactory's settings: Analytics:BatchSize stays at its default (100, i.e. > 3) and
        // Analytics:FlushIntervalSeconds is 1, so the bad click shares a batch with the good ones.
        queue.TryEnqueue(QueuedClick(linkId)).Should().BeTrue();
        queue.TryEnqueue(QueuedClick(-1)).Should().BeTrue();
        queue.TryEnqueue(QueuedClick(linkId)).Should().BeTrue();

        var stored = await EventuallyAsync(() => CountClicksAsync(_factory, linkId), n => n >= 2);

        stored.Should().Be(2, "the click for the missing link must not discard the valid clicks in its batch");
    }

    // ---- Unique visitors (CLK-AC-11) -------------------------------------------------------------

    [Fact]
    public async Task Summary_TwoVisitsWithSameUserAgent_CountsTwoClicksOneVisitor()
    {
        var (code, _) = await CreateAsync(_client);
        (await VisitAsync(_client, code, ChromeAgent)).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, ChromeAgent)).StatusCode.Should().Be(HttpStatusCode.Found);

        var summary = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<AnalyticsResponse>($"/api/v1/analytics/{code}"))!,
            s => s.Clicks >= 2);

        summary.Clicks.Should().Be(2);
        summary.UniqueVisitors.Should().Be(1, "both visits come from the same IP and User-Agent");
    }

    [Fact]
    public async Task Summary_VisitsWithDifferentUserAgents_CountAsDifferentVisitors()
    {
        var (code, _) = await CreateAsync(_client);
        (await VisitAsync(_client, code, ChromeAgent)).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, ChromeAgent)).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0"))
            .StatusCode.Should().Be(HttpStatusCode.Found);

        var summary = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<AnalyticsResponse>($"/api/v1/analytics/{code}"))!,
            s => s.Clicks >= 3);

        summary.Clicks.Should().Be(3);
        summary.UniqueVisitors.Should().Be(2);
    }
}

/// <summary>
/// Own host whose click writer never flushes on a timer during the test (5 minute window, batch of 1000),
/// so the only way queued clicks reach the database is the graceful-shutdown drain.
/// </summary>
public class FlushOnShutdownFactory : ApiFactory
{
    public FlushOnShutdownFactory() : base(new Dictionary<string, string?>
    {
        ["Analytics:FlushIntervalSeconds"] = "300",
        ["Analytics:BatchSize"] = "1000"
    })
    {
    }
}

public class ClickShutdownTests
{
    [Fact]
    public async Task Shutdown_QueuedClicks_AreWrittenBeforeStop()
    {
        const int clicks = 5;
        long linkId;

        // The factory is created and disposed here (not a class fixture): disposing it stops the host,
        // which is the shutdown path under test (CLK-AC-10).
        await using (var factory = new FlushOnShutdownFactory())
        {
            var client = factory.CreateNoRedirectClient();
            var (code, _) = await CreateAsync(client);
            linkId = await LinkIdAsync(factory, code);
            var queue = factory.Services.GetRequiredService<IClickQueue>();

            for (var i = 0; i < clicks; i++)
            {
                queue.TryEnqueue(QueuedClick(linkId)).Should().BeTrue();
            }
        }

        // The factory's services are gone after dispose, so read through a fresh context
        // (the shared CountClicksAsync helper needs a live factory).
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ApiFactory.ConnectionString).Options;
        await using var db = new AppDbContext(options);
        var stored = await db.ClickEvents.CountAsync(c => c.ShortUrlId == linkId);

        stored.Should().Be(clicks, "clicks still queued at a graceful shutdown must be written before the host stops");
    }
}
