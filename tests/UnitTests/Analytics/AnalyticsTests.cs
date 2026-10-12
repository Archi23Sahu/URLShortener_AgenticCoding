using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using UrlShortener.Analytics;
using UrlShortener.Configurations;

namespace UrlShortener.UnitTests.Analytics;

public class VisitorHasherTests
{
    [Fact]
    public void Compute_SameInputs_GiveSameHash()
    {
        VisitorHasher.Compute("1.2.3.4", "Mozilla", "salt")
            .Should().Be(VisitorHasher.Compute("1.2.3.4", "Mozilla", "salt"));
    }

    [Fact]
    public void Compute_IsSixtyFourLowerCaseHexCharacters()
    {
        VisitorHasher.Compute("1.2.3.4", "Mozilla", "salt").Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("1.2.3.5", "Mozilla", "salt")]   // different IP
    [InlineData("1.2.3.4", "Chrome", "salt")]    // different browser
    [InlineData("1.2.3.4", "Mozilla", "other")]  // different salt
    public void Compute_AnyInputChange_ChangesHash(string ip, string ua, string salt)
    {
        VisitorHasher.Compute(ip, ua, salt)
            .Should().NotBe(VisitorHasher.Compute("1.2.3.4", "Mozilla", "salt"));
    }

    [Fact]
    public void Compute_DoesNotContainRawIp()
    {
        VisitorHasher.Compute("203.0.113.9", "Mozilla", "salt").Should().NotContain("203.0.113.9");
    }

    [Fact]
    public void Compute_NullInputs_DoNotThrow()
    {
        var act = () => VisitorHasher.Compute(null, null, "salt");

        act.Should().NotThrow();
    }
}

public class HeaderCountryResolverTests
{
    private static HeaderCountryResolver Resolver(string header = "CF-IPCountry") =>
        new(Options.Create(new AnalyticsOptions { CountryHeader = header }));

    private static DefaultHttpContext ContextWith(string? headerValue)
    {
        var ctx = new DefaultHttpContext();
        if (headerValue is not null)
        {
            ctx.Request.Headers["CF-IPCountry"] = headerValue;
        }
        return ctx;
    }

    [Theory]
    [InlineData("US", "US")]
    [InlineData("de", "DE")]
    [InlineData(" IN ", "IN")]
    public void Resolve_ValidCode_ReturnsUpperCase(string header, string expected)
    {
        Resolver().Resolve(ContextWith(header)).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("USA")]
    [InlineData("1A")]
    [InlineData("U")]
    [InlineData("XX")]
    [InlineData("T1")]
    [InlineData("'; DROP TABLE")]
    public void Resolve_InvalidOrUnknown_ReturnsNull(string header)
    {
        Resolver().Resolve(ContextWith(header)).Should().BeNull();
    }

    [Fact]
    public void Resolve_MissingHeader_ReturnsNull()
    {
        Resolver().Resolve(ContextWith(null)).Should().BeNull();
    }

    [Fact]
    public void Resolve_HeaderNameNotConfigured_ReturnsNull()
    {
        Resolver(header: "").Resolve(ContextWith("US")).Should().BeNull();
    }
}

public class ChannelClickQueueTests
{
    private static ClickRecord Click(long id = 1) => new(id, DateTime.UtcNow, new string('a', 64), null);

    private static ChannelClickQueue Queue(int capacity) =>
        new(Options.Create(new AnalyticsOptions { QueueCapacity = capacity }));

    [Fact]
    public void TryEnqueue_WithSpace_ReturnsTrueAndItemCanBeRead()
    {
        var queue = Queue(2);

        queue.TryEnqueue(Click(7)).Should().BeTrue();

        queue.Reader.TryRead(out var item).Should().BeTrue();
        item!.ShortUrlId.Should().Be(7);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ReturnsFalseWithoutBlocking()
    {
        var queue = Queue(2);
        queue.TryEnqueue(Click()).Should().BeTrue();
        queue.TryEnqueue(Click()).Should().BeTrue();

        queue.TryEnqueue(Click()).Should().BeFalse();
    }

    [Fact]
    public void TryEnqueue_AfterReading_HasSpaceAgain()
    {
        var queue = Queue(1);
        queue.TryEnqueue(Click()).Should().BeTrue();
        queue.TryEnqueue(Click()).Should().BeFalse();

        queue.Reader.TryRead(out _);

        queue.TryEnqueue(Click()).Should().BeTrue();
    }
}

public class ClickTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ClickTracker Tracker(IClickQueue queue, ICountryResolver? country = null, TestMetrics? telemetry = null) =>
        new(queue,
            country ?? Mock.Of<ICountryResolver>(),
            new FixedClock(),
            Options.Create(new AnalyticsOptions { Salt = "s" }),
            (telemetry ?? TestMetrics.Create()).Metrics,
            NullLogger<ClickTracker>.Instance);

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        ctx.Request.Headers.UserAgent = "TestAgent/1.0";
        return ctx;
    }

    [Fact]
    public void Track_QueuesClickWithLinkIdTimeHashAndCountry()
    {
        ClickRecord? captured = null;
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>()))
            .Callback<ClickRecord>(c => captured = c)
            .Returns(true);
        var country = new Mock<ICountryResolver>();
        country.Setup(c => c.Resolve(It.IsAny<HttpContext>())).Returns("US");

        Tracker(queue.Object, country.Object).Track(Context(), 42);

        captured.Should().NotBeNull();
        captured!.ShortUrlId.Should().Be(42);
        captured.ClickedAt.Should().Be(Now.UtcDateTime);
        captured.Country.Should().Be("US");
        captured.VisitorHash.Should().Be(VisitorHasher.Compute("203.0.113.9", "TestAgent/1.0", "s"));
    }

    [Fact]
    public void Track_QueueFull_DoesNotThrow()
    {
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(false);

        var act = () => Tracker(queue.Object).Track(Context(), 1);

        act.Should().NotThrow();
    }

    [Fact]
    public void Track_QueueThrows_DoesNotThrow()
    {
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Throws<InvalidOperationException>();

        var act = () => Tracker(queue.Object).Track(Context(), 1);

        act.Should().NotThrow();
    }

    // ---- click metrics (OBS-AC-04) ----

    [Fact]
    public void Track_QueueAccepts_IncrementsClicksQueued()
    {
        var telemetry = TestMetrics.Create();
        using var queued = telemetry.Collect<long>("urlshortener.clicks.queued");
        using var dropped = telemetry.Collect<long>("urlshortener.clicks.dropped");
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(true);

        Tracker(queue.Object, telemetry: telemetry).Track(Context(), 1);

        queued.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(1);
        dropped.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Track_QueueFull_IncrementsClicksDroppedWithQueueFullReason()
    {
        var telemetry = TestMetrics.Create();
        using var queued = telemetry.Collect<long>("urlshortener.clicks.queued");
        using var dropped = telemetry.Collect<long>("urlshortener.clicks.dropped");
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>())).Returns(false);

        Tracker(queue.Object, telemetry: telemetry).Track(Context(), 1);

        var drop = dropped.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        drop.Value.Should().Be(1);
        drop.Tags["reason"].Should().Be("queue_full");
        queued.GetMeasurementSnapshot().Should().BeEmpty();
    }
}

public class ClickTrackerBrowserTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0", "Firefox")]
    [InlineData("curl/8.4.0", "Bot")]
    [InlineData("", "Unknown")]
    public void Track_StoresBrowserDetectedFromUserAgent(string userAgent, string expected)
    {
        ClickRecord? captured = null;
        var queue = new Mock<IClickQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<ClickRecord>()))
            .Callback<ClickRecord>(c => captured = c)
            .Returns(true);
        var tracker = new ClickTracker(queue.Object, Mock.Of<ICountryResolver>(), TimeProvider.System,
            Options.Create(new AnalyticsOptions { Salt = "s" }), TestMetrics.Create().Metrics, NullLogger<ClickTracker>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        ctx.Request.Headers.UserAgent = userAgent;

        tracker.Track(ctx, 1);

        captured!.Browser.Should().Be(expected);
    }
}
