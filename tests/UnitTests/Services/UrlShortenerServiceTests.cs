using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;
using UrlShortener.Resilience;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class UrlShortenerServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IShortUrlRepository> _repo = new();
    private readonly UrlShortenerService _sut;

    public UrlShortenerServiceTests()
    {
        var options = Options.Create(new ShortenerOptions
        {
            BaseUrl = "http://localhost:8080/",
            DefaultExpiryDays = 30,
            MaxExpiryDays = 365
        });

        // Zero delays so transient retries (spec 010) don't slow the suite.
        var resilience = new SqlResilience(
            Options.Create(new ResilienceOptions { SqlMaxRetries = 3, SqlBaseDelayMs = 0, StartupBaseDelaySeconds = 0 }),
            NullLogger<SqlResilience>.Instance);

        _sut = new UrlShortenerService(
            _repo.Object,
            options,
            new FixedTimeProvider(Now),
            resilience,
            NullLogger<UrlShortenerService>.Instance);
    }

    // ---- CreateAsync -------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_NewUrl_PersistsNormalizedEntityAndReturnsShortUrl()
    {
        ShortUrl? saved = null;
        _repo.Setup(r => r.FindActiveByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl?)null);
        _repo.Setup(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl entity, Func<long, string> codeFromId, CancellationToken _) =>
            {
                entity.Id = 1;
                entity.Code = codeFromId(entity.Id);
                saved = entity;
                return entity;
            });

        var result = await _sut.CreateAsync(new CreateShortUrlRequest("HTTPS://Www.Example.com/very/long/url"), CancellationToken.None);

        result.Created.Should().BeTrue();
        result.Response.ShortUrl.Should().Be("http://localhost:8080/100001");
        result.Response.ExpiresAt.Should().Be(Now.AddDays(30));

        saved.Should().NotBeNull();
        saved!.OriginalUrl.Should().Be("https://www.example.com/very/long/url");
        saved.OriginalUrlHash.Should().HaveLength(64);
        saved.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CreateAsync_WithExpiryDays_UsesRequestedLifetime()
    {
        _repo.Setup(r => r.FindActiveByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl?)null);
        _repo.Setup(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl entity, Func<long, string> codeFromId, CancellationToken _) =>
            {
                entity.Id = 2;
                entity.Code = codeFromId(entity.Id);
                return entity;
            });

        var result = await _sut.CreateAsync(new CreateShortUrlRequest("https://example.com/a", ExpiryDays: 7), CancellationToken.None);

        result.Response.ExpiresAt.Should().Be(Now.AddDays(7));
    }

    [Fact]
    public async Task CreateAsync_DuplicateActiveUrl_ReturnsExistingLinkWithoutInserting()
    {
        var existing = new ShortUrl
        {
            Id = 7,
            Code = "100007",
            OriginalUrl = "https://www.example.com/",
            OriginalUrlHash = "hash",
            CreatedAt = Now.AddDays(-1),
            ExpiresAt = Now.AddDays(5)
        };
        _repo.Setup(r => r.FindActiveByHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await _sut.CreateAsync(new CreateShortUrlRequest("https://www.example.com"), CancellationToken.None);

        result.Created.Should().BeFalse();
        result.Response.ShortUrl.Should().Be("http://localhost:8080/100007");
        result.Response.ExpiresAt.Should().Be(existing.ExpiresAt);
        _repo.Verify(r => r.AddAsync(It.IsAny<ShortUrl>(), It.IsAny<Func<long, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_InvalidUrl_ThrowsArgumentException()
    {
        Func<Task> act = () => _sut.CreateAsync(new CreateShortUrlRequest("javascript:alert(1)"), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- ResolveAsync ------------------------------------------------------------------

    
    [Fact]
    public async Task ResolveAsync_ActiveLink_ReturnsIdAndOriginalUrl()
    {
        _repo.Setup(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrl { Id = 42, Code = "100001", OriginalUrl = "https://example.com/a", ExpiresAt = Now.AddDays(1) });

        var link = await _sut.ResolveAsync("100001", CancellationToken.None);

        link.Url.Should().Be("https://example.com/a");
        link.Id.Should().Be(42);
    }

    [Fact]
    public async Task ResolveAsync_UnknownCode_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetByCodeAsync("nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShortUrl?)null);

        Func<Task> act = () => _sut.ResolveAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ResolveAsync_ExpiredLink_ThrowsLinkExpired()
    {
        _repo.Setup(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrl { Code = "100001", OriginalUrl = "https://example.com/a", ExpiresAt = Now.AddSeconds(-1) });

        Func<Task> act = () => _sut.ResolveAsync("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
    }

    [Fact]
    public async Task ResolveAsync_LinkExpiringExactlyNow_IsTreatedAsExpired()
    {
        _repo.Setup(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShortUrl { Code = "100001", OriginalUrl = "https://example.com/a", ExpiresAt = Now });

        Func<Task> act = () => _sut.ResolveAsync("100001", CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
    }

    [Fact]
    public async Task ResolveAsync_TransientReadFailure_RetriesAndSucceeds()
    {
        // RSL-AC-02
        _repo.SetupSequence(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException())
            .ReturnsAsync(new ShortUrl { Id = 42, Code = "100001", OriginalUrl = "https://example.com/a", ExpiresAt = Now.AddDays(1) });

        var link = await _sut.ResolveAsync("100001", CancellationToken.None);

        link.Url.Should().Be("https://example.com/a");
        _repo.Verify(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResolveAsync_NonTransientFailure_IsNotRetried()
    {
        // RSL-AC-03
        _repo.Setup(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        Func<Task> act = () => _sut.ResolveAsync("100001", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repo.Verify(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- UpdateAsync -------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_ActiveLink_ChangesUrlAndHashAndKeepsExpiry()
    {
        var entity = NewEntity(expiresAt: Now.AddDays(10));
        _repo.Setup(r => r.GetByCodeForUpdateAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var response = await _sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com/page"), CancellationToken.None);

        entity.OriginalUrl.Should().Be("https://new.example.com/page");
        entity.OriginalUrlHash.Should().HaveLength(64).And.NotBe("old");
        entity.ExpiresAt.Should().Be(Now.AddDays(10));
        response.ShortUrl.Should().Be("http://localhost:8080/100001");
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithExpiryDays_ResetsExpiry()
    {
        var entity = NewEntity(expiresAt: Now.AddDays(10));
        _repo.Setup(r => r.GetByCodeForUpdateAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        var response = await _sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://new.example.com/", ExpiryDays: 90), CancellationToken.None);

        response.ExpiresAt.Should().Be(Now.AddDays(90));
    }

    [Fact]
    public async Task UpdateAsync_UnknownCode_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetByCodeForUpdateAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        Func<Task> act = () => _sut.UpdateAsync("nope", new UpdateShortUrlRequest("https://example.com"), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_ExpiredLink_ThrowsAndDoesNotSave()
    {
        var entity = NewEntity(expiresAt: Now.AddDays(-1));
        _repo.Setup(r => r.GetByCodeForUpdateAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(entity);

        Func<Task> act = () => _sut.UpdateAsync("100001", new UpdateShortUrlRequest("https://example.com"), CancellationToken.None);

        await act.Should().ThrowAsync<LinkExpiredException>();
        _repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- DeleteAsync -------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ExistingCode_Deletes()
    {
        _repo.Setup(r => r.DeleteByCodeAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await _sut.DeleteAsync("100001", CancellationToken.None);

        _repo.Verify(r => r.DeleteByCodeAsync("100001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_UnknownCode_ThrowsNotFound()
    {
        _repo.Setup(r => r.DeleteByCodeAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        Func<Task> act = () => _sut.DeleteAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ---- GetAnalyticsAsync -------------------------------------------------------------

    [Fact]
    public async Task GetAnalyticsAsync_ExistingCode_MapsRepositoryStats()
    {
        var entity = NewEntity(expiresAt: Now.AddDays(1));
        entity.Id = 3;
        _repo.Setup(r => r.GetByCodeAsync("100001", It.IsAny<CancellationToken>())).ReturnsAsync(entity);
        _repo.Setup(r => r.GetClickStatsAsync(3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClickStats(10, 4, new Dictionary<string, long> { ["US"] = 6, ["IN"] = 4 }));

        var analytics = await _sut.GetAnalyticsAsync("100001", CancellationToken.None);

        analytics.Clicks.Should().Be(10);
        analytics.UniqueVisitors.Should().Be(4);
        analytics.Countries.Should().BeEquivalentTo(new Dictionary<string, long> { ["US"] = 6, ["IN"] = 4 });
    }

    [Fact]
    public async Task GetAnalyticsAsync_UnknownCode_ThrowsNotFound()
    {
        _repo.Setup(r => r.GetByCodeAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((ShortUrl?)null);

        Func<Task> act = () => _sut.GetAnalyticsAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ---- helpers -----------------------------------------------------------------------

    private static ShortUrl NewEntity(DateTime expiresAt) => new()
    {
        Id = 1,
        Code = "100001",
        OriginalUrl = "https://old.example.com/",
        OriginalUrlHash = "old",
        CreatedAt = Now.AddDays(-1),
        ExpiresAt = expiresAt
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime utcNow)
        {
            _now = new DateTimeOffset(utcNow, TimeSpan.Zero);
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
