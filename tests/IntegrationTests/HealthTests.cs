using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

// RedisApiFactory / UnreachableRedisApiFactory live in RedisApiFactory.cs (shared with RedisCacheTests).
public class HealthTests : IClassFixture<UnreachableRedisApiFactory>
{
    private readonly HttpClient _client;

    public HealthTests(UnreachableRedisApiFactory factory)
    {
        _client = factory.CreateNoRedirectClient();
    }

    // ---- Redis unreachable (OBS-AC-09) ----

    [Fact]
    public async Task Health_RedisUnreachable_Returns200Degraded()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Degraded");
    }

    [Fact]
    public async Task Redirect_RedisUnreachable_StillReturns302()
    {
        var target = UniqueUrl();
        var (code, _) = await CreateAsync(_client, target);

        var response = await VisitAsync(_client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().Be(target);
    }

    // ---- Redis reachable (OBS-AC-08), opt-in via URLSHORTENER_TEST_REDIS ----

    [RedisFact]
    public async Task Health_RedisReachable_ReturnsHealthy()
    {
        await using var factory = new RedisApiFactory(RedisFactAttribute.ConnectionString!);
        var client = factory.CreateNoRedirectClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // ---- no OTLP exporter (OBS-AC-07) ----

    [Fact]
    public async Task App_WithoutOtlpEndpoint_StartsAndServesRequests()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateNoRedirectClient();
        factory.Services.GetRequiredService<IConfiguration>()["OTEL_EXPORTER_OTLP_ENDPOINT"]
            .Should().BeNullOrEmpty("this test covers the default setup, where no OTLP exporter is configured");

        var target = UniqueUrl();
        var (code, _) = await CreateAsync(client, target);
        var redirect = await VisitAsync(client, code);
        var health = await client.GetAsync("/health");

        redirect.StatusCode.Should().Be(HttpStatusCode.Found);
        redirect.Headers.Location!.ToString().Should().Be(target);
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }
}
