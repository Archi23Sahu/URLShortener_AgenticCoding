using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.Repositories;

namespace UrlShortener.Services;

/// <summary>
/// One cleanup pass: deletes links that expired longer ago than the grace period, in small batches.
/// Kept separate from the timer (ExpiredLinkCleanupService) so it can be unit tested.
/// Cache entries need no eviction: each one lives for at most the link's remaining lifetime,
/// so it is already gone by the time the link is deleted.
/// </summary>
public sealed class ExpiredLinkCleaner
{
    private readonly IShortUrlRepository _repository;
    private readonly TimeProvider _clock;
    private readonly CleanupOptions _options;
    private readonly ILogger<ExpiredLinkCleaner> _logger;

    public ExpiredLinkCleaner(
        IShortUrlRepository repository,
        TimeProvider clock,
        IOptions<CleanupOptions> options,
        ILogger<ExpiredLinkCleaner> logger)
    {
        _repository = repository;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns the number of links deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(Math.Max(0, _options.GracePeriodDays));
        var batchSize = Math.Max(1, _options.BatchSize);
        var total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var deleted = await _repository.DeleteExpiredAsync(cutoff, batchSize, ct);
            total += deleted;

            // A short batch means nothing is left to delete.
            if (deleted < batchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            _logger.LogInformation("Cleanup deleted {Count} link(s) that expired before {Cutoff:O}", total, cutoff);
        }

        return total;
    }
}
