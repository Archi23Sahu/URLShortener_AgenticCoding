using Microsoft.EntityFrameworkCore;
using UrlShortener.Entities;

namespace UrlShortener.Repositories;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<ShortUrl> ShortUrls => Set<ShortUrl>();

    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime column holds UTC; mark values read back as UTC (see UtcDateTimeConverter).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Picks up ShortUrlConfiguration and ClickEventConfiguration.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
