using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace UrlShortener.Repositories;

/// <summary>
/// Marks every DateTime read from SQL Server as UTC. datetime2 stores no offset, so EF Core would
/// otherwise return DateTimeKind.Unspecified and System.Text.Json would serialize it without "Z",
/// letting clients read it as local time. Values are written unchanged: the app only ever stores
/// UTC times (TimeProvider.GetUtcNow().UtcDateTime).
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
