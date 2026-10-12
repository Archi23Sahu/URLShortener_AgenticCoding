namespace UrlShortener.Configurations;

public class RateLimitRule
{
    /// <summary>Requests allowed per client per window.</summary>
    public int PermitLimit { get; set; } = 20;

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;
}

public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Master switch. Turn off for load tests that send all traffic from one IP.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Create, update and delete. Strict: these write to the database.</summary>
    public RateLimitRule Write { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    /// <summary>Redirects and analytics reads. Generous: redirects are the hot, cheap path.</summary>
    public RateLimitRule Read { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };
}
