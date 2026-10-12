using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UrlShortener.Entities;

namespace UrlShortener.Configurations;

public class ShortUrlConfiguration : IEntityTypeConfiguration<ShortUrl>
{
    // Base62 uses upper and lower case letters, so "aB" and "Ab" are different codes.
    // SQL Server's default collation is case-insensitive, so Code needs a case-sensitive one.
    // If you switch to PostgreSQL, remove UseCollation (it is case-sensitive by default).
    private const string CaseSensitiveCollation = "SQL_Latin1_General_CP1_CS_AS";

    public void Configure(EntityTypeBuilder<ShortUrl> builder)
    {
        builder.ToTable("ShortUrls");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Code)
            .HasColumnType("varchar(12)")
            .UseCollation(CaseSensitiveCollation)
            .IsRequired();

        builder.Property(x => x.OriginalUrl)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(x => x.OriginalUrlHash)
            .HasColumnType("char(64)")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.ExpiresAt)
            .HasColumnType("datetime2")
            .IsRequired();

        // Optimistic locking for PUT updates.
        builder.Property(x => x.RowVersion)
            .IsRowVersion();

        // Redirect lookup by code. The unique constraint also catches Base62 collisions.
        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasDatabaseName("UX_ShortUrls_Code");

        // Duplicate detection. Non-unique on purpose: an expired link the cleanup job
        // has not removed yet must not block re-shortening the same URL.
        builder.HasIndex(x => x.OriginalUrlHash)
            .HasDatabaseName("IX_ShortUrls_OriginalUrlHash");

        // Cleanup job scans for expired rows.
        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("IX_ShortUrls_ExpiresAt");

        builder.HasMany(x => x.ClickEvents)
            .WithOne(x => x.ShortUrl)
            .HasForeignKey(x => x.ShortUrlId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
