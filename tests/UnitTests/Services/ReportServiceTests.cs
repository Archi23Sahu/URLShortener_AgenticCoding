using FluentAssertions;
using Moq;
using UrlShortener.Entities;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class ReportServiceTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 3, 15, 23, 59, 0, TimeSpan.Zero);
    }

    private readonly Mock<IShortUrlRepository> _repo = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private ReportService Create()
    {
        _repo.Setup(r => r.GetByCodeAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrl { Id = 7, Code = "abc" });
        return new ReportService(_repo.Object, new FixedClock());
    }

    [Fact]
    public async Task Daily_NoDates_UsesLastThirtyDaysEndingToday()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow>());

        var result = await Create().GetDailyAsync("abc", null, null, _ct);

        result.To.Should().Be(new DateOnly(2026, 3, 15));
        result.From.Should().Be(new DateOnly(2026, 2, 14));
        result.Days.Should().HaveCount(30);
        _repo.Verify(r => r.GetDailyClicksAsync(7, new DateTime(2026, 2, 14), new DateTime(2026, 3, 16), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Daily: one open bound (RPT-AC-11) ----

    [Fact]
    public async Task Daily_OnlyFrom_EndsToday()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow>());

        var result = await Create().GetDailyAsync("abc", new DateOnly(2026, 3, 1), null, _ct);

        result.From.Should().Be(new DateOnly(2026, 3, 1));
        result.To.Should().Be(new DateOnly(2026, 3, 15));
        result.Days.Should().HaveCount(15);
        _repo.Verify(r => r.GetDailyClicksAsync(7, new DateTime(2026, 3, 1), new DateTime(2026, 3, 16), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Daily_OnlyTo_StartsTwentyNineDaysBefore()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow>());

        var result = await Create().GetDailyAsync("abc", null, new DateOnly(2026, 3, 10), _ct);

        result.To.Should().Be(new DateOnly(2026, 3, 10));
        result.From.Should().Be(new DateOnly(2026, 2, 9));
        result.Days.Should().HaveCount(30);
    }

    [Fact]
    public async Task Daily_FillsMissingDaysWithZero()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow> { new(new DateTime(2026, 3, 2), 5, 3) });

        var result = await Create().GetDailyAsync("abc", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 3), _ct);

        result.Days.Select(d => (d.Date.Day, d.Clicks, d.UniqueVisitors))
            .Should().Equal((1, 0L, 0L), (2, 5L, 3L), (3, 0L, 0L));
        result.TotalClicks.Should().Be(5);
    }

    [Fact]
    public async Task Daily_SingleDay_ReturnsOneEntry()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow>());

        var result = await Create().GetDailyAsync("abc", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), _ct);

        result.Days.Should().ContainSingle();
    }

    [Fact]
    public async Task Daily_FromAfterTo_Throws()
    {
        var act = () => Create().GetDailyAsync("abc", new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 1), _ct);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Daily_RangeLongerThanLimit_Throws()
    {
        var act = () => Create().GetDailyAsync("abc", new DateOnly(2025, 1, 1), new DateOnly(2026, 3, 1), _ct);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- Edge of the supported date range (RPT-AC-15) ----

    [Theory]
    [InlineData(0)] // DateOnly.MinValue: the default start (to - 29 days) is before 0001-01-01
    [InlineData(1)] // DateOnly.MaxValue: the exclusive upper bound (to + 1 day) is past 9999-12-31
    public async Task Daily_ToAtEdgeOfSupportedRange_ThrowsInvalidRequestNotArgumentOutOfRange(int edge)
    {
        var to = edge == 0 ? DateOnly.MinValue : DateOnly.MaxValue;

        var act = () => Create().GetDailyAsync("abc", null, to, _ct);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Countries_ToIsMaxDate_ThrowsInvalidRequest()
    {
        var act = () => Create().GetCountriesAsync("abc", null, DateOnly.MaxValue, _ct);
        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Browsers_ToIsMaxDate_ThrowsInvalidRequest()
    {
        var act = () => Create().GetBrowsersAsync("abc", null, DateOnly.MaxValue, _ct);
        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Daily_ExactlyMaxRange_IsAllowed()
    {
        _repo.Setup(r => r.GetDailyClicksAsync(7, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DailyClickRow>());
        var to = new DateOnly(2026, 3, 1);

        var result = await Create().GetDailyAsync("abc", to.AddDays(-(ReportService.MaxRangeDays - 1)), to, _ct);

        result.Days.Should().HaveCount(ReportService.MaxRangeDays);
    }

    [Fact]
    public async Task AllReports_UnknownCode_ThrowNotFound()
    {
        _repo.Setup(r => r.GetByCodeAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);
        var service = new ReportService(_repo.Object, new FixedClock());

        await ((Func<Task>)(() => service.GetDailyAsync("nope", null, null, _ct))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => service.GetCountriesAsync("nope", null, null, _ct))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => service.GetBrowsersAsync("nope", null, null, _ct))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Countries_CountsUnknownSeparatelyAndSortsByClicks()
    {
        _repo.Setup(r => r.GetCountryBreakdownAsync(7, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow> { new("IN", 2), new(null, 4), new("US", 3), new("DE", 1) });

        var result = await Create().GetCountriesAsync("abc", null, null, _ct);

        result.TotalClicks.Should().Be(10);
        result.UnknownClicks.Should().Be(4);
        result.Countries.Select(c => c.Country).Should().Equal("US", "IN", "DE");
        result.Countries[0].Percentage.Should().Be(30.0);
    }

    [Fact]
    public async Task Countries_NoClicks_ReturnsEmptyWithZeroTotals()
    {
        _repo.Setup(r => r.GetCountryBreakdownAsync(7, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        var result = await Create().GetCountriesAsync("abc", null, null, _ct);

        result.TotalClicks.Should().Be(0);
        result.Countries.Should().BeEmpty();
    }

    [Fact]
    public async Task Countries_PassesInclusiveRangeAsHalfOpenInterval()
    {
        _repo.Setup(r => r.GetCountryBreakdownAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        await Create().GetCountriesAsync("abc", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 3), _ct);

        _repo.Verify(r => r.GetCountryBreakdownAsync(7, new DateTime(2026, 3, 1), new DateTime(2026, 3, 4), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Countries_FromAfterTo_Throws()
    {
        var act = () => Create().GetCountriesAsync("abc", new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 1), _ct);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- Countries / browsers: one open bound is unbounded (RPT-AC-14) ----

    [Fact]
    public async Task Countries_OnlyFromWithHugeSpan_PassesOpenUpperBound()
    {
        _repo.Setup(r => r.GetCountryBreakdownAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        await Create().GetCountriesAsync("abc", new DateOnly(2024, 3, 15), null, _ct);

        _repo.Verify(r => r.GetCountryBreakdownAsync(7, new DateTime(2024, 3, 15), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Countries_OnlyTo_PassesOpenLowerBound()
    {
        _repo.Setup(r => r.GetCountryBreakdownAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        await Create().GetCountriesAsync("abc", null, new DateOnly(2026, 3, 3), _ct);

        _repo.Verify(r => r.GetCountryBreakdownAsync(7, null, new DateTime(2026, 3, 4), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Browsers_OnlyFromWithHugeSpan_PassesOpenUpperBound()
    {
        _repo.Setup(r => r.GetBrowserBreakdownAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        await Create().GetBrowsersAsync("abc", new DateOnly(2024, 3, 15), null, _ct);

        _repo.Verify(r => r.GetBrowserBreakdownAsync(7, new DateTime(2024, 3, 15), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Browsers_OnlyTo_PassesOpenLowerBound()
    {
        _repo.Setup(r => r.GetBrowserBreakdownAsync(7, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow>());

        await Create().GetBrowsersAsync("abc", null, new DateOnly(2026, 3, 3), _ct);

        _repo.Verify(r => r.GetBrowserBreakdownAsync(7, null, new DateTime(2026, 3, 4), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Browsers_NullBrowserIsReportedAsUnknownAndMergedWithExistingUnknown()
    {
        _repo.Setup(r => r.GetBrowserBreakdownAsync(7, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow> { new("Chrome", 6), new(null, 3), new("Unknown", 1) });

        var result = await Create().GetBrowsersAsync("abc", null, null, _ct);

        result.TotalClicks.Should().Be(10);
        result.Browsers.Select(b => (b.Browser, b.Clicks)).Should().Equal(("Chrome", 6L), ("Unknown", 4L));
        result.Browsers[0].Percentage.Should().Be(60.0);
    }

    [Fact]
    public async Task Browsers_PercentagesRoundToOneDecimal()
    {
        _repo.Setup(r => r.GetBrowserBreakdownAsync(7, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BreakdownRow> { new("Chrome", 1), new("Firefox", 2) });

        var result = await Create().GetBrowsersAsync("abc", null, null, _ct);

        result.Browsers.Select(b => b.Percentage).Should().Equal(66.7, 33.3);
    }
}
