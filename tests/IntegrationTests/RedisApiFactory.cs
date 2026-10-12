using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Own host with a real RedisCache instead of the in-memory cache, using the same client settings as Program.cs.
/// Program.cs reads ConnectionStrings:Redis before test configuration applies, so the cache is swapped in services.
/// </summary>
public class RedisApiFactory : ApiFactory
{
    private readonly string _redisConnection;

    public RedisApiFactory(string redisConnection) : base(new Dictionary<string, string?>())
    {
        _redisConnection = redisConnection;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Base first: its ConfigureTestServices installs the in-memory cache, and this one must run after it.
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDistributedCache>();
            services.AddStackExchangeRedisCache(options =>
            {
                var redis = ConfigurationOptions.Parse(_redisConnection);
                redis.AbortOnConnectFail = false;
                redis.ConnectTimeout = 1000;
                redis.SyncTimeout = 500;
                redis.AsyncTimeout = 500;
                options.ConfigurationOptions = redis;
                // Unique per run so tests never read entries left by another run or by a dev instance.
                options.InstanceName = $"urlshortener-tests:{Guid.NewGuid():N}:";
            });
        });
    }
}

/// <summary>Redis is configured but nothing listens on the port.</summary>
public class UnreachableRedisApiFactory : RedisApiFactory
{
    public UnreachableRedisApiFactory() : base("127.0.0.1:1")
    {
    }
}
