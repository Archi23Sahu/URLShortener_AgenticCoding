namespace UrlShortener.IntegrationTests;

/// <summary>
/// A [Fact] that needs a real, reachable Redis. It runs only when the URLSHORTENER_TEST_REDIS environment variable
/// holds a StackExchange.Redis connection string (for example "localhost:6379" after "docker compose up -d redis");
/// otherwise it is reported as skipped, so the default test run (and CI, which has no Redis) stays green.
/// Read the connection string from <see cref="ConnectionString"/> inside the test.
/// </summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "URLSHORTENER_TEST_REDIS";

    public RedisFactAttribute()
    {
        if (ConnectionString is null)
        {
            Skip = $"Needs a reachable Redis: set {EnvironmentVariable} to its connection string " +
                   "(e.g. localhost:6379 after 'docker compose up -d redis').";
        }
    }

    /// <summary>The configured Redis connection string, or null when Redis tests are not enabled.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
