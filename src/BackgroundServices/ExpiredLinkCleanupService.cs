using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.Services;

namespace UrlShortener.BackgroundServices;

/// <summary>Runs the expired-link cleanup on a timer. A failed pass is logged and retried at the next interval.</summary>
public sealed class ExpiredLinkCleanupService : BackgroundService
{
    // Let the app finish starting (and migrations finish) before the first pass.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _clock;
    private readonly CleanupOptions _options;
    private readonly ILogger<ExpiredLinkCleanupService> _logger;

    public ExpiredLinkCleanupService(
        IServiceScopeFactory scopeFactory,
        TimeProvider clock,
        IOptions<CleanupOptions> options,
        ILogger<ExpiredLinkCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Expired link cleanup is disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));

        try
        {
            // Timers run on the injected clock so the schedule can be tested without waiting.
            await Task.Delay(StartupDelay, _clock, stoppingToken);

            using var timer = new PeriodicTimer(interval, _clock);
            do
            {
                await RunPassAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        try
        {
            // DbContext is scoped, so every pass gets its own scope.
            using var scope = _scopeFactory.CreateScope();
            var cleaner = scope.ServiceProvider.GetRequiredService<ExpiredLinkCleaner>();
            await cleaner.RunOnceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Expired link cleanup failed; it will be retried at the next interval");
        }
    }
}
