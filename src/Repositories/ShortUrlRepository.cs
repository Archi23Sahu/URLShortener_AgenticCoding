using Microsoft.EntityFrameworkCore;
using UrlShortener.Entities;
using UrlShortener.Exceptions;

namespace UrlShortener.Repositories;

public class ShortUrlRepository : IShortUrlRepository
{
    private readonly AppDbContext _db;

    public ShortUrlRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct) =>
        _db.ShortUrls.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);

    public Task<ShortUrl?> GetByCodeForUpdateAsync(string code, CancellationToken ct) =>
        _db.ShortUrls.FirstOrDefaultAsync(x => x.Code == code, ct);

    public Task<ShortUrl?> FindActiveByHashAsync(string hash, string normalizedUrl, DateTime nowUtc, CancellationToken ct) =>
        _db.ShortUrls
            .AsNoTracking()
            .Where(x => x.OriginalUrlHash == hash && x.ExpiresAt > nowUtc && x.OriginalUrl == normalizedUrl)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<ShortUrl> AddAsync(ShortUrl entity, Func<long, string> codeFromId, CancellationToken ct)
    {
        // Code is NOT NULL and unique, but Base62 needs the identity Id first.
        // Park a unique placeholder (12 chars, fits varchar(12)), then replace it once the Id is known.
        entity.Code = "tmp" + Guid.NewGuid().ToString("N")[..9];

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.ShortUrls.Add(entity);
        await _db.SaveChangesAsync(ct);

        entity.Code = codeFromId(entity.Id);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return entity;
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(ex);
        }
    }

    public async Task<bool> DeleteByCodeAsync(string code, CancellationToken ct) =>
        await _db.ShortUrls.Where(x => x.Code == code).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<DailyClickRow>> GetDailyClicksAsync(
        long shortUrlId, DateTime fromUtc, DateTime toExclusiveUtc, CancellationToken ct)
    {
        // Range filter on (ShortUrlId, ClickedAt) uses IX_ClickEvents_ShortUrlId_ClickedAt.
        return await _db.ClickEvents
            .AsNoTracking()
            .Where(c => c.ShortUrlId == shortUrlId && c.ClickedAt >= fromUtc && c.ClickedAt < toExclusiveUtc)
            .GroupBy(c => c.ClickedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyClickRow(g.Key, g.LongCount(), g.Select(c => c.VisitorHash).Distinct().LongCount()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BreakdownRow>> GetCountryBreakdownAsync(
        long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct)
    {
        return await InRange(shortUrlId, fromUtc, toExclusiveUtc)
            .GroupBy(c => c.Country)
            .Select(g => new BreakdownRow(g.Key, g.LongCount()))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BreakdownRow>> GetBrowserBreakdownAsync(
        long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct)
    {
        return await InRange(shortUrlId, fromUtc, toExclusiveUtc)
            .GroupBy(c => c.Browser)
            .Select(g => new BreakdownRow(g.Key, g.LongCount()))
            .ToListAsync(ct);
    }

    private IQueryable<ClickEvent> InRange(long shortUrlId, DateTime? fromUtc, DateTime? toExclusiveUtc)
    {
        var query = _db.ClickEvents.AsNoTracking().Where(c => c.ShortUrlId == shortUrlId);

        if (fromUtc.HasValue)
        {
            query = query.Where(c => c.ClickedAt >= fromUtc.Value);
        }

        if (toExclusiveUtc.HasValue)
        {
            query = query.Where(c => c.ClickedAt < toExclusiveUtc.Value);
        }

        return query;
    }

    public async Task<int> DeleteExpiredAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        // Pick the batch by id first (uses IX_ShortUrls_ExpiresAt), then delete exactly those rows.
        var ids = await _db.ShortUrls
            .Where(x => x.ExpiresAt < cutoffUtc)
            // Oldest first, in the order of IX_ShortUrls_ExpiresAt, so a batch is a short index range scan
            // rather than a sort of every expired row.
            .OrderBy(x => x.ExpiresAt)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            return 0;
        }

        return await _db.ShortUrls.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<ClickStats> GetClickStatsAsync(long shortUrlId, CancellationToken ct)
    {
        var clicks = _db.ClickEvents.AsNoTracking().Where(c => c.ShortUrlId == shortUrlId);

        var total = await clicks.LongCountAsync(ct);
        var unique = await clicks.Select(c => c.VisitorHash).Distinct().LongCountAsync(ct);
        var countries = await clicks
            .Where(c => c.Country != null)
            .GroupBy(c => c.Country!)
            .Select(g => new { Country = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Country, x => x.Count, ct);

        return new ClickStats(total, unique, countries);
    }
}
