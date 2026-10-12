namespace UrlShortener.Configurations;

public class CleanupOptions
{
    public const string SectionName = "Cleanup";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the cleanup runs.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Links are deleted only this many days after they expired. Until then a visitor still gets
    /// "410 Gone" (the link existed) instead of "404 Not Found". Deleting a link also deletes its
    /// click history, so a longer grace period keeps analytics available for longer.
    /// </summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>Rows deleted per statement. Small batches keep locks short on a busy table.</summary>
    public int BatchSize { get; set; } = 1000;
}
