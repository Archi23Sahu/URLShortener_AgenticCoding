# 009 — Observability (metrics, monitoring, health)

| | |
|---|---|
| Status | Implemented |
| AC prefix | `OBS` |
| Last verified | 2026-10-12 |
| Related specs | 002 (redirect), 004 (cache), 005 (click tracking), 010 (resilience) |
| Plans | [012](../plans/012-observability.md) |
| Source | Requirement "Reliability & Production Features": *Metrics and monitoring*, *Health Checks* |

## Purpose

Lets an operator see how the service behaves in production: request and business metrics, cache effectiveness, click-pipeline health, and a health endpoint that reflects every dependency, including Redis.

## API contract

| Route | Response |
|-------|----------|
| `GET /health` | `200 Healthy` / `200 Degraded` / `503 Unhealthy` (plain text, ASP.NET Core default writer). Not rate limited. |

Telemetry is pushed, not served: OpenTelemetry exports over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. No `/metrics` scrape endpoint.

## Behaviour

1. A single `Meter` named `UrlShortener` (created through `IMeterFactory`) owns all application instruments:

   | Instrument | Kind | Tags | Incremented when |
   |------------|------|------|------------------|
   | `urlshortener.links.created` | Counter | `result` = `created` \| `duplicate` | `POST /api/v1/shorten` succeeds |
   | `urlshortener.redirects` | Counter | `outcome` = `found` \| `not_found` \| `expired` | `GET /{code}` finishes (302 / 404 / 410) |
   | `urlshortener.cache.lookups` | Counter | `result` = `hit` \| `miss` \| `error` \| `bypassed` | Cache decorator resolves a code: `hit`; `miss` (no entry, or entry past its expiry); `error` (Redis read failed, or the entry was unreadable JSON); `bypassed` (breaker open) |
   | `urlshortener.clicks.queued` | Counter | — | Click accepted by the queue |
   | `urlshortener.clicks.dropped` | Counter | `reason` = `queue_full` \| `insert_failed` | Click lost |
   | `urlshortener.clicks.stored` | Counter | — | Click rows saved |
   | `urlshortener.clicks.queue_depth` | ObservableGauge | — | Read on collection (clicks waiting in memory) |

2. OpenTelemetry collects that meter plus ASP.NET Core (request duration, active requests), HttpClient and .NET runtime metrics, and ASP.NET Core/HttpClient traces. Resource `service.name` = `url-shortener`.
3. The OTLP exporter is added **only** when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured; without it the app runs normally with no exporter and no errors.
4. `docker-compose.yml` adds an `aspire-dashboard` service (`mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2`, metrics and traces UI at `http://127.0.0.1:18888`; 13.5.2 was built on 2026-09-12 and is pinned because the newer 13.6.x images were under two weeks old when this was chosen on 2026-10-11). The API exports to it over the compose network (`OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889`). The UI is published on loopback only and the OTLP port is not published; anonymous access is for local use.
5. **Health:** `/health` runs a `database` check and a `redis` check (`RedisHealthCheck`). The `redis` check probes a key through the distributed cache when the cache **is Redis**; reachable → Healthy, unreachable or timing out (2 s) → **Degraded** (the app still serves from SQL, spec 004), never Unhealthy. When no Redis is configured (in-memory cache) it reports Healthy without probing. SQL down → Unhealthy (503). The overall status is the worst of all checks. *(Design note: the check is always registered rather than only when `ConnectionStrings:Redis` is set, so integration tests can exercise it by swapping in a real `RedisCache`; `Program.cs` reads the connection string before test configuration applies.)*
6. Recording a metric never throws and never adds I/O to the redirect path.

## Edge cases and failure modes

- No OTLP collector running but endpoint configured: the exporter retries in the background; requests are unaffected.
- Redis probe uses the client's existing timeouts (connect 1 s, sync/async 500 ms), so `/health` stays fast when Redis is down.
- Cardinality: no tag carries a short code, URL, IP or user agent.

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` (env) | unset | Enables OTLP export when set (compose sets it to the dashboard). |
| `OTEL_SERVICE_NAME` (env) | `url-shortener` | Overrides the resource name. |

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| OBS-AC-01 | When a new link is created, then `urlshortener.links.created` +1 with `result=created`; when a duplicate is returned, `result=duplicate`. | `ShortenControllerTests.Create_Success_RecordsLinkCreatedWithResultTag`, `ShortenControllerTests.Create_InvalidRequest_RecordsNothing` |
| OBS-AC-02 | When a redirect returns 302 / 404 / 410, then `urlshortener.redirects` +1 with `outcome` `found` / `not_found` / `expired`. | `RedirectControllerTests.Redirect_KnownCode_RecordsFoundOutcome`, `RedirectControllerTests.Redirect_UnknownCode_RecordsNotFoundOutcome`, `RedirectControllerTests.Redirect_ExpiredLink_RecordsExpiredOutcome` |
| OBS-AC-03 | When the cache decorator resolves a code, then `urlshortener.cache.lookups` records `hit`, `miss`, `error` or `bypassed` accordingly. | `CachedUrlShortenerServiceTests.Resolve_MissThenHit_RecordsMissThenHit`, `CachedUrlShortenerServiceTests.Resolve_CachedEntryIsPastExpiry_RecordsMiss`, `CachedUrlShortenerServiceTests.Resolve_CacheReadThrows_RecordsError`, `CachedUrlShortenerServiceTests.Resolve_CachedEntryIsUnreadable_RecordsError`, `CachedUrlShortenerServiceTests.Resolve_DuringCooldownAfterFailure_RecordsBypassed` |
| OBS-AC-04 | When a click is queued, then `clicks.queued` +1; when the queue is full, `clicks.dropped{reason=queue_full}` +1. | `ClickTrackerTests.Track_QueueAccepts_IncrementsClicksQueued`, `ClickTrackerTests.Track_QueueFull_IncrementsClicksDroppedWithQueueFullReason` |
| OBS-AC-05 | When the writer stores N clicks, then `clicks.stored` +N; when it gives up on clicks, `clicks.dropped{reason=insert_failed}` +count. | `ClickRecorderBackgroundServiceTests.Flush_Success_RecordsAllClicksStored`, `ClickRecorderBackgroundServiceTests.Flush_TransientFailureEveryAttempt_RecordsWholeBatchDroppedAsInsertFailed`, `ClickRecorderBackgroundServiceTests.Flush_RowByRowWithOneFailingClick_RecordsTwoStoredAndOneDropped` |
| OBS-AC-06 | `clicks.queue_depth` reports the number of clicks currently queued. | `UrlShortenerMetricsTests.QueueDepth_OnCollection_ReportsClicksCurrentlyQueued` |
| OBS-AC-07 | Given no `OTEL_EXPORTER_OTLP_ENDPOINT`, the app starts and serves requests normally. | `HealthTests.App_WithoutOtlpEndpoint_StartsAndServesRequests` |
| OBS-AC-08 | Given Redis configured and reachable, `/health` returns 200 `Healthy`. | `HealthTests.Health_RedisReachable_ReturnsHealthy` (`[RedisFact]`: skipped unless `URLSHORTENER_TEST_REDIS` is set) |
| OBS-AC-09 | Given Redis configured but unreachable, `/health` returns 200 `Degraded` (not 503) and redirects still work. | `HealthTests.Health_RedisUnreachable_Returns200Degraded`, `HealthTests.Redirect_RedisUnreachable_StillReturns302` |
| OBS-AC-10 | Given no Redis configured (in-memory cache), the `redis` check reports Healthy without probing and `/health` returns `Healthy`. | `ApiEndToEndTests.Health_ReturnsHealthy` |

## Known limitations

- No alerting rules or dashboards are shipped; the Aspire dashboard is for local/demo use.
- Metrics are per instance; aggregation is the collector's job.
- `urlshortener.redirects{outcome=not_found}` counts only 404s that reach the controller. A 404 from the route constraint (code longer than 12 characters or not Base62) never reaches it and is visible only in ASP.NET Core's own request metrics.
- **Traces carry request paths**, and therefore short codes (ASP.NET Core `url.path`). The user agent is removed from spans and `/health` and `/swagger` are not traced, but a trace store should be treated as holding which codes were visited. Query-string values are not recorded.
- Logs are not exported over OTLP: Serilog owns logging, so they stay on the console sink even though `UseOtlpExporter()` enables a logging pipeline.
- `/health` is public and unthrottled by design, and its `database` check opens a SQL connection per call, so a flood of probes competes with redirects for the connection pool. While Redis is down each call waits up to the Redis command timeout (capped at 2 s); the Degraded result carries no exception, so polling doesn't flood the logs.
