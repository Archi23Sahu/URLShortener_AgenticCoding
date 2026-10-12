using System.Diagnostics.Metrics;
using UrlShortener.Analytics;

namespace UrlShortener.Observability;

public enum RedirectOutcome { Found, NotFound, Expired }

public enum CacheLookupResult { Hit, Miss, Error, Bypassed }

public enum ClickDropReason { QueueFull, InsertFailed }

/// <summary>
/// Owns every application metric (spec 009). Instrument names and tags live here only, so dashboards
/// have one source of truth. Tags never carry a short code, URL, IP or user agent: those would explode
/// cardinality and leak personal data. Recording is an in-memory increment: safe on the redirect path.
/// </summary>
public sealed class UrlShortenerMetrics
{
    public const string MeterName = "UrlShortener";

    private readonly Counter<long> _linksCreated;
    private readonly Counter<long> _redirects;
    private readonly Counter<long> _cacheLookups;
    private readonly Counter<long> _clicksQueued;
    private readonly Counter<long> _clicksDropped;
    private readonly Counter<long> _clicksStored;

    public UrlShortenerMetrics(IMeterFactory meterFactory, IClickQueue queue)
    {
        var meter = meterFactory.Create(MeterName);

        _linksCreated = meter.CreateCounter<long>("urlshortener.links.created", "{link}",
            "Successful create requests; result=created for a new code, duplicate when an existing link was returned.");
        _redirects = meter.CreateCounter<long>("urlshortener.redirects", "{redirect}",
            "Redirect requests by outcome (found=302, not_found=404, expired=410).");
        _cacheLookups = meter.CreateCounter<long>("urlshortener.cache.lookups", "{lookup}",
            "Redirect cache lookups (hit, miss, error, bypassed while the cache circuit is open).");
        _clicksQueued = meter.CreateCounter<long>("urlshortener.clicks.queued", "{click}",
            "Clicks accepted by the in-memory queue.");
        _clicksDropped = meter.CreateCounter<long>("urlshortener.clicks.dropped", "{click}",
            "Clicks lost (queue_full, insert_failed).");
        _clicksStored = meter.CreateCounter<long>("urlshortener.clicks.stored", "{click}",
            "Click rows written to the database.");
        meter.CreateObservableGauge("urlshortener.clicks.queue_depth", () => queue.Count, "{click}",
            "Clicks waiting in memory to be written.");
    }

    public void LinkCreated(bool duplicate) =>
        _linksCreated.Add(1, new KeyValuePair<string, object?>("result", duplicate ? "duplicate" : "created"));

    public void Redirect(RedirectOutcome outcome) =>
        _redirects.Add(1, new KeyValuePair<string, object?>("outcome", outcome switch
        {
            RedirectOutcome.Found => "found",
            RedirectOutcome.NotFound => "not_found",
            _ => "expired"
        }));

    public void CacheLookup(CacheLookupResult result) =>
        _cacheLookups.Add(1, new KeyValuePair<string, object?>("result", result switch
        {
            CacheLookupResult.Hit => "hit",
            CacheLookupResult.Miss => "miss",
            CacheLookupResult.Error => "error",
            _ => "bypassed"
        }));

    public void ClickQueued() => _clicksQueued.Add(1);

    public void ClicksDropped(ClickDropReason reason, int count) =>
        _clicksDropped.Add(count, new KeyValuePair<string, object?>("reason",
            reason == ClickDropReason.QueueFull ? "queue_full" : "insert_failed"));

    public void ClicksStored(int count) => _clicksStored.Add(count);
}
