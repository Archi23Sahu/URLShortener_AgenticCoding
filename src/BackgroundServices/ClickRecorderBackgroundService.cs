using Microsoft.Extensions.Options;
using UrlShortener.Analytics;
using UrlShortener.Configurations;
using UrlShortener.Observability;
using UrlShortener.Resilience;

namespace UrlShortener.BackgroundServices;

/// <summary>
/// Takes clicks off the in-memory queue and writes them to ClickEvents in batches
/// (up to BatchSize clicks, or whatever arrived within FlushIntervalSeconds).
/// Inserts go through <see cref="IClickEventWriter"/> (fresh DbContext per call). Transient SQL failures are
/// retried as a batch; non-transient ones fall back to row-by-row inserts (spec 010).
/// </summary>
public sealed class ClickRecorderBackgroundService : BackgroundService
{
    private readonly IClickQueue _queue;
    private readonly IClickEventWriter _writer;
    private readonly SqlResilience _resilience;
    private readonly UrlShortenerMetrics _metrics;
    private readonly ILogger<ClickRecorderBackgroundService> _logger;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    public ClickRecorderBackgroundService(
        IClickQueue queue,
        IClickEventWriter writer,
        SqlResilience resilience,
        UrlShortenerMetrics metrics,
        IOptions<AnalyticsOptions> options,
        ILogger<ClickRecorderBackgroundService> logger)
    {
        _queue = queue;
        _writer = writer;
        _resilience = resilience;
        _metrics = metrics;
        _logger = logger;
        _batchSize = Math.Max(1, options.Value.BatchSize);
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.FlushIntervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.Reader;
        var batch = new List<ClickRecord>(_batchSize);

        try
        {
            // Wait for the first click, then keep collecting until the batch is full or the window closes.
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                window.CancelAfter(_flushInterval);

                try
                {
                    while (batch.Count < _batchSize && await reader.WaitToReadAsync(window.Token))
                    {
                        while (batch.Count < _batchSize && reader.TryRead(out var click))
                        {
                            batch.Add(click);
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The flush window elapsed. Write what we have.
                }

                await FlushAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }

        // Shutdown: write everything still in memory so a normal restart loses nothing.
        // (A crash or kill -9 still loses whatever is queued: that is the documented trade-off.)
        while (_queue.Reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= _batchSize)
            {
                await FlushAsync(batch, CancellationToken.None);
                batch.Clear();
            }
        }

        await FlushAsync(batch, CancellationToken.None);
    }

    private async Task FlushAsync(List<ClickRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // Metrics are recorded outside the try blocks: nothing after a successful insert may send the
        // batch down a failure path (a throwing listener would otherwise cause duplicate rows).
        var stored = 0;
        var dropped = 0;

        try
        {
            // Transient failures (database briefly unavailable) are retried as a whole batch (spec 010).
            await _resilience.Transient.ExecuteAsync(async token => await _writer.InsertAsync(batch, token), ct);
            stored = batch.Count;
        }
        catch (Exception ex) when (SqlTransientErrorDetector.IsTransient(ex))
        {
            // Still failing after the retries: the database is down. Per-row attempts would all fail too.
            dropped = batch.Count;
            _logger.LogError(ex, "Dropped {Count} click(s): database unavailable after retries", batch.Count);
        }
        catch (Exception ex)
        {
            // Not transient. Most likely a link was deleted after its click was queued (foreign key violation),
            // which rejects the whole batch. Retry one by one so a single bad click cannot discard its neighbours.
            _logger.LogWarning(ex, "Batch insert of {Count} click(s) failed; retrying individually", batch.Count);

            for (var i = 0; i < batch.Count; i++)
            {
                var click = batch[i];

                try
                {
                    await _writer.InsertAsync(new[] { click }, ct);
                    stored++;
                }
                catch (Exception single) when (SqlTransientErrorDetector.IsTransient(single))
                {
                    // The database went away while falling back: every remaining click would wait out its own
                    // timeout and stall the writer. Drop the rest and let the queue move on.
                    var remaining = batch.Count - i;
                    dropped += remaining;
                    _logger.LogError(single, "Dropped {Count} click(s): database unavailable during row-by-row insert", remaining);
                    break;
                }
                catch (Exception single)
                {
                    dropped++;
                    _logger.LogError(single, "Dropped click for link {ShortUrlId}", click.ShortUrlId);
                }
            }
        }

        if (stored > 0)
        {
            _metrics.ClicksStored(stored);
        }

        if (dropped > 0)
        {
            _metrics.ClicksDropped(ClickDropReason.InsertFailed, dropped);
        }

        _logger.LogDebug("Flushed {Count} click(s): {Stored} stored, {Dropped} dropped", batch.Count, stored, dropped);
    }
}
