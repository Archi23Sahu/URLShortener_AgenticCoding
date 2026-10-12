using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UrlShortener.Entities;

namespace UrlShortener.Configurations;

public class ClickEventConfiguration : IEntityTypeConfiguration<ClickEvent>
{
    public void Configure(EntityTypeBuilder<ClickEvent> builder)
    {
        builder.ToTable("ClickEvents");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.ClickedAt)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(x => x.VisitorHash)
            .HasColumnType("char(64)")
            .IsRequired();

        builder.Property(x => x.Country)
            .HasColumnType("char(2)")
            .IsRequired(false);

        builder.Property(x => x.Browser)
            .HasColumnType("varchar(30)")
            .IsRequired(false);

        // Analytics queries per link and time range.
        builder.HasIndex(x => new { x.ShortUrlId, x.ClickedAt })
            .HasDatabaseName("IX_ClickEvents_ShortUrlId_ClickedAt");
    }
}
