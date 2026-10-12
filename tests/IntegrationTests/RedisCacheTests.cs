using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// The redirect cache against a real Redis client (spec 004). Each test builds its own host:
/// class fixtures are created even when a [RedisFact] is skipped, and the timing test needs a fresh circuit breaker.
/// </summary>
public class RedisCacheTests
{
    // ---- real Redis (CCH-AC-18), opt-in via URLSHORTENER_TEST_REDIS ----

    [RedisFact]
    public async Task Redirect_PopulatesRedis_UpdateAndDeleteRemoveKey()
    {
        await using var factory = new RedisApiFactory(RedisFactAttribute.ConnectionString!);
        var client = factory.CreateNoRedirectClient();
        var cache = factory.Services.GetRequiredService<IDistributedCache>();
        var target = UniqueUrl();
        var (code, _) = await CreateAsync(client, target);
        // Same key as CachedUrlShortenerService; the RedisCache adds the instance name prefix itself.
        var key = "link:" + code;

        var redirect = await VisitAsync(client, code);

        redirect.StatusCode.Should().Be(HttpStatusCode.Found);
        var cached = await cache.GetAsync(key);
        cached.Should().NotBeNull("a successful redirect stores the link in Redis");
        Encoding.UTF8.GetString(cached!).Should().Contain(target);

        // Update removes the entry; the next redirect caches the new target.
        var newTarget = UniqueUrl();
        var updated = await client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = newTarget });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cache.GetAsync(key)).Should().BeNull("an update invalidates the cached entry");

        var again = await VisitAsync(client, code);
        again.StatusCode.Should().Be(HttpStatusCode.Found);
        again.Headers.Location!.ToString().Should().Be(newTarget);
        var recached = await cache.GetAsync(key);
        recached.Should().NotBeNull();
        Encoding.UTF8.GetString(recached!).Should().Contain(newTarget);

        // Delete removes the entry.
        (await client.DeleteAsync($"/api/v1/shorten/{code}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await cache.GetAsync(key)).Should().BeNull("a delete invalidates the cached entry");
    }

    // ---- unreachable Redis (CCH-AC-17) ----

    [Fact]
    public async Task RedisUnreachable_RedirectStillServedFromSql()
    {
        await using var factory = new UnreachableRedisApiFactory();
        var client = factory.CreateNoRedirectClient();
        var target = UniqueUrl();
        var (code, _) = await CreateAsync(client, target);

        // First redirect pays for the failed Redis connect (ConnectTimeout 1000 ms + AsyncTimeout 500 ms) and opens the breaker.
        var first = Stopwatch.StartNew();
        var firstResponse = await VisitAsync(client, code);
        first.Stop();

        // Second redirect is inside the 30 s cooldown, so Redis is skipped entirely.
        var second = Stopwatch.StartNew();
        var secondResponse = await VisitAsync(client, code);
        second.Stop();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        firstResponse.Headers.Location!.ToString().Should().Be(target);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        secondResponse.Headers.Location!.ToString().Should().Be(target);
        first.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        second.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the open circuit breaker skips Redis");
    }
}
