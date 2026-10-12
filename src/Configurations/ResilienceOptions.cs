namespace UrlShortener.Configurations;

public class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Attempts to apply migrations at startup before giving up (SQL Server in Docker needs a few seconds).</summary>
    public int StartupMaxAttempts { get; set; } = 10;

    /// <summary>First delay between startup attempts; doubles each time.</summary>
    public int StartupBaseDelaySeconds { get; set; } = 2;

    /// <summary>Longest single wait between startup attempts.</summary>
    public int StartupMaxDelaySeconds { get; set; } = 30;

    /// <summary>Retries for transient SQL errors on idempotent operations (redirect lookup, click inserts). 0 disables retrying.</summary>
    public int SqlMaxRetries { get; set; } = 3;

    /// <summary>First delay before a transient retry; grows exponentially with jitter.</summary>
    public int SqlBaseDelayMs { get; set; } = 200;

    /// <summary>
    /// Total time budget for a request-path read including all retries (redirect lookup). Without it each attempt
    /// could wait out the 30 s SQL command timeout, holding the request for minutes during a database brownout.
    /// </summary>
    public int RequestTimeoutMs { get; set; } = 3000;
}
