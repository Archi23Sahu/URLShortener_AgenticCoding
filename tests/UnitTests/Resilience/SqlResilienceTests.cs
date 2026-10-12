using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using UrlShortener.Configurations;
using UrlShortener.Resilience;

namespace UrlShortener.UnitTests.Resilience;

public class SqlResilienceTests
{
    private const int StartupMaxAttempts = 5;
    private const int SqlMaxRetries = 3;

    // Zero delays: the pipelines still retry, but the tests don't wait for backoff.
    private static SqlResilience Create(
        int startupMaxAttempts = StartupMaxAttempts,
        int sqlMaxRetries = SqlMaxRetries,
        int sqlBaseDelayMs = 0,
        int requestTimeoutMs = 5000) =>
        new(Options.Create(new ResilienceOptions
            {
                StartupMaxAttempts = startupMaxAttempts,
                StartupBaseDelaySeconds = 0,
                SqlMaxRetries = sqlMaxRetries,
                SqlBaseDelayMs = sqlBaseDelayMs,
                RequestTimeoutMs = requestTimeoutMs
            }),
            NullLogger<SqlResilience>.Instance);

    // ---- Startup (RSL-AC-07) -----------------------------------------------------------

    [Fact]
    public async Task Startup_FailsUntilLastAttempt_Completes()
    {
        var sut = Create();
        var calls = 0;

        await sut.Startup.ExecuteAsync(_ =>
        {
            calls++;
            if (calls < StartupMaxAttempts)
            {
                throw new InvalidOperationException("database not ready");
            }

            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        calls.Should().Be(StartupMaxAttempts);
    }

    [Fact]
    public async Task Startup_FailsEveryAttempt_ThrowsLastErrorAfterMaxAttempts()
    {
        var sut = Create();
        var calls = 0;

        Func<Task> act = async () => await sut.Startup.ExecuteAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException($"attempt {calls}");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"attempt {StartupMaxAttempts}");
        calls.Should().Be(StartupMaxAttempts);
    }

    // ---- Transient ---------------------------------------------------------------------

    [Fact]
    public async Task Transient_TimeoutEveryAttempt_RetriesSqlMaxRetriesThenThrows()
    {
        var sut = Create();
        var calls = 0;

        Func<Task> act = async () => await sut.Transient.ExecuteAsync(_ =>
        {
            calls++;
            throw new TimeoutException();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        calls.Should().Be(1 + SqlMaxRetries);
    }

    [Fact]
    public async Task Transient_NonTransientError_IsNotRetried()
    {
        var sut = Create();
        var calls = 0;

        Func<Task> act = async () => await sut.Transient.ExecuteAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Transient_SqlMaxRetriesZero_DoesNotRetry()
    {
        var sut = Create(sqlMaxRetries: 0);
        var calls = 0;

        Func<Task> act = async () => await sut.Transient.ExecuteAsync(_ =>
        {
            calls++;
            throw new TimeoutException();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        calls.Should().Be(1);
    }

    // ---- RequestRead (RSL-AC-08) -------------------------------------------------------
    // These use real time: the budget is Polly's timeout strategy, so a short budget and generous upper bound
    // keep them fast without being flaky.

    [Fact]
    public async Task RequestRead_HangingOperation_GivesUpWithinBudget()
    {
        var sut = Create(sqlMaxRetries: 3, sqlBaseDelayMs: 0, requestTimeoutMs: 200);
        var stopwatch = Stopwatch.StartNew();

        Func<Task> act = async () => await sut.RequestRead.ExecuteAsync(
            async ct => await Task.Delay(Timeout.Infinite, ct), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RequestRead_TransientFailuresBeyondBudget_GivesUpWithinBudget()
    {
        // 100 retries with exponential backoff would take far longer than the 300 ms budget.
        var sut = Create(sqlMaxRetries: 100, sqlBaseDelayMs: 100, requestTimeoutMs: 300);
        var calls = 0;
        var stopwatch = Stopwatch.StartNew();

        Func<Task> act = async () => await sut.RequestRead.ExecuteAsync(ct =>
        {
            // Like SqlClient, honour cancellation before starting another attempt.
            ct.ThrowIfCancellationRequested();
            calls++;
            throw new TimeoutException();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        stopwatch.Stop();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        calls.Should().BeLessThan(1 + 100);
    }

    [Fact]
    public async Task RequestRead_TransientFailureOnce_Succeeds()
    {
        var sut = Create(sqlBaseDelayMs: 0, requestTimeoutMs: 5000);
        var calls = 0;

        var result = await sut.RequestRead.ExecuteAsync(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new TimeoutException();
            }

            return ValueTask.FromResult("ok");
        }, CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(2);
    }
}
