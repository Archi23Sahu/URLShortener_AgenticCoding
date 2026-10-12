using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using UrlShortener.Configurations;

namespace UrlShortener.Resilience;

/// <summary>
/// Owns the Polly pipelines for SQL Server access. Only idempotent work goes through them: the startup
/// migration, the redirect lookup and click inserts. Request-path writes (create/update/delete) are not
/// retried — they use explicit transactions or optimistic locking, where a blind retry could duplicate
/// a row or hide a conflict.
/// </summary>
public sealed class SqlResilience
{
    public SqlResilience(IOptions<ResilienceOptions> options, ILogger<SqlResilience> logger)
    {
        var o = options.Value;
        var startupAttempts = Math.Max(1, o.StartupMaxAttempts);

        Startup = startupAttempts == 1
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    // Anything can fail while SQL Server is still starting (login failures, missing database...).
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                    MaxRetryAttempts = startupAttempts - 1,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(Math.Max(0, o.StartupBaseDelaySeconds)),
                    MaxDelay = TimeSpan.FromSeconds(Math.Max(1, o.StartupMaxDelaySeconds)),
                    OnRetry = args =>
                    {
                        logger.LogWarning(args.Outcome.Exception,
                            "Database not ready (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                            args.AttemptNumber + 1, startupAttempts, args.RetryDelay);
                        return default;
                    }
                })
                .Build();

        var transientRetry = o.SqlMaxRetries <= 0
            ? null
            : new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(SqlTransientErrorDetector.IsTransient),
                MaxRetryAttempts = o.SqlMaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(Math.Max(0, o.SqlBaseDelayMs)),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Transient SQL error (retry {Retry}/{MaxRetries}); retrying in {Delay}",
                        args.AttemptNumber + 1, o.SqlMaxRetries, args.RetryDelay);
                    return default;
                }
            };

        Transient = transientRetry is null
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder().AddRetry(transientRetry).Build();

        // Outermost strategy first: the timeout bounds the retries as a whole, not each attempt.
        var requestRead = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromMilliseconds(Math.Max(10, o.RequestTimeoutMs)));
        RequestRead = (transientRetry is null ? requestRead : requestRead.AddRetry(transientRetry)).Build();
    }

    /// <summary>Retries any failure while applying migrations at startup (spec 010, RSL-AC-07).</summary>
    public ResiliencePipeline Startup { get; }

    /// <summary>Retries transient SQL errors only. For background work (click inserts, spec 010 RSL-AC-04..05).</summary>
    public ResiliencePipeline Transient { get; }

    /// <summary>
    /// Transient retries inside a total time budget, for idempotent reads on the request path (redirect lookup,
    /// spec 010 RSL-AC-02/03/08). Exceeding the budget throws Polly's TimeoutRejectedException.
    /// </summary>
    public ResiliencePipeline RequestRead { get; }
}
