using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.RateLimiting;

public static class RateLimitPolicies
{
    public const string Write = "write";
    public const string Read = "read";

    /// <summary>
    /// The identity a limiter partition is keyed on. IPv4 clients are keyed by address. An IPv6 client usually controls
    /// a whole /64, so it is keyed by that prefix: otherwise rotating the low 64 bits would give it a new bucket per
    /// request. IPv4-mapped IPv6 addresses are treated as their IPv4 address.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return "v6:" + Convert.ToHexString(bytes[..8]);
    }

    /// <summary>
    /// One fixed-window limiter per client IP. A fixed window is the simplest option; its known weakness is
    /// that a client can send up to twice the limit across a window boundary.
    /// </summary>
    public static RateLimitPartition<string> Partition(HttpContext context, bool enabled, RateLimitRule rule)
    {
        if (!enabled)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var client = ClientKey(context.Connection.RemoteIpAddress);

        return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, rule.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
            QueueLimit = 0, // reject immediately; never make a client wait
            AutoReplenishment = true
        });
    }
}

public static class RateLimitingSetup
{
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Options are read per request (not at startup) so configuration overrides always apply.
            limiter.AddPolicy(RateLimitPolicies.Write, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPolicies.Partition(context, options.Enabled, options.Write);
            });

            limiter.AddPolicy(RateLimitPolicies.Read, context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                return RateLimitPolicies.Partition(context, options.Enabled, options.Read);
            });

            limiter.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                // Debug, not Warning: a flood would otherwise write one console line per rejected request.
                // Rejections are visible in the http.server.request.duration metric (status 429).
                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimiting")
                    .LogDebug("Rate limit exceeded for {Client} on {Method} {Path}",
                        http.Connection.RemoteIpAddress, http.Request.Method, http.Request.Path);

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = "Rate limit exceeded. Wait for the number of seconds in the Retry-After header and try again."
                };
                problem.Extensions["traceId"] = http.TraceIdentifier;

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.ContentType = "application/problem+json";
                await http.Response.WriteAsync(JsonSerializer.Serialize(problem), ct);
            };
        });

        return services;
    }
}
