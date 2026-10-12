using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;
using UrlShortener.Analytics;
using UrlShortener.Observability;

namespace UrlShortener.UnitTests;

/// <summary>
/// A real <see cref="UrlShortenerMetrics"/> on its own meter factory. Create one per test: collectors only see
/// meters from their own factory, so measurements from other tests can never leak in.
/// </summary>
public sealed class TestMetrics
{
    private TestMetrics(IMeterFactory meterFactory, IClickQueue queue)
    {
        MeterFactory = meterFactory;
        Metrics = new UrlShortenerMetrics(meterFactory, queue);
    }

    public IMeterFactory MeterFactory { get; }

    public UrlShortenerMetrics Metrics { get; }

    public static TestMetrics Create(IClickQueue? queue = null) =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            queue ?? Mock.Of<IClickQueue>());

    /// <summary>Collects one instrument, e.g. "urlshortener.redirects".</summary>
    public MetricCollector<T> Collect<T>(string instrument) where T : struct =>
        new(MeterFactory, UrlShortenerMetrics.MeterName, instrument);
}
