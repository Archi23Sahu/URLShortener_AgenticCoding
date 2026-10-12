# Plan 012 — Metrics, monitoring and Redis health check

| | |
|---|---|
| Status | Implemented |
| Spec | [009 — Observability](../specs/009-observability.md) |
| ACs satisfied / added | `OBS-AC-01..10` (new) |
| Source | Requirement "Reliability & Production Features": metrics and monitoring; health checks. |

## Goal

Emit application and platform metrics through OpenTelemetry, viewable in an Aspire dashboard started by `docker compose`, and make `/health` report Redis as `Degraded` when it's down.

## Out of scope

- A Prometheus `/metrics` scrape endpoint (its ASP.NET Core exporter is prerelease); alerting rules; shipping logs over OTLP (Serilog console stays as is).
- Changing the `/health` response format (stays plain text so existing probes and tests keep working).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/UrlShortener.csproj` | `OpenTelemetry.Extensions.Hosting`, `.Instrumentation.AspNetCore`, `.Instrumentation.Http`, `.Instrumentation.Runtime`, `.Exporter.OpenTelemetryProtocol` — one stable 1.x version, **published ≥ 2 weeks ago**, still targeting `net8.0`, `--vulnerable` clean. | Metrics and traces. |
| `src/Observability/UrlShortenerMetrics.cs` *(new)* | Singleton built from `IMeterFactory`; creates the instruments in spec 009 behaviour 1; small methods (`LinkCreated(bool duplicate)`, `Redirect(outcome)`, `CacheLookup(result)`, `ClickQueued()`, `ClicksDropped(reason, n)`, `ClicksStored(n)`); observable gauge reads `IClickQueue` depth. | One owner for names/tags; unit-testable. |
| `src/Analytics/ClickQueue.cs` | Add `int Count` to `IClickQueue` (bounded channel supports counting). | Queue-depth gauge. |
| `src/Controllers/ShortenController.cs` (links created), `src/Controllers/RedirectController.cs` (redirect outcome, around `ResolveAsync`), `src/Caching/CachedUrlShortenerService.cs` (cache lookups), `src/Analytics/ClickTracker.cs` (queued / queue_full), `src/BackgroundServices/ClickRecorderBackgroundService.cs` (stored / insert_failed) | Record metrics at the points in spec 009. `UrlShortenerService` is **not** instrumented. | OBS-AC-01..06. |
| `src/Observability/RedisHealthCheck.cs` *(new)* | `IHealthCheck` reading a probe key via `IDistributedCache`; exception/timeout → `Degraded`. | Health check covers Redis. |
| `src/Program.cs` | `AddOpenTelemetry()` with the meter + instrumentation; OTLP exporter only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; the `redis` health check is **always** registered and reports Healthy without probing when the cache is not Redis (so tests can swap in a real `RedisCache`; see spec 009 behaviour 5). Traces: `/health` and `/swagger` filtered out, `user_agent.original` removed. | OBS-AC-07..10. |
| `docker-compose.yml` | Add `aspire-dashboard` (`mcr.microsoft.com/dotnet/aspire-dashboard`, pinned tag), UI `127.0.0.1:18888`, OTLP on the compose network; `api` gets `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_SERVICE_NAME`. | Monitoring UI for the demo. |

## Steps

1. Packages + `UrlShortenerMetrics` + `IClickQueue.Count`. — verify: `dotnet build`; `dotnet list src package --vulnerable` clean.
2. **Tests first (test-author)**: metrics unit tests with `MetricCollector<T>` (`Microsoft.Extensions.Diagnostics.Testing` 8.10.0 in UnitTests); health integration tests. — verify: they fail.
3. Instrument the five call sites. — verify: unit filter.
4. Health check + OpenTelemetry wiring in `Program.cs`. — verify: full suite (`Health_ReturnsHealthy` must still pass).
5. Compose: `docker compose up --build` → open `http://127.0.0.1:18888`, create a link, redirect, and confirm `urlshortener.*` metrics appear. (Manual check; screenshot goes into the README in plan 015.)
6. Full suite → reviewers → close.

## Test strategy

| AC | Level | Test | Notes |
|----|-------|------|-------|
| OBS-AC-01..06 | unit | `UrlShortenerMetricsTests.*` + additions to existing service/cache/tracker tests | `MetricCollector<long>` on the named instruments; assert value and tags. |
| OBS-AC-07 | integration | existing suite | `ApiFactory` sets no OTLP endpoint, so every integration test proves the app runs without an exporter. Add an explicit assertion only if cheap. |
| OBS-AC-08/09 | integration | `HealthTests.Health_RedisReachable_ReturnsHealthy` (`[RedisFact]`, needs real Redis), `HealthTests.Health_RedisUnreachable_Returns200Degraded` (Redis pointed at a closed port, `127.0.0.1:1`) | Register the real Redis cache through `ConfigureTestServices`. |
| OBS-AC-10 | integration | `ApiEndToEndTests.Health_ReturnsHealthy` | Existing. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| OpenTelemetry 1.x may depend on `Microsoft.Extensions.*` 9.x on `net8.0`. | Supported combination; check `dotnet list package --include-transitive` and run the full suite. |
| Metric calls on the redirect path. | Counters are lock-free in-memory increments; no I/O (spec 009 behaviour 6). |
| Redis check makes `/health` slow when Redis hangs. | Client timeouts are ≤ 1 s (spec 004); the check returns Degraded on timeout. |
| Aspire dashboard runs without auth by default. | Bind the UI to `127.0.0.1` only; document. |

Rollback: remove the packages and the `Observability/` folder, revert `Program.cs`, compose and the five call sites.
