using FluentValidation;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using UrlShortener.Analytics;
using UrlShortener.BackgroundServices;
using UrlShortener.Caching;
using UrlShortener.Configurations;
using UrlShortener.Middleware;
using UrlShortener.RateLimiting;
using UrlShortener.Repositories;
using UrlShortener.Resilience;
using UrlShortener.Services;
using UrlShortener.Validators;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UrlShortener.Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.Configure<ShortenerOptions>(builder.Configuration.GetSection(ShortenerOptions.SectionName));

builder.Services.Configure<AnalyticsOptions>(builder.Configuration.GetSection(AnalyticsOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);

// Polly retry pipelines for idempotent SQL work (spec 010).
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection(ResilienceOptions.SectionName));
builder.Services.AddSingleton<SqlResilience>();
builder.Services.AddScoped<IShortUrlRepository, ShortUrlRepository>();

// Redis cache-aside on the redirect path. CachedUrlShortenerService wraps the real service.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        var redis = ConfigurationOptions.Parse(redisConnection);
        redis.AbortOnConnectFail = false; // start even if Redis is down; reconnect in the background
        redis.ConnectTimeout = 1000;      // short timeouts: a dead cache must not make redirects slow
        redis.SyncTimeout = 500;
        redis.AsyncTimeout = 500;
        options.ConfigurationOptions = redis;
        options.InstanceName = "urlshortener:";
    });
}
else
{
    // No Redis configured (for example in tests): same behaviour, in-process memory.
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSingleton<CacheHealth>();
builder.Services.AddScoped<UrlShortenerService>();
builder.Services.AddScoped<IUrlShortenerService>(sp =>
    ActivatorUtilities.CreateInstance<CachedUrlShortenerService>(sp, sp.GetRequiredService<UrlShortenerService>()));
builder.Services.AddValidatorsFromAssemblyContaining<CreateShortUrlRequestValidator>();

// Asynchronous click analytics (queue -> background worker -> ClickEvents).
builder.Services.AddSingleton<IClickQueue, ChannelClickQueue>();
builder.Services.AddSingleton<ICountryResolver, HeaderCountryResolver>();
builder.Services.AddSingleton<ClickTracker>();
builder.Services.AddSingleton<IClickEventWriter, ClickEventWriter>();
builder.Services.AddHostedService<ClickRecorderBackgroundService>();

// Rate limiting (per client IP) and background cleanup of long-expired links.
builder.Services.AddAppRateLimiting(builder.Configuration);

builder.Services.Configure<CleanupOptions>(builder.Configuration.GetSection(CleanupOptions.SectionName));
builder.Services.AddScoped<ExpiredLinkCleaner>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddHostedService<ExpiredLinkCleanupService>();

// FluentValidation reports missing/invalid fields, so turn off MVC's implicit [Required]
// for non-nullable reference types. That keeps every 400 in one consistent format.
builder.Services.AddControllers(options =>
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Metrics and monitoring (spec 009). OTLP export only when an endpoint is configured (compose points it at the Aspire dashboard).
builder.Services.AddSingleton<UrlShortenerMetrics>();
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "url-shortener"))
    .WithMetrics(metrics => metrics
        .AddMeter(UrlShortenerMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            // Probes and API docs add noise, not insight.
            options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments("/swagger");
            // Spans already carry the path (short code); don't add the User-Agent, which with the path would
            // fingerprint visitors in a store outside the salted click hashing (spec 005, spec 009).
            options.EnrichWithHttpResponse = (activity, _) => activity.SetTag("user_agent.original", null);
        })
        .AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    // Exports metrics and traces. UseOtlpExporter also enables a logging pipeline, but Serilog replaces the
    // logger factory, so logs stay on the Serilog console sink.
    telemetry.UseOtlpExporter();
}

// Redis down is Degraded, not Unhealthy: redirects fall back to SQL Server (spec 004).
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database")
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Degraded, timeout: TimeSpan.FromSeconds(2));

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await DatabaseInitializer.MigrateWithRetryAsync(app.Services, app.Logger);
}

// After routing (added automatically) so [EnableRateLimiting] on controllers is visible; before the endpoints run.
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Makes Program visible to WebApplicationFactory<Program> in the integration tests.
public partial class Program { }
