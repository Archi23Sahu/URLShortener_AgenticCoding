using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using UrlShortener.Analytics;
using UrlShortener.Configurations;
using UrlShortener.Controllers;
using UrlShortener.Exceptions;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Controllers;

public class RedirectControllerTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private readonly Mock<IUrlShortenerService> _service = new();
    private readonly Mock<IClickQueue> _queue = new();
    private readonly TestMetrics _telemetry = TestMetrics.Create();

    private RedirectController Create()
    {
        // Real ClickTracker so the test proves the controller never reaches Track, not just a mocked seam.
        var tracker = new ClickTracker(
            _queue.Object,
            Mock.Of<ICountryResolver>(),
            new FixedClock(),
            Options.Create(new AnalyticsOptions { Salt = "s" }),
            _telemetry.Metrics,
            NullLogger<ClickTracker>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        ctx.Request.Headers.UserAgent = "TestAgent/1.0";

        return new RedirectController(_service.Object, tracker, _telemetry.Metrics)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx }
        };
    }

    /// <summary>The single redirect measurement must be +1 with this outcome tag.</summary>
    private static void ShouldHaveRecordedOutcome(MetricCollector<long> redirects, string outcome)
    {
        var measurement = redirects.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags["outcome"].Should().Be(outcome);
    }

    // ---- one click on a successful redirect (RED-AC-08) ----

    [Fact]
    public async Task Redirect_KnownCode_QueuesOneClickAndReturns302()
    {
        // Positive control: proves this harness reaches the queue, so the Times.Never tests below
        // cannot pass just because ClickTracker.Track swallowed an exception.
        const long id = 42;
        const string url = "https://example.com/target";
        _service.Setup(s => s.ResolveAsync("abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedLink(id, url, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        _queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(true);
        var sut = Create();

        var result = await sut.RedirectToTarget("abc123", CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be(url);
        redirect.Permanent.Should().BeFalse();
        _queue.Verify(q => q.TryEnqueue(It.Is<ClickRecord>(c => c.ShortUrlId == id)), Times.Once);
    }

    // ---- no click on failed redirects (RED-AC-08) ----

    [Fact]
    public async Task Redirect_UnknownCode_QueuesNoClick()
    {
        _service.Setup(s => s.ResolveAsync("nope123", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("nope123"));
        var sut = Create();

        Func<Task> act = () => sut.RedirectToTarget("nope123", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        _queue.Verify(q => q.TryEnqueue(It.IsAny<ClickRecord>()), Times.Never);
    }

    [Fact]
    public async Task Redirect_ExpiredLink_QueuesNoClick()
    {
        _service.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LinkExpiredException("100001"));
        var sut = Create();

        Func<Task> act = () => sut.RedirectToTarget("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
        _queue.Verify(q => q.TryEnqueue(It.IsAny<ClickRecord>()), Times.Never);
    }

    // ---- redirect outcome metric (OBS-AC-02) ----

    [Fact]
    public async Task Redirect_KnownCode_RecordsFoundOutcome()
    {
        using var redirects = _telemetry.Collect<long>("urlshortener.redirects");
        _service.Setup(s => s.ResolveAsync("abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedLink(42, "https://example.com/target", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        _queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(true);
        var sut = Create();

        await sut.RedirectToTarget("abc123", CancellationToken.None);

        ShouldHaveRecordedOutcome(redirects, "found");
    }

    [Fact]
    public async Task Redirect_UnknownCode_RecordsNotFoundOutcome()
    {
        using var redirects = _telemetry.Collect<long>("urlshortener.redirects");
        _service.Setup(s => s.ResolveAsync("nope123", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotFoundException("nope123"));
        var sut = Create();

        Func<Task> act = () => sut.RedirectToTarget("nope123", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        ShouldHaveRecordedOutcome(redirects, "not_found");
    }

    [Fact]
    public async Task Redirect_ExpiredLink_RecordsExpiredOutcome()
    {
        using var redirects = _telemetry.Collect<long>("urlshortener.redirects");
        _service.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LinkExpiredException("100001"));
        var sut = Create();

        Func<Task> act = () => sut.RedirectToTarget("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
        ShouldHaveRecordedOutcome(redirects, "expired");
    }
}
