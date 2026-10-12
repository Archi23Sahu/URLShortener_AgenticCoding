using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Analytics;
using UrlShortener.BackgroundServices;
using UrlShortener.Configurations;
using UrlShortener.Resilience;

namespace UrlShortener.UnitTests.BackgroundServices;

public class ClickRecorderBackgroundServiceTests
{
    private const int SqlMaxRetries = 3;
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IClickEventWriter> _writer = new();
    private readonly TestMetrics _telemetry = TestMetrics.Create();

    // Sizes of every InsertAsync call, captured at call time: the service reuses (and clears) its batch list,
    // so inspecting the recorded argument afterwards could see an emptied list.
    private readonly List<int> _insertSizes = new();

    /// <summary>
    /// Enqueues the clicks, starts the service and stops it. StopAsync drains whatever is still in memory
    /// and flushes it, so all clicks are written by the time it returns — no sleeping needed.
    /// </summary>
    private async Task RunWithClicksAsync(int clickCount, int batchSize = 10, int flushIntervalSeconds = 1)
    {
        var (sut, queue) = CreateService(batchSize, flushIntervalSeconds);
        using (sut)
        {
            Enqueue(queue, clickCount);

            await sut.StartAsync(CancellationToken.None);
            await sut.StopAsync(CancellationToken.None);
        }
    }

    private (ClickRecorderBackgroundService Sut, ChannelClickQueue Queue) CreateService(int batchSize, int flushIntervalSeconds)
    {
        var analytics = Options.Create(new AnalyticsOptions { BatchSize = batchSize, FlushIntervalSeconds = flushIntervalSeconds });
        var queue = new ChannelClickQueue(analytics);
        var resilience = new SqlResilience(
            Options.Create(new ResilienceOptions { SqlMaxRetries = SqlMaxRetries, SqlBaseDelayMs = 0, StartupBaseDelaySeconds = 0 }),
            NullLogger<SqlResilience>.Instance);

        var sut = new ClickRecorderBackgroundService(
            queue, _writer.Object, resilience, _telemetry.Metrics, analytics, NullLogger<ClickRecorderBackgroundService>.Instance);
        return (sut, queue);
    }

    private static void Enqueue(ChannelClickQueue queue, int clickCount)
    {
        for (var i = 0; i < clickCount; i++)
        {
            queue.TryEnqueue(new ClickRecord(i + 1, Now, $"visitor-{i}", "US")).Should().BeTrue();
        }
    }

    /// <summary>Records the size of each insert and lets <paramref name="behaviour"/> decide its outcome (1-based call number).</summary>
    private void SetupWriter(Func<int, int, Exception?> behaviour)
    {
        _writer.Setup(w => w.InsertAsync(It.IsAny<IReadOnlyCollection<ClickRecord>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<ClickRecord> clicks, CancellationToken _) =>
            {
                _insertSizes.Add(clicks.Count);
                var error = behaviour(_insertSizes.Count, clicks.Count);
                return error is null ? Task.CompletedTask : Task.FromException(error);
            });
    }

    // ---- batching (CLK-AC-08) ----------------------------------------------------------

    [Fact]
    public async Task Flush_MoreClicksThanBatchSize_WritesBatchesOfAtMostBatchSizeInOrder()
    {
        // The flush interval is far longer than the test, so only batch size (and the shutdown drain) can write.
        SetupWriter((_, _) => null);

        await RunWithClicksAsync(5, batchSize: 2, flushIntervalSeconds: 3600);

        _insertSizes.Should().Equal(2, 2, 1);
    }

    [Fact]
    public async Task Flush_PartialBatch_IsWrittenByTheFlushIntervalWithoutShutdown()
    {
        SetupWriter((_, _) => null);
        var (sut, queue) = CreateService(batchSize: 100, flushIntervalSeconds: 1);
        using (sut)
        {
            Enqueue(queue, 1);
            await sut.StartAsync(CancellationToken.None);

            // Wait for the writer before stopping: StopAsync would flush it too, which would prove nothing.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_insertSizes.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            var sizesBeforeStop = _insertSizes.ToArray();
            await sut.StopAsync(CancellationToken.None);

            sizesBeforeStop.Should().Equal(1);
        }
    }

    // ---- transient failures ------------------------------------------------------------

    [Fact]
    public async Task Flush_TransientFailureOnce_RetriesWholeBatchWithoutRowByRow()
    {
        // RSL-AC-04
        SetupWriter((call, _) => call == 1 ? new TimeoutException() : null);

        await RunWithClicksAsync(3);

        _insertSizes.Should().Equal(3, 3);
        _writer.Verify(w => w.InsertAsync(It.Is<IReadOnlyCollection<ClickRecord>>(c => c.Count == 1), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Flush_TransientFailureEveryAttempt_DropsBatchWithoutRowByRow()
    {
        // RSL-AC-05
        SetupWriter((_, _) => new TimeoutException());

        await RunWithClicksAsync(3);

        _insertSizes.Should().HaveCount(1 + SqlMaxRetries).And.OnlyContain(size => size == 3);
        _writer.Verify(w => w.InsertAsync(It.Is<IReadOnlyCollection<ClickRecord>>(c => c.Count == 1), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- non-transient failures --------------------------------------------------------

    [Fact]
    public async Task Flush_NonTransientBatchFailure_FallsBackToRowByRow()
    {
        // RSL-AC-06: e.g. FK violation because a link was deleted after its click was queued.
        SetupWriter((_, size) => size > 1 ? new DbUpdateException("FK violation", new InvalidOperationException()) : null);

        await RunWithClicksAsync(3);

        _insertSizes.Should().Equal(3, 1, 1, 1);
        _writer.Verify(w => w.InsertAsync(It.Is<IReadOnlyCollection<ClickRecord>>(c => c.Count == 1), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Flush_TransientFailureDuringRowByRow_StopsAndDropsTheRemainingClicks()
    {
        // RSL-AC-09: call 1 is the batch (non-transient, triggers the fallback); call 2 is the first single row
        // and hits an unavailable database, so the other two clicks must not be attempted.
        using var stored = _telemetry.Collect<long>("urlshortener.clicks.stored");
        using var dropped = _telemetry.Collect<long>("urlshortener.clicks.dropped");
        SetupWriter((_, size) => size > 1
            ? new DbUpdateException("FK violation", new InvalidOperationException())
            : new TimeoutException());

        await RunWithClicksAsync(3);

        _insertSizes.Should().Equal(3, 1);
        var drops = dropped.GetMeasurementSnapshot();
        drops.Sum(m => m.Value).Should().Be(3);
        drops.Should().OnlyContain(m => Equals(m.Tags["reason"], "insert_failed"));
        stored.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_TransientFailureAfterSomeRowsWereStored_DropsOnlyTheRemainder()
    {
        // RSL-AC-09, mixed case: call 1 is the batch (non-transient, triggers the fallback), call 2 stores the
        // first click, call 3 hits an unavailable database, so click 3 of 4 onwards must be dropped unattempted.
        using var stored = _telemetry.Collect<long>("urlshortener.clicks.stored");
        using var dropped = _telemetry.Collect<long>("urlshortener.clicks.dropped");
        SetupWriter((call, size) => call switch
        {
            1 => new DbUpdateException("FK violation", new InvalidOperationException()),
            2 => null,
            _ => new TimeoutException()
        });

        await RunWithClicksAsync(4);

        _insertSizes.Should().Equal(4, 1, 1);
        stored.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(1);
        dropped.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(3);
    }

    // ---- click metrics (OBS-AC-05) -----------------------------------------------------

    [Fact]
    public async Task Flush_Success_RecordsAllClicksStored()
    {
        using var stored = _telemetry.Collect<long>("urlshortener.clicks.stored");
        using var dropped = _telemetry.Collect<long>("urlshortener.clicks.dropped");
        SetupWriter((_, _) => null);

        await RunWithClicksAsync(3);

        stored.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(3);
        dropped.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_TransientFailureEveryAttempt_RecordsWholeBatchDroppedAsInsertFailed()
    {
        using var stored = _telemetry.Collect<long>("urlshortener.clicks.stored");
        using var dropped = _telemetry.Collect<long>("urlshortener.clicks.dropped");
        SetupWriter((_, _) => new TimeoutException());

        await RunWithClicksAsync(3);

        var drops = dropped.GetMeasurementSnapshot();
        drops.Sum(m => m.Value).Should().Be(3);
        drops.Should().OnlyContain(m => Equals(m.Tags["reason"], "insert_failed"));
        stored.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_RowByRowWithOneFailingClick_RecordsTwoStoredAndOneDropped()
    {
        // Call 1 is the batch (non-transient failure); calls 2-4 are single rows, the second of which fails.
        using var stored = _telemetry.Collect<long>("urlshortener.clicks.stored");
        using var dropped = _telemetry.Collect<long>("urlshortener.clicks.dropped");
        SetupWriter((call, size) => size > 1 || call == 3 ? new DbUpdateException("FK violation", new InvalidOperationException()) : null);

        await RunWithClicksAsync(3);

        _insertSizes.Should().Equal(3, 1, 1, 1);
        stored.GetMeasurementSnapshot().Sum(m => m.Value).Should().Be(2);
        var drops = dropped.GetMeasurementSnapshot();
        drops.Sum(m => m.Value).Should().Be(1);
        drops.Should().OnlyContain(m => Equals(m.Tags["reason"], "insert_failed"));
    }
}
