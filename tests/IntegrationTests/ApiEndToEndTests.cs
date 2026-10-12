using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;
using UrlShortener.Services;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class ApiEndToEndTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ApiEndToEndTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task FullLifecycle_Create_Redirect_Analytics_Update_Delete()
    {
        var target = UniqueUrl();

        // Create
        var created = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await created.Content.ReadFromJsonAsync<ShortUrlResponse>())!;
        var code = CodeOf(body);
        code.Should().MatchRegex("^[0-9A-Za-z]{6,}$");
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // Same URL again returns the existing link (200)
        var again = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        CodeOf((await again.Content.ReadFromJsonAsync<ShortUrlResponse>())!).Should().Be(code);

        // Redirect is a 302 to the original URL
        var redirect = await VisitAsync(_client, code, ChromeAgent);
        redirect.StatusCode.Should().Be(HttpStatusCode.Found);
        redirect.Headers.Location!.ToString().Should().Be(target);

        // Click shows up in analytics (written by the background worker)
        var analytics = await EventuallyAsync(
            async () => (await _client.GetFromJsonAsync<AnalyticsResponse>($"/api/v1/analytics/{code}"))!,
            a => a.Clicks >= 1);
        analytics.Clicks.Should().Be(1);
        analytics.UniqueVisitors.Should().Be(1);

        // Update changes the target
        var newTarget = UniqueUrl();
        var updated = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = newTarget, expiryDays = 10 });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await VisitAsync(_client, code)).Headers.Location!.ToString().Should().Be(newTarget);

        // Delete, then the link and its analytics are gone
        (await _client.DeleteAsync($"/api/v1/shorten/{code}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/v1/analytics/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"/api/v1/shorten/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redirect_UnknownCode_Returns404ProblemJson()
    {
        var response = await VisitAsync(_client, "zzzzzz9");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("traceId");
    }

    [Fact]
    public async Task Redirect_ExpiredLink_Returns410()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddMinutes(-5));

        var response = await VisitAsync(_client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Update_ExpiredLink_Returns410()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddMinutes(-5));

        var response = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = UniqueUrl() });

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Update_UnknownCode_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/shorten/nope123", new { url = UniqueUrl() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public async Task Create_InvalidExpiryDays_Returns400(int days)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl(), expiryDays = days });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_MissingUrl_Returns400WithFieldError()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Url");
    }

    [Fact]
    public async Task Create_ExpiryDaysSetsExpiry()
    {
        var (_, body) = await CreateAsync(_client, expiryDays: 2);

        body.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(2), TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public async Task Update_InvalidExpiryDays_Returns400(int days)
    {
        var (code, _) = await CreateAsync(_client);

        var response = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = UniqueUrl(), expiryDays = days });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExpiredLink_Returns204ThenRedirectIs404()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddHours(-1));

        var deleted = await _client.DeleteAsync($"/api/v1/shorten/{code}");

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Duplicate detection and code case-sensitivity ----

    [Fact]
    public async Task Create_EquivalentUrlSpellings_ReturnsSameLinkWith200()
    {
        var path = Guid.NewGuid().ToString("N");

        var first = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = $"HTTPS://Example.COM:443/{path}" });
        var second = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = $"https://example.com/{path}" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstCode = CodeOf((await first.Content.ReadFromJsonAsync<ShortUrlResponse>())!);
        var secondCode = CodeOf((await second.Content.ReadFromJsonAsync<ShortUrlResponse>())!);
        secondCode.Should().Be(firstCode);
    }

    [Fact]
    public async Task Create_OnlyExpiredLinkExistsForUrl_Returns201WithNewCode()
    {
        var url = UniqueUrl();
        var expiredCode = await InsertLinkAsync(_factory, DateTime.UtcNow.AddHours(-1), url);

        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var code = CodeOf((await response.Content.ReadFromJsonAsync<ShortUrlResponse>())!);
        code.Should().NotBe(expiredCode);
    }

    [Fact]
    public async Task Create_DuplicateWithLongerExpiryDays_ReturnsExistingLinkUnextended()
    {
        var url = UniqueUrl();
        var first = await _client.PostAsJsonAsync("/api/v1/shorten", new { url, expiryDays = 2 });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await ReadExpiresAtAsync(first);
        var firstCode = CodeOf((await first.Content.ReadFromJsonAsync<ShortUrlResponse>())!);

        var again = await _client.PostAsJsonAsync("/api/v1/shorten", new { url, expiryDays = 30 });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        CodeOf((await again.Content.ReadFromJsonAsync<ShortUrlResponse>())!).Should().Be(firstCode);
        var afterDuplicate = await ReadExpiresAtAsync(again);
        ParseAsUtcInstant(afterDuplicate).Should().Be(ParseAsUtcInstant(original),
            "an existing active link is returned unchanged; a longer expiryDays must not extend it");
    }

    [Fact]
    public async Task Redirect_CodesDifferingOnlyByCase_ResolveIndependently()
    {
        // Real SQL Server collation (SQL_Latin1_General_CP1_CS_AS) decides this; a mocked repository cannot.
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var lowerFirst = "aBcD" + suffix;
        var upperFirst = "AbCd" + suffix;
        var urlA = UniqueUrl();
        var urlB = UniqueUrl();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ShortUrls.Add(new ShortUrl
            {
                Code = lowerFirst,
                OriginalUrl = urlA,
                OriginalUrlHash = UrlNormalizer.ComputeHash(urlA),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            });
            db.ShortUrls.Add(new ShortUrl
            {
                Code = upperFirst,
                OriginalUrl = urlB,
                OriginalUrlHash = UrlNormalizer.ComputeHash(urlB),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
        }

        var a = await VisitAsync(_client, lowerFirst);
        var b = await VisitAsync(_client, upperFirst);

        a.StatusCode.Should().Be(HttpStatusCode.Found);
        a.Headers.Location!.ToString().Should().Be(urlA);
        b.StatusCode.Should().Be(HttpStatusCode.Found);
        b.Headers.Location!.ToString().Should().Be(urlB);
    }

    [Fact]
    public async Task Update_ConcurrentChange_ThrowsConcurrencyConflict()
    {
        // The HTTP API does not expose RowVersion, so the 409 path is proven at the repository level
        // against real SQL Server: two contexts load the same row, the second save must fail.
        var (code, _) = await CreateAsync(_client);

        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var repo1 = scope1.ServiceProvider.GetRequiredService<IShortUrlRepository>();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IShortUrlRepository>();

        var first = (await repo1.GetByCodeForUpdateAsync(code, CancellationToken.None))!;
        var second = (await repo2.GetByCodeForUpdateAsync(code, CancellationToken.None))!;

        second.OriginalUrl = UniqueUrl();
        await repo2.SaveChangesAsync(CancellationToken.None);

        first.OriginalUrl = UniqueUrl();
        var act = () => repo1.SaveChangesAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    [Fact]
    public async Task Create_ManyDifferentUrls_ReturnsUniqueCodes()
    {
        var tasks = Enumerable.Range(0, 25).Select(_ => CreateAsync(_client));
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Code).Distinct().Should().HaveCount(25);
    }

    // ---- UTC expiresAt (plan 007) ----
    // These assert on the raw JSON string: deserializing into DateTime would accept an offset-less value
    // and hide the bug (datetime2 read by EF comes back as DateTimeKind.Unspecified and serializes without "Z").

    [Fact]
    public async Task Create_DuplicateUrl_ReturnsExpiresAtInUtc()
    {
        var target = UniqueUrl();
        var first = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await ReadExpiresAtAsync(first);

        var again = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = target });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var duplicate = await ReadExpiresAtAsync(again);
        duplicate.Should().EndWith("Z",
            "the 200 duplicate response reads expiresAt from SQL and must still be UTC (CRT-AC-16); " +
            "a value without an offset means EF returned DateTimeKind.Unspecified");
        ParseAsUtcInstant(duplicate).Should().Be(ParseAsUtcInstant(original),
            "the duplicate response must report the existing link's expiry instant");
    }

    [Fact]
    public async Task Update_WithoutExpiryDays_ReturnsUnchangedExpiresAtInUtc()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var original = await ReadExpiresAtAsync(created);
        var code = CodeOf((await created.Content.ReadFromJsonAsync<ShortUrlResponse>())!);

        var updated = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = UniqueUrl() });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterUpdate = await ReadExpiresAtAsync(updated);
        afterUpdate.Should().EndWith("Z",
            "an update without expiryDays returns the expiry read from SQL and it must still be UTC (UPD-AC-10); " +
            "a value without an offset means EF returned DateTimeKind.Unspecified");
        ParseAsUtcInstant(afterUpdate).Should().Be(ParseAsUtcInstant(original),
            "an update without expiryDays must not change the expiry instant");
    }

    [Fact]
    public async Task Repository_ReadsDateTimesAsUtcKind()
    {
        // Root-cause guard for plan 007: every DateTime read through EF must be marked UTC,
        // so endpoints added later can't reintroduce offset-less timestamps.
        var (code, _) = await CreateAsync(_client);

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IShortUrlRepository>();
        var entity = await repository.GetByCodeAsync(code, CancellationToken.None);

        entity.Should().NotBeNull();
        entity!.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc,
            "SQL datetime2 has no offset, so EF materializes Unspecified unless a UTC value converter is registered");
        entity.CreatedAt.Kind.Should().Be(DateTimeKind.Utc,
            "SQL datetime2 has no offset, so EF materializes Unspecified unless a UTC value converter is registered");
    }

    private static async Task<string> ReadExpiresAtAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("expiresAt").GetString()!;
    }

    /// <summary>Parses an ISO-8601 timestamp; an offset-less value is treated as UTC so only the instant is compared.</summary>
    private static DateTimeOffset ParseAsUtcInstant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
