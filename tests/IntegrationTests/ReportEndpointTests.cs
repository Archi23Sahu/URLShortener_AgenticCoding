using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Repositories;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class ReportEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ReportEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    /// <summary>Inserts clicks directly (bypassing the click writer) so timestamps are exact.</summary>
    private async Task InsertClicksAsync(string code, params (DateTime ClickedAt, string VisitorHash)[] clicks)
    {
        var id = await LinkIdAsync(_factory, code);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (clickedAt, visitorHash) in clicks)
        {
            db.ClickEvents.Add(new ClickEvent
            {
                ShortUrlId = id,
                ClickedAt = clickedAt,
                VisitorHash = visitorHash,
                Country = "US",
                Browser = "Chrome"
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<string> LinkWithClicksAsync()
    {
        var (code, _) = await CreateAsync(_client);
        (await VisitAsync(_client, code, ChromeAgent, "IN")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, ChromeAgent, "IN")).StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0", "US"))
            .StatusCode.Should().Be(HttpStatusCode.Found);
        (await VisitAsync(_client, code, "curl/8.4.0")).StatusCode.Should().Be(HttpStatusCode.Found);
        return code;
    }

    [Fact]
    public async Task Countries_ReportsKnownAndUnknown()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<CountryBreakdownResponse>($"/api/v1/analytics/{code}/countries"))!,
            r => r.TotalClicks >= 4);

        report.TotalClicks.Should().Be(4);
        report.UnknownClicks.Should().Be(1);
        report.Countries.Select(c => (c.Country, c.Clicks)).Should().Equal(("IN", 2L), ("US", 1L));
        report.Countries[0].Percentage.Should().Be(50.0);
    }

    [Fact]
    public async Task Browsers_ReportsDetectedFamilies()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<BrowserBreakdownResponse>($"/api/v1/analytics/{code}/browsers"))!,
            r => r.TotalClicks >= 4);

        report.Browsers.Select(b => (b.Browser, b.Clicks)).Should().Equal(("Chrome", 2L), ("Bot", 1L), ("Firefox", 1L));
    }

    [Fact]
    public async Task Daily_DefaultRange_IsThirtyDaysEndingTodayWithTodaysClicks()
    {
        var code = await LinkWithClicksAsync();

        var report = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<DailyClicksResponse>($"/api/v1/analytics/{code}/daily"))!,
            r => r.TotalClicks >= 4);

        report.Days.Should().HaveCount(30);
        report.To.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
        report.TotalClicks.Should().Be(4);
        report.Days[^1].Clicks.Should().Be(4);
        report.Days[^1].UniqueVisitors.Should().BeGreaterThan(0);
        report.Days.Take(29).Should().OnlyContain(d => d.Clicks == 0);
    }

    [Fact]
    public async Task Daily_ExplicitRange_ReturnsOneEntryPerDay()
    {
        var (code, _) = await CreateAsync(_client);

        var report = await _client.GetFromJsonAsync<DailyClicksResponse>(
            $"/api/v1/analytics/{code}/daily?from=2026-01-01&to=2026-01-10");

        report!.Days.Should().HaveCount(10);
        report.TotalClicks.Should().Be(0);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_FromAfterTo_Return400(string report)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/{report}?from=2026-02-10&to=2026-02-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_NotADate_Return400(string report)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/{report}?from=30&to=366");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Daily_RangeOverOneYear_Returns400()
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/daily?from=2024-01-01&to=2026-01-01");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Edge of the supported date range (RPT-AC-15) --------------------------------------------

    [Theory]
    [InlineData("daily", "0001-01-01")]
    [InlineData("daily", "9999-12-31")]
    [InlineData("countries", "9999-12-31")]
    [InlineData("browsers", "9999-12-31")]
    public async Task Reports_ToAtEdgeOfSupportedRange_Return400ProblemJson(string report, string to)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.GetAsync($"/api/v1/analytics/{code}/{report}?to={to}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    // ---- Real SQL date grouping (RPT-AC-12) ------------------------------------------------------

    [Fact]
    public async Task Daily_ClicksAroundMidnight_LandOnDifferentDaysWithPerDayUniqueVisitors()
    {
        var (code, _) = await CreateAsync(_client);
        var hashA = new string('a', 64);
        var hashB = new string('b', 64);
        var hashC = new string('c', 64);
        await InsertClicksAsync(code,
            (new DateTime(2026, 1, 10, 23, 59, 59, DateTimeKind.Utc), hashA),
            (new DateTime(2026, 1, 10, 23, 59, 59, DateTimeKind.Utc), hashB),
            (new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc), hashC),
            (new DateTime(2026, 1, 11, 0, 0, 1, DateTimeKind.Utc), hashC));

        var report = await _client.GetFromJsonAsync<DailyClicksResponse>(
            $"/api/v1/analytics/{code}/daily?from=2026-01-10&to=2026-01-11");

        report!.TotalClicks.Should().Be(4);
        report.Days.Select(d => (d.Date, d.Clicks, d.UniqueVisitors)).Should().Equal(
            (new DateOnly(2026, 1, 10), 2L, 2L),
            (new DateOnly(2026, 1, 11), 2L, 1L));
    }

    // ---- Expired link not yet cleaned up (RPT-AC-13) ---------------------------------------------

    [Fact]
    public async Task Reports_ExpiredLinkNotYetCleaned_StillReturnHistory()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddDays(-2));
        await InsertClicksAsync(code, (DateTime.UtcNow.AddDays(-3), new string('d', 64)));

        var summary = await _client.GetAsync($"/api/v1/analytics/{code}");
        var daily = await _client.GetAsync($"/api/v1/analytics/{code}/daily?from=2020-01-01&to=2020-01-02");
        var dailyDefault = await _client.GetAsync($"/api/v1/analytics/{code}/daily");
        var countries = await _client.GetAsync($"/api/v1/analytics/{code}/countries");
        var browsers = await _client.GetAsync($"/api/v1/analytics/{code}/browsers");

        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        (await summary.Content.ReadFromJsonAsync<AnalyticsResponse>())!.Clicks.Should().Be(1);
        daily.StatusCode.Should().Be(HttpStatusCode.OK);
        dailyDefault.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dailyDefault.Content.ReadFromJsonAsync<DailyClicksResponse>())!.TotalClicks.Should().Be(1);
        countries.StatusCode.Should().Be(HttpStatusCode.OK);
        (await countries.Content.ReadFromJsonAsync<CountryBreakdownResponse>())!.TotalClicks.Should().Be(1);
        browsers.StatusCode.Should().Be(HttpStatusCode.OK);
        (await browsers.Content.ReadFromJsonAsync<BrowserBreakdownResponse>())!.TotalClicks.Should().Be(1);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("countries")]
    [InlineData("browsers")]
    public async Task Reports_UnknownCode_Return404(string report)
    {
        var response = await _client.GetAsync($"/api/v1/analytics/zzzzzz9/{report}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
