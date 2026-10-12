using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using UrlShortener.Configurations;
using UrlShortener.RateLimiting;

namespace UrlShortener.UnitTests.RateLimiting;

public class RateLimitPolicyTests
{
    private static DefaultHttpContext ContextFrom(string? ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        return ctx;
    }

    private static RateLimitRule Rule(int permits, int seconds = 60) =>
        new() { PermitLimit = permits, WindowSeconds = seconds };

    [Fact]
    public void Partition_AllowsUpToTheLimit_ThenRejects()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(2));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Partition_RejectedLease_ReportsRetryAfter()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(1, seconds: 30));
        using var limiter = partition.Factory(partition.PartitionKey);
        limiter.AttemptAcquire();

        var rejected = limiter.AttemptAcquire();

        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Partition_DifferentClients_GetDifferentPartitionKeys()
    {
        var a = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));
        var b = RateLimitPolicies.Partition(ContextFrom("203.0.113.2"), enabled: true, Rule(5));

        a.PartitionKey.Should().NotBe(b.PartitionKey);
    }

    [Fact]
    public void Partition_SameClient_GetsTheSamePartitionKey()
    {
        var a = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));
        var b = RateLimitPolicies.Partition(ContextFrom("203.0.113.1"), enabled: true, Rule(5));

        a.PartitionKey.Should().Be(b.PartitionKey);
    }

    [Fact]
    public void Partition_UnknownClientAddress_StillGetsALimiter()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom(null), enabled: true, Rule(1));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Partition_Disabled_NeverRejects()
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: false, Rule(1));
        using var limiter = partition.Factory(partition.PartitionKey);

        for (var i = 0; i < 1000; i++)
        {
            limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Partition_NonPositiveLimit_IsTreatedAsOne(int permits)
    {
        var partition = RateLimitPolicies.Partition(ContextFrom("203.0.113.9"), enabled: true, Rule(permits));
        using var limiter = partition.Factory(partition.PartitionKey);

        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    // ---- IPv6 /64 grouping and IPv4-mapped addresses (RLM-AC-12) ----

    [Fact]
    public void ClientKey_IPv6AddressesSharingA64_AreEqual()
    {
        var a = RateLimitPolicies.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        var b = RateLimitPolicies.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"));

        a.Should().Be(b);
    }

    [Fact]
    public void ClientKey_IPv6AddressesInDifferent64s_AreDifferent()
    {
        var a = RateLimitPolicies.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        var b = RateLimitPolicies.ClientKey(IPAddress.Parse("2001:db8:1:3::1"));

        a.Should().NotBe(b);
    }

    [Fact]
    public void ClientKey_IPv4MappedIPv6_EqualsTheIPv4Key()
    {
        var mapped = RateLimitPolicies.ClientKey(IPAddress.Parse("::ffff:203.0.113.9"));
        var plain = RateLimitPolicies.ClientKey(IPAddress.Parse("203.0.113.9"));

        mapped.Should().Be(plain);
    }

    [Fact]
    public void ClientKey_IPv4_IsTheAddress()
    {
        RateLimitPolicies.ClientKey(IPAddress.Parse("203.0.113.9")).Should().Be("203.0.113.9");
    }

    [Fact]
    public void ClientKey_NullAddress_IsUnknown()
    {
        RateLimitPolicies.ClientKey(null).Should().Be("unknown");
    }

    [Fact]
    public void Partition_IPv6ClientRotatingWithinItsA64_SharesOneLimiter()
    {
        var first = RateLimitPolicies.Partition(ContextFrom("2001:db8:1:2::1"), enabled: true, Rule(1));
        var second = RateLimitPolicies.Partition(ContextFrom("2001:db8:1:2:ffff:ffff:ffff:ffff"), enabled: true, Rule(1));
        using var limiter = first.Factory(first.PartitionKey);

        second.PartitionKey.Should().Be(first.PartitionKey);
        limiter.AttemptAcquire().IsAcquired.Should().BeTrue();
        // The second request resolves to the same partition key, so it hits the same (exhausted) limiter.
        limiter.AttemptAcquire().IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Defaults_WritesAreStricterThanReads()
    {
        var options = new RateLimitOptions();

        options.Enabled.Should().BeTrue();
        options.Write.PermitLimit.Should().BeLessThan(options.Read.PermitLimit);
    }

    [Fact]
    public void ShippedConfig_EnablesRateLimiting()
    {
        // A linked copy of src/appsettings.json (see UnitTests.csproj), so a test-local appsettings.json can never shadow it.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(Path.Combine("shipped", "appsettings.json"), optional: false)
            .Build();

        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>();

        options.Should().NotBeNull("the shipped appsettings.json must contain a RateLimit section");
        options!.Enabled.Should().BeTrue("the shipped config must enable rate limiting (RLM-AC-11)");
    }

    [Fact]
    public void ShippedConfig_DoesNotEnableDebugLoggingForCacheDecorator()
    {
        // CCH-AC-19: only appsettings.Development.json may turn this on; production must not log every redirect.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(Path.Combine("shipped", "appsettings.json"), optional: false)
            .Build();

        var overrides = configuration.GetSection("Serilog:MinimumLevel:Override").GetChildren()
            .Where(o => o.Key.Contains("CachedUrlShortenerService", StringComparison.Ordinal))
            .ToList();

        overrides.Should().NotContain(o => o.Value == "Debug" || o.Value == "Verbose",
            "the shipped config must not log the cache decorator at Debug (CCH-AC-19)");
    }
}
