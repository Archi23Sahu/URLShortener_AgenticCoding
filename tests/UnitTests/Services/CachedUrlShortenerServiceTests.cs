using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using UrlShortener.Caching;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Exceptions;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class CachedUrlShortenerServiceTests
{
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string CacheLookups = "urlshortener.cache.lookups";

    private readonly MutableClock _clock = new();
    private readonly Mock<IUrlShortenerService> _inner = new();
    private readonly TestMetrics _telemetry = TestMetrics.Create();

    private DateTime UtcNow => _clock.Now.UtcDateTime;

    private ResolvedLink Link(TimeSpan lifetime, long id = 7, string url = "https://example.com/a") =>
        new(id, url, UtcNow + lifetime);

    private CachedUrlShortenerService Create(IDistributedCache cache, int ttlMinutes = 10, int cooldownSeconds = 30)
    {
        var options = Options.Create(new CacheOptions { TtlMinutes = ttlMinutes, FailureCooldownSeconds = cooldownSeconds });
        return new CachedUrlShortenerService(
            _inner.Object, cache, new CacheHealth(_clock, options), _clock, options, _telemetry.Metrics,
            NullLogger<CachedUrlShortenerService>.Instance);
    }

    /// <summary>The result tag of every cache lookup recorded so far, in order.</summary>
    private static IEnumerable<object?> LookupResults(MetricCollector<long> lookups)
    {
        var snapshot = lookups.GetMeasurementSnapshot();
        snapshot.Should().OnlyContain(m => m.Value == 1);
        return snapshot.Select(m => m.Tags["result"]);
    }

    private static IDistributedCache MemoryCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    // ---- hit / miss ---------------------------------------------------------------------

    [Fact]
    public async Task Resolve_Miss_ReadsInnerAndStores_ThenSecondCallIsAHit()
    {
        var cache = MemoryCache();
        var link = Link(TimeSpan.FromDays(1));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var sut = Create(cache);

        var first = await sut.ResolveAsync("100001", CancellationToken.None);
        var second = await sut.ResolveAsync("100001", CancellationToken.None);

        first.Should().Be(link);
        second.Should().Be(link);
        _inner.Verify(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()), Times.Once);
        (await cache.GetAsync("link:100001")).Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_Hit_DoesNotCallInner()
    {
        var cache = MemoryCache();
        var link = Link(TimeSpan.FromDays(1), id: 99, url: "https://cached.example.com");
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(link));
        var sut = Create(cache);

        var result = await sut.ResolveAsync("100001", CancellationToken.None);

        result.Id.Should().Be(99);
        result.Url.Should().Be("https://cached.example.com");
        _inner.Verify(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_CachedEntryIsPastExpiry_FallsBackToInner()
    {
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(Link(TimeSpan.FromSeconds(-1))));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LinkExpiredException("100001"));
        var sut = Create(cache);

        Func<Task> act = () => sut.ResolveAsync("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
    }

    [Fact]
    public async Task Resolve_CodesAreCaseSensitive()
    {
        var cache = MemoryCache();
        _inner.Setup(s => s.ResolveAsync("aB1234", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1), 1, "https://one.example.com"));
        _inner.Setup(s => s.ResolveAsync("Ab1234", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1), 2, "https://two.example.com"));
        var sut = Create(cache);

        var a = await sut.ResolveAsync("aB1234", CancellationToken.None);
        var b = await sut.ResolveAsync("Ab1234", CancellationToken.None);

        a.Url.Should().Be("https://one.example.com");
        b.Url.Should().Be("https://two.example.com");
    }

    [Fact]
    public async Task Resolve_InnerThrowsNotFound_PropagatesAndCachesNothing()
    {
        var cache = MemoryCache();
        _inner.Setup(s => s.ResolveAsync("nope", It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("nope"));
        var sut = Create(cache);

        Func<Task> act = () => sut.ResolveAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        (await cache.GetAsync("link:nope")).Should().BeNull();
    }

    [Fact]
    public async Task Resolve_CorruptCacheEntry_IsTreatedAsMissAndOverwritten()
    {
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", new byte[] { 1, 2, 3 });
        var link = Link(TimeSpan.FromDays(1));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var sut = Create(cache);

        var result = await sut.ResolveAsync("100001", CancellationToken.None);

        result.Should().Be(link);
        JsonSerializer.Deserialize<ResolvedLink>((await cache.GetAsync("link:100001"))!).Should().Be(link);
    }

    // ---- TTL ----------------------------------------------------------------------------

    [Theory]
    [InlineData(60 * 24, 10)]   // link lives a day, TTL capped at the 10 minute maximum
    [InlineData(2, 2)]          // link has 2 minutes left, so the entry must not outlive it
    public async Task Resolve_Miss_TtlIsSmallerOfMaxAndRemainingLifetime(int linkMinutesLeft, int expectedMinutes)
    {
        DistributedCacheEntryOptions? captured = null;
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, _, o, _) => captured = o)
            .Returns(Task.CompletedTask);
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Link(TimeSpan.FromMinutes(linkMinutesLeft)));
        var sut = Create(cache.Object, ttlMinutes: 10);

        await sut.ResolveAsync("100001", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    // ---- invalidation -------------------------------------------------------------------

    [Fact]
    public async Task Update_Success_RemovesCachedEntry()
    {
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(Link(TimeSpan.FromDays(1))));
        _inner.Setup(s => s.UpdateAsync("100001", It.IsAny<UpdateShortUrlRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrlResponse("http://localhost:8080/100001", UtcNow.AddDays(1)));
        var sut = Create(cache);

        await sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com"), CancellationToken.None);

        (await cache.GetAsync("link:100001")).Should().BeNull();
    }

    [Fact]
    public async Task Update_InnerThrows_KeepsCachedEntry()
    {
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(Link(TimeSpan.FromDays(1))));
        _inner.Setup(s => s.UpdateAsync("100001", It.IsAny<UpdateShortUrlRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException(new InvalidOperationException("conflict")));
        var sut = Create(cache);

        Func<Task> act = () => sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com"), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
        (await cache.GetAsync("link:100001")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_Success_RemovesCachedEntry()
    {
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(Link(TimeSpan.FromDays(1))));
        _inner.Setup(s => s.DeleteAsync("100001", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var sut = Create(cache);

        await sut.DeleteAsync("100001", CancellationToken.None);

        (await cache.GetAsync("link:100001")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_NotFound_Propagates()
    {
        _inner.Setup(s => s.DeleteAsync("nope", It.IsAny<CancellationToken>())).ThrowsAsync(new NotFoundException("nope"));
        var sut = Create(MemoryCache());

        Func<Task> act = () => sut.DeleteAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Update_CacheRemoveFails_StillReturnsTheUpdate()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        var response = new ShortUrlResponse("http://localhost:8080/100001", UtcNow.AddDays(1));
        _inner.Setup(s => s.UpdateAsync("100001", It.IsAny<UpdateShortUrlRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var sut = Create(cache.Object);

        var result = await sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com"), CancellationToken.None);

        result.Should().Be(response);
    }

    // ---- Redis failure: fail open + circuit breaker -------------------------------------

    [Fact]
    public async Task Resolve_CacheReadThrows_FallsBackToInner()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        var link = Link(TimeSpan.FromDays(1));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var sut = Create(cache.Object);

        var result = await sut.ResolveAsync("100001", CancellationToken.None);

        result.Should().Be(link);
    }

    [Fact]
    public async Task Resolve_AfterCacheFailure_SkipsCacheDuringCooldown_ThenRetries()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1)));
        var sut = Create(cache.Object, cooldownSeconds: 30);

        await sut.ResolveAsync("100001", CancellationToken.None);   // fails, opens the breaker
        await sut.ResolveAsync("100001", CancellationToken.None);   // skipped: no second cache call
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        _clock.Now = _clock.Now.AddSeconds(31);
        await sut.ResolveAsync("100001", CancellationToken.None);   // cooldown over: tries again
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Resolve_CacheWriteThrows_StillReturnsTheLink()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var link = Link(TimeSpan.FromDays(1));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var sut = Create(cache.Object);

        var result = await sut.ResolveAsync("100001", CancellationToken.None);

        result.Should().Be(link);
    }

    [Fact]
    public async Task Update_WhileBreakerIsOpen_StillAttemptsToRemoveTheEntry()
    {
        // CCH-AC-16: a stale entry is worse than a slow request, so invalidation ignores the breaker.
        var removedKeys = new List<string>();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        cache.Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => removedKeys.Add(key))
            .Returns(Task.CompletedTask);
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1)));
        _inner.Setup(s => s.UpdateAsync("100001", It.IsAny<UpdateShortUrlRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrlResponse("http://localhost:8080/100001", UtcNow.AddDays(1)));
        var sut = Create(cache.Object, cooldownSeconds: 30);

        await sut.ResolveAsync("100001", CancellationToken.None);   // read fails, opens the breaker
        await sut.ResolveAsync("100001", CancellationToken.None);   // proves the breaker is open: cache not asked
        cache.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        await sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com"), CancellationToken.None);

        removedKeys.Should().ContainSingle().Which.Should().EndWith("link:100001");
    }

    // ---- cache lookup metric (OBS-AC-03) ------------------------------------------------

    [Fact]
    public async Task Resolve_MissThenHit_RecordsMissThenHit()
    {
        using var lookups = _telemetry.Collect<long>(CacheLookups);
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1)));
        var sut = Create(MemoryCache());

        await sut.ResolveAsync("100001", CancellationToken.None);
        await sut.ResolveAsync("100001", CancellationToken.None);

        LookupResults(lookups).Should().Equal("miss", "hit");
    }

    [Fact]
    public async Task Resolve_CachedEntryIsPastExpiry_RecordsMiss()
    {
        using var lookups = _telemetry.Collect<long>(CacheLookups);
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", JsonSerializer.SerializeToUtf8Bytes(Link(TimeSpan.FromSeconds(-1))));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LinkExpiredException("100001"));
        var sut = Create(cache);

        Func<Task> act = () => sut.ResolveAsync("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
        LookupResults(lookups).Should().Equal("miss");
    }

    [Fact]
    public async Task Resolve_CacheReadThrows_RecordsError()
    {
        using var lookups = _telemetry.Collect<long>(CacheLookups);
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1)));
        var sut = Create(cache.Object);

        await sut.ResolveAsync("100001", CancellationToken.None);

        LookupResults(lookups).Should().Equal("error");
    }

    [Fact]
    public async Task Resolve_CachedEntryIsUnreadable_RecordsError()
    {
        using var lookups = _telemetry.Collect<long>(CacheLookups);
        var cache = MemoryCache();
        await cache.SetAsync("link:100001", new byte[] { 1, 2, 3 });
        var link = Link(TimeSpan.FromDays(1));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        var sut = Create(cache);

        var result = await sut.ResolveAsync("100001", CancellationToken.None);

        result.Should().Be(link);
        LookupResults(lookups).Should().Equal("error");
    }

    [Fact]
    public async Task Resolve_DuringCooldownAfterFailure_RecordsBypassed()
    {
        using var lookups = _telemetry.Collect<long>(CacheLookups);
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));
        _inner.Setup(s => s.ResolveAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(Link(TimeSpan.FromDays(1)));
        var sut = Create(cache.Object, cooldownSeconds: 30);

        await sut.ResolveAsync("100001", CancellationToken.None);   // fails, opens the breaker
        await sut.ResolveAsync("100001", CancellationToken.None);   // breaker open: cache not asked

        LookupResults(lookups).Should().Equal("error", "bypassed");
    }

    // ---- pass-through -------------------------------------------------------------------

    [Fact]
    public async Task Create_And_Analytics_AreDelegatedToInner()
    {
        var created = new CreateResult(new ShortUrlResponse("http://localhost:8080/100001", UtcNow.AddDays(1)), true);
        var stats = new AnalyticsResponse(3, 2, new Dictionary<string, long>());
        _inner.Setup(s => s.CreateAsync(It.IsAny<CreateShortUrlRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(created);
        _inner.Setup(s => s.GetAnalyticsAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(stats);
        var sut = Create(MemoryCache());

        (await sut.CreateAsync(new CreateShortUrlRequest("https://example.com"), CancellationToken.None)).Should().Be(created);
        (await sut.GetAnalyticsAsync("100001", CancellationToken.None)).Should().Be(stats);
    }
}

public class CacheHealthTests
{
    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void IsAvailable_Initially_True()
    {
        var health = new CacheHealth(new MutableClock(), Options.Create(new CacheOptions()));

        health.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_BlocksUntilCooldownPasses()
    {
        var clock = new MutableClock();
        var health = new CacheHealth(clock, Options.Create(new CacheOptions { FailureCooldownSeconds = 30 }));

        health.RecordFailure();
        health.IsAvailable.Should().BeFalse();

        clock.Now = clock.Now.AddSeconds(29);
        health.IsAvailable.Should().BeFalse();

        clock.Now = clock.Now.AddSeconds(2);
        health.IsAvailable.Should().BeTrue();
    }
}
