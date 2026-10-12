using UrlShortener.Entities;

namespace UrlShortener.Repositories;

/// <summary>Clicks and unique visitors for one UTC calendar day.</summary>
public record DailyClickRow(DateTime Date, long Clicks, long UniqueVisitors);

/// <summary>Clicks for one value (a country code or browser name). Key is null when the value is unknown.</summary>
public record BreakdownRow(string? Key, long Clicks);

public record ClickStats(long Clicks, long UniqueVisitors, IReadOnlyDictionary<string, long> Countries);

public interface IShortUrlRepository
{
    /// <summary>Read-only lookup (no change tracking). Used by redirect and analytics.</summary>
    Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct);

    /// <summary>Tracked lookup so changes can be saved with optimistic locking.</summary>
    Task<ShortUrl?> GetByCodeForUpdateAsync(string code, CancellationToken ct);

    /// <summary>Finds a non-expired link with the same normalized URL (duplicate detection).</summary>
    Task<ShortUrl?> FindActiveByHashAsync(string hash, string normalizedUrl, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Inserts the row, then derives the Base62 code from the generated identity value.
    /// Both steps run in one transaction so a row never exists without its final code.
    /// </summary>
    Task<ShortUrl> AddAsync(ShortUrl entity, Func<long, string> codeFromId, CancellationToken ct);

    /// <summary>Saves tracked changes. Throws ConcurrencyConflictException on a RowVersion mismatch.</summary>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Deletes the link; its click events are removed by the database cascade. Returns false if not found.</summary>
    Task<bool> DeleteByCodeAsync(string code, CancellationToken ct);

    Task<ClickStats> GetClickStatsAsync(long shortUrlId, CancellationToken ct);

    /// <summary>Clicks per UTC day in [fromUtc, toExclusiveUtc). Days with no clicks are not returned.</summary>
    Task<IReadOnlyList<DailyClickRow>> GetDailyClicksAsync(long shortUrlId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct);

    /// <summary>Clicks per country, including a null key for unknown. Null bounds mean "no limit".</summary>
    Task<IReadOnlyList<BreakdownRow>> GetCountryBreakdownAsync(long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct);

    /// <summary>Clicks per browser, including a null key for unknown. Null bounds mean "no limit".</summary>
    Task<IReadOnlyList<BreakdownRow>> GetBrowserBreakdownAsync(long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct);

    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> links whose ExpiresAt is before the cutoff (their click events
    /// go with them via cascade). Returns how many links were deleted.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}
