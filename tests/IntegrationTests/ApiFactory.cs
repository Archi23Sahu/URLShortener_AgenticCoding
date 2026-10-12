using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Repositories;
using Microsoft.AspNetCore.TestHost;

namespace UrlShortener.IntegrationTests;

/// <summary>
/// Starts the real application in memory (real controllers, validation, middleware, EF Core and SQL Server).
/// Only two things are swapped: Redis becomes an in-memory cache, and the database is a separate test database.
/// SQL Server is required: start it with "docker compose up -d sqlserver".
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    // Same server as docker-compose.yml, but a different database so dev data is never touched.
    private const string DefaultConnection =
        "Server=localhost,1433;Database=UrlShortener_IntegrationTests;User Id=sa;Password=Your_strong_Passw0rd!;TrustServerCertificate=True";

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("URLSHORTENER_TEST_DB") ?? DefaultConnection;

    private static readonly object DbLock = new();
    private static bool _dbReady;

    private readonly Dictionary<string, string?> _overrides;

    static ApiFactory()
    {
        // Program.cs reads the Redis connection string while the host is being built, before test
        // configuration callbacks run. Environment variables are visible at that point, so they are used here.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "");
    }

    public ApiFactory() : this(new Dictionary<string, string?>())
    {
    }

    protected ApiFactory(Dictionary<string, string?> overrides)
    {
        _overrides = overrides;
        EnsureDatabase();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // Tests that are not about rate limiting must never be throttled.
                ["RateLimit:Enabled"] = "false",
                // The cleanup job must not delete rows while a test is running.
                ["Cleanup:Enabled"] = "false",
                // Flush clicks quickly so tests do not wait long.
                ["Analytics:FlushIntervalSeconds"] = "1",
                ["Analytics:Salt"] = "integration-test-salt",
                ["Shortener:BaseUrl"] = "http://short.test"
            };

            foreach (var item in _overrides)
            {
                settings[item.Key] = item.Value;
            }

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
        });
    }

    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void EnsureDatabase()
    {
        lock (DbLock)
        {
            if (_dbReady)
            {
                return;
            }

            // Integration tests write to and bulk-delete from this database, so refuse anything that
            // does not look like a dedicated test database (e.g. a dev or shared DB set by mistake).
            var catalog = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
            if (catalog is null || !catalog.Contains("IntegrationTests", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to run integration tests against database '{catalog}': its name must contain " +
                    "'IntegrationTests' because the tests write to and bulk-delete from it. " +
                    "Point URLSHORTENER_TEST_DB at a dedicated test database (default: UrlShortener_IntegrationTests).");
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options;
            using var db = new AppDbContext(options);

            try
            {
                if (db.Database.GetMigrations().Any())
                {
                    db.Database.Migrate();
                }
                else
                {
                    db.Database.EnsureCreated();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not prepare the integration-test database. Is SQL Server running? " +
                    "Start it with 'docker compose up -d sqlserver', or set URLSHORTENER_TEST_DB to another connection string.", ex);
            }

            _dbReady = true;
        }
    }
}
