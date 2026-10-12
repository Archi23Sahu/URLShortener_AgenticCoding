using FluentAssertions;
using Microsoft.Extensions.Options;
using UrlShortener.Analytics;
using UrlShortener.Configurations;

namespace UrlShortener.UnitTests.Observability;

public class UrlShortenerMetricsTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ClickRecord Click(long id = 1) => new(id, Now, new string('a', 64), null);

    // ---- queue depth gauge (OBS-AC-06) ----

    [Fact]
    public void QueueDepth_OnCollection_ReportsClicksCurrentlyQueued()
    {
        var queue = new ChannelClickQueue(Options.Create(new AnalyticsOptions { QueueCapacity = 10 }));
        var telemetry = TestMetrics.Create(queue);
        using var depth = telemetry.Collect<int>("urlshortener.clicks.queue_depth");
        queue.TryEnqueue(Click(1)).Should().BeTrue();
        queue.TryEnqueue(Click(2)).Should().BeTrue();

        depth.RecordObservableInstruments();
        depth.LastMeasurement!.Value.Should().Be(2);

        // Read live on every collection, not captured once: taking a click off lowers it.
        queue.Reader.TryRead(out _).Should().BeTrue();
        depth.RecordObservableInstruments();
        depth.LastMeasurement!.Value.Should().Be(1);
    }
}
