using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;

namespace UrlShortener.Analytics;

public interface IClickQueue
{
    /// <summary>Adds a click without waiting. Returns false when the queue is full (click dropped).</summary>
    bool TryEnqueue(ClickRecord click);

    ChannelReader<ClickRecord> Reader { get; }

    /// <summary>Clicks currently waiting to be written (exported as a metric).</summary>
    int Count { get; }
}

/// <summary>
/// Bounded in-memory queue between the redirect endpoint (many writers) and the
/// background worker (one reader). Bounded on purpose: a traffic spike or a slow database
/// must never grow memory without limit.
/// </summary>
public sealed class ChannelClickQueue : IClickQueue
{
    private readonly Channel<ClickRecord> _channel;

    public ChannelClickQueue(IOptions<AnalyticsOptions> options)
    {
        var capacity = Math.Max(1, options.Value.QueueCapacity);

        _channel = Channel.CreateBounded<ClickRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // with TryWrite this means "return false", not "block"
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool TryEnqueue(ClickRecord click) => _channel.Writer.TryWrite(click);

    public ChannelReader<ClickRecord> Reader => _channel.Reader;

    public int Count => _channel.Reader.Count;
}
