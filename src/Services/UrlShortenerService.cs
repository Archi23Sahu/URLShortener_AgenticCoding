using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Entities;
using UrlShortener.Exceptions;
using UrlShortener.Repositories;
using UrlShortener.Resilience;

namespace UrlShortener.Services;

public class UrlShortenerService : IUrlShortenerService
{
    private readonly IShortUrlRepository _repository;
    private readonly ShortenerOptions _options;
    private readonly TimeProvider _clock;
    private readonly SqlResilience _resilience;
    private readonly ILogger<UrlShortenerService> _logger;

    public UrlShortenerService(
        IShortUrlRepository repository,
        IOptions<ShortenerOptions> options,
        TimeProvider clock,
        SqlResilience resilience,
        ILogger<UrlShortenerService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _clock = clock;
        _resilience = resilience;
        _logger = logger;
    }

    public async Task<CreateResult> CreateAsync(CreateShortUrlRequest request, CancellationToken ct)
    {
        var normalized = Normalize(request.Url);
        var hash = UrlNormalizer.ComputeHash(normalized);
        var now = Now();

        // Duplicate detection: reuse an active link for the same URL instead of minting a new code.
        var existing = await _repository.FindActiveByHashAsync(hash, normalized, now, ct);
        if (existing is not null)
        {
            _logger.LogInformation("Duplicate URL detected; returning existing code {Code}", existing.Code);
            return new CreateResult(ToResponse(existing), Created: false);
        }

        var entity = new ShortUrl
        {
            OriginalUrl = normalized,
            OriginalUrlHash = hash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(request.ExpiryDays ?? _options.DefaultExpiryDays)
        };

        var created = await _repository.AddAsync(entity, ShortCodeGenerator.FromId, ct);
        _logger.LogInformation("Created short URL {Code} expiring {ExpiresAt:O}", created.Code, created.ExpiresAt);

        return new CreateResult(ToResponse(created), Created: true);
    }

    public async Task<ResolvedLink> ResolveAsync(string code, CancellationToken ct)
    {
        // Read-only and idempotent, so transient SQL errors are retried within a time budget (spec 010, RSL-AC-02/08).
        var entity = await _resilience.RequestRead.ExecuteAsync(
                         async token => await _repository.GetByCodeAsync(code, token), ct)
                     ?? throw new NotFoundException(code);

        if (entity.ExpiresAt <= Now())
        {
            throw new LinkExpiredException(code);
        }

        return new ResolvedLink(entity.Id, entity.OriginalUrl, entity.ExpiresAt);
    }

    public async Task<ShortUrlResponse> UpdateAsync(string code, UpdateShortUrlRequest request, CancellationToken ct)
    {
        var entity = await _repository.GetByCodeForUpdateAsync(code, ct)
                     ?? throw new NotFoundException(code);

        var now = Now();
        if (entity.ExpiresAt <= now)
        {
            throw new LinkExpiredException(code);
        }

        var normalized = Normalize(request.Url);
        entity.OriginalUrl = normalized;
        entity.OriginalUrlHash = UrlNormalizer.ComputeHash(normalized);

        if (request.ExpiryDays.HasValue)
        {
            entity.ExpiresAt = now.AddDays(request.ExpiryDays.Value);
        }

        await _repository.SaveChangesAsync(ct);
        _logger.LogInformation("Updated short URL {Code}", code);

        return ToResponse(entity);
    }

    public async Task DeleteAsync(string code, CancellationToken ct)
    {
        var deleted = await _repository.DeleteByCodeAsync(code, ct);
        if (!deleted)
        {
            throw new NotFoundException(code);
        }

        _logger.LogInformation("Deleted short URL {Code}", code);
    }

    public async Task<AnalyticsResponse> GetAnalyticsAsync(string code, CancellationToken ct)
    {
        var entity = await _repository.GetByCodeAsync(code, ct)
                     ?? throw new NotFoundException(code);

        var stats = await _repository.GetClickStatsAsync(entity.Id, ct);
        return new AnalyticsResponse(stats.Clicks, stats.UniqueVisitors, stats.Countries);
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static string Normalize(string? url)
    {
        if (!UrlNormalizer.TryNormalize(url, out var normalized))
        {
            throw new InvalidRequestException("Url must be an absolute http or https URL.");
        }

        return normalized;
    }

    private ShortUrlResponse ToResponse(ShortUrl entity) =>
        new($"{_options.BaseUrl.TrimEnd('/')}/{entity.Code}", entity.ExpiresAt);
}
