using Microsoft.EntityFrameworkCore;
using UrlShortener.Resilience;

namespace UrlShortener.Repositories;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending EF Core migrations. SQL Server in Docker takes several seconds to accept
    /// connections, so the work runs through the Polly startup pipeline (spec 010, RSL-AC-07).
    /// </summary>
    public static async Task MigrateWithRetryAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var resilience = services.GetRequiredService<SqlResilience>();

        try
        {
            await resilience.Startup.ExecuteAsync(async token =>
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(token);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Could not apply database migrations; giving up");
            throw;
        }

        logger.LogInformation("Database migrations applied.");
    }
}
