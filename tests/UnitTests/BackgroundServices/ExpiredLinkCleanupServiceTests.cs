using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using UrlShortener.BackgroundServices;
using UrlShortener.Configurations;
using UrlShortener.Repositories;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.BackgroundServices;

public class ExpiredLinkCleanupServiceTests
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly Mock<IShortUrlRepository> _repo = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero));

    private int Passes =>
        _repo.Invocations.Count(i => i.Method.Name == nameof(IShortUrlRepository.DeleteExpiredAsync));

    private ExpiredLinkCleanupService Create(bool enabled = true, int intervalMinutes = 1)
    {
        var cleanup = Options.Create(new CleanupOptions { Enabled = enabled, IntervalMinutes = intervalMinutes });
        var services = new ServiceCollection();
        services.AddScoped(_ => new ExpiredLinkCleaner(_repo.Object, _clock, cleanup, NullLogger<ExpiredLinkCleaner>.Instance));
        var provider = services.BuildServiceProvider();

        return new ExpiredLinkCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(), _clock, cleanup, NullLogger<ExpiredLinkCleanupService>.Instance);
    }

    /// <summary>The service runs on a pool thread, so wait (real time, bounded) until it has observed the fake clock.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue(what);
    }

    // ---- schedule and failure handling (CLN-AC-06) -------------------------------------

    [Fact]
    public async Task Run_PassThrows_ServiceKeepsRunningAndRunsAgainAtTheNextInterval()
    {
        _repo.SetupSequence(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"))
            .ReturnsAsync(0);
        using var sut = Create(intervalMinutes: 1);

        await sut.StartAsync(CancellationToken.None);

        // Nothing runs before the startup delay has elapsed.
        _clock.Advance(StartupDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Passes.Should().Be(0);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Passes == 1, "the first pass runs once the startup delay has elapsed");
        sut.ExecuteTask!.IsCompleted.Should().BeFalse("a failed pass is logged, not fatal");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => Passes == 2, "the next pass runs one interval after the failed one");

        await sut.StopAsync(CancellationToken.None);
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- disabled (CLN-AC-07) ----------------------------------------------------------

    [Fact]
    public async Task Run_Disabled_NeverTouchesTheRepositoryAndTheTaskCompletes()
    {
        using var sut = Create(enabled: false);

        await sut.StartAsync(CancellationToken.None);
        _clock.Advance(StartupDelay);
        _clock.Advance(TimeSpan.FromHours(2));

        await WaitUntilAsync(() => sut.ExecuteTask!.IsCompleted, "a disabled service exits immediately");
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
        Passes.Should().Be(0);
        _repo.Verify(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
