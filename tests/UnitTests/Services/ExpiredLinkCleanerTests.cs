using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Configurations;
using UrlShortener.Repositories;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class ExpiredLinkCleanerTests
{
    private sealed class FixedClock : TimeProvider
    {
        public static readonly DateTimeOffset Instant = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Instant;
    }

    private readonly Mock<IShortUrlRepository> _repo = new();

    private ExpiredLinkCleaner Create(int graceDays = 7, int batchSize = 1000) =>
        new(_repo.Object,
            new FixedClock(),
            Options.Create(new CleanupOptions { GracePeriodDays = graceDays, BatchSize = batchSize }),
            NullLogger<ExpiredLinkCleaner>.Instance);

    [Fact]
    public async Task RunOnce_NothingExpired_DeletesNothing()
    {
        _repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var total = await Create().RunOnceAsync(CancellationToken.None);

        total.Should().Be(0);
        _repo.Verify(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunOnce_CutoffIsNowMinusGracePeriod()
    {
        DateTime? captured = null;
        _repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, int, CancellationToken>((cutoff, _, _) => captured = cutoff)
            .ReturnsAsync(0);

        await Create(graceDays: 7).RunOnceAsync(CancellationToken.None);

        captured.Should().Be(FixedClock.Instant.UtcDateTime.AddDays(-7));
    }

    [Fact]
    public async Task RunOnce_FullBatches_KeepGoingUntilAShortBatch()
    {
        _repo.SetupSequence(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), 1000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1000)
            .ReturnsAsync(1000)
            .ReturnsAsync(300);

        var total = await Create(batchSize: 1000).RunOnceAsync(CancellationToken.None);

        total.Should().Be(2300);
        _repo.Verify(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), 1000, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RunOnce_ExactMultipleOfBatch_StopsAfterAnEmptyBatch()
    {
        _repo.SetupSequence(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(500)
            .ReturnsAsync(0);

        var total = await Create(batchSize: 500).RunOnceAsync(CancellationToken.None);

        total.Should().Be(500);
    }

    [Fact]
    public async Task RunOnce_ZeroGrace_UsesNowAsTheCutoff()
    {
        DateTime? captured = null;
        _repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, int, CancellationToken>((cutoff, _, _) => captured = cutoff)
            .ReturnsAsync(0);

        await Create(graceDays: 0).RunOnceAsync(CancellationToken.None);

        captured.Should().Be(FixedClock.Instant.UtcDateTime);
    }

    [Fact]
    public async Task RunOnce_AlreadyCancelled_ThrowsBeforeTouchingTheDatabase()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => Create().RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _repo.Verify(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunOnce_RepositoryFails_PropagatesSoTheTimerCanLogIt()
    {
        _repo.Setup(r => r.DeleteExpiredAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        Func<Task> act = () => Create().RunOnceAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
