using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

/// <summary>Own host with a tiny limit (3 writes, 2 reads per minute) so 429 is easy to reach.</summary>
public class LimitedApiFactory : ApiFactory
{
    public LimitedApiFactory() : base(new Dictionary<string, string?>
    {
        ["RateLimit:Enabled"] = "true",
        ["RateLimit:Write:PermitLimit"] = "3",
        ["RateLimit:Write:WindowSeconds"] = "60",
        ["RateLimit:Read:PermitLimit"] = "2",
        ["RateLimit:Read:WindowSeconds"] = "60"
    })
    {
    }
}

public class RateLimitIntegrationTests : IClassFixture<LimitedApiFactory>
{
    private readonly HttpClient _client;

    public RateLimitIntegrationTests(LimitedApiFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    [Fact]
    public async Task Writes_AfterLimit_Return429WithRetryAfter()
    {
        for (var i = 0; i < 3; i++)
        {
            var ok = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"request {i + 1} is inside the limit");
        }

        var blocked = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });

        blocked.StatusCode.Should().Be((HttpStatusCode)429);
        blocked.Headers.Contains("Retry-After").Should().BeTrue();
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Too many requests");
    }

    [Fact]
    public async Task Reads_AfterLimit_Return429_AndHealthIsNeverLimited()
    {
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/zzzzzz9")).StatusCode.Should().Be((HttpStatusCode)429);

        for (var i = 0; i < 10; i++)
        {
            (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    // ---- Shared budgets (own host per test: limiter state lives in the app instance) ----

    [Fact]
    public async Task AnalyticsEndpoints_ShareReadLimit_Return429()
    {
        await using var factory = new LimitedApiFactory();
        var client = factory.CreateNoRedirectClient();

        // Unknown code: a 404 still spends a read permit. Summary and /browsers use up the budget of 2,
        // so /countries is blocked — the permits are shared across different analytics routes.
        (await client.GetAsync("/api/v1/analytics/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/analytics/zzzzzz9/browsers")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var blocked = await client.GetAsync("/api/v1/analytics/zzzzzz9/countries");

        blocked.StatusCode.Should().Be((HttpStatusCode)429);
        blocked.Headers.Contains("Retry-After").Should().BeTrue();
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Reads_AfterWriteBudgetIsExhausted_AreNotRateLimited()
    {
        await using var factory = new LimitedApiFactory();
        var client = factory.CreateNoRedirectClient();

        // Spend the whole write budget of 3; the first link is kept for a real redirect below.
        var (code, _) = await CreateAsync(client);
        for (var i = 0; i < 2; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
            ok.StatusCode.Should().Be(HttpStatusCode.Created, $"request {i + 2} is inside the write limit");
        }

        var blocked = await client.PostAsJsonAsync("/api/v1/shorten", new { url = UniqueUrl() });
        blocked.StatusCode.Should().Be((HttpStatusCode)429);

        // Reads have their own counter (budget 2): both of these must get through.
        (await VisitAsync(client, code)).StatusCode.Should().Be(HttpStatusCode.Found);
        (await client.GetAsync("/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutAndDelete_ShareWriteLimit_Return429()
    {
        await using var factory = new LimitedApiFactory();
        var client = factory.CreateNoRedirectClient();

        // Unknown code: a 404 still spends a write permit, so no links need to be created.
        (await client.PutAsJsonAsync("/api/v1/shorten/zzzzzz9", new { url = UniqueUrl() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.DeleteAsync("/api/v1/shorten/zzzzzz9")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync("/api/v1/shorten/zzzzzz9", new { url = UniqueUrl() })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var blocked = await client.DeleteAsync("/api/v1/shorten/zzzzzz9");

        blocked.StatusCode.Should().Be((HttpStatusCode)429);
        blocked.Headers.Contains("Retry-After").Should().BeTrue();
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }
}
