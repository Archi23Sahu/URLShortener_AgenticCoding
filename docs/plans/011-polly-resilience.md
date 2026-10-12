# Plan 011 — Polly retry policies

| | |
|---|---|
| Status | Implemented |
| Spec | [010 — Resilience](../specs/010-resilience.md), [005 — Click tracking](../specs/005-click-tracking.md) |
| ACs satisfied / added | `RSL-AC-01..07` (new); regression guard `CLK-AC-09`, `CLK-AC-10`, `RED-AC-01` |
| Source | Requirement "Reliability & Production Features": retry policies using Polly. |

## Goal

Add Polly v8 retry policies where they're safe (idempotent reads, background inserts, startup migration), and stop the click writer from making up to 100 doomed row-by-row inserts while SQL is down.

## Out of scope

- Retrying create/update/delete on the request path (see spec 010 behaviour 4).
- Replacing the Redis breaker (`CacheHealth`) with Polly.

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/UrlShortener.csproj` | Add `Microsoft.Extensions.Resilience` **8.10.0** (Polly v8, targets .NET 8). | Requirement: Polly. |
| `src/Configurations/ResilienceOptions.cs` *(new)* | `StartupMaxAttempts`, `StartupBaseDelaySeconds`, `SqlMaxRetries`, `SqlBaseDelayMs`. | Configurable, testable with zero delays. |
| `src/Resilience/SqlTransientErrorDetector.cs` *(new)* | Static `IsTransient(Exception)` walking inner exceptions: `SqlException` numbers in a documented transient set, or `TimeoutException`. | Single, unit-testable classification (RSL-AC-01). |
| `src/Resilience/SqlResilience.cs` *(new)* | Singleton exposing `ResiliencePipeline Startup` and `ResiliencePipeline Transient`, built from `ResilienceOptions` + `ILogger` (logs each retry). | One place that owns the pipelines; constructor-injectable into services and tests. |
| `src/Repositories/DatabaseInitializer.cs` | Replace the hand-written loop with `SqlResilience.Startup`. | RSL-AC-07; replaces the hand-written retry loop. |
| `src/Services/UrlShortenerService.cs` | `ResolveAsync`: wrap the `GetByCodeAsync` call in `SqlResilience.Transient`. | RSL-AC-02/03. |
| `src/Analytics/ClickEventWriter.cs` *(new)* + `src/BackgroundServices/ClickRecorderBackgroundService.cs` | Move the scope-per-insert logic into `IClickEventWriter`; the service runs batch inserts through `Transient`, drops a persistently-transient batch with one log, and falls back to row-by-row only for non-transient failures. | RSL-AC-04..06; testable without SQL; removes the per-row retry storm during an outage. |
| `src/Program.cs`, `src/appsettings.json` | Register options, `SqlResilience`, `IClickEventWriter`; add the `Resilience` section with defaults. | Wiring. |
| `tests/UnitTests/...` | New tests (see Test strategy); update `UrlShortenerServiceTests` constructor calls for the new dependency. | — |

## Steps

1. Package + options + detector + `SqlResilience`. — verify: `dotnet build`, `dotnet list src package --vulnerable` clean.
2. **Tests first (test-author)** for RSL-AC-01..07 against the new types; tests for 02–05 and 07 fail until steps 3–5. — verify: test-runner filter.
3. `DatabaseInitializer` on `Startup` pipeline. — verify: `dotnet build`; compose stack starts (`docker compose up --build`, API logs "Database migrations applied").
4. `UrlShortenerService.ResolveAsync` on `Transient`. — verify: unit filter `UrlShortenerServiceTests`.
5. `IClickEventWriter` extraction + new failure handling. — verify: `ClickPipelineTests`, `ClickShutdownTests` still pass; new unit tests pass.
6. Full suite → both reviewers → close.

## Test strategy

| AC | Level | Test | Notes |
|----|-------|------|-------|
| RSL-AC-01 | unit | `SqlTransientErrorDetectorTests.*` | `TimeoutException`, nested transient, FK `DbUpdateException`, domain exceptions. Real `SqlException` instances can't be constructed publicly; the number list is covered by code review and documented in the class. |
| RSL-AC-02/03 | unit | `UrlShortenerServiceTests.ResolveAsync_TransientReadFailure_RetriesAndSucceeds`, `…_NonTransientFailure_IsNotRetried` | Mock repo throws `TimeoutException` once; zero-delay options. |
| RSL-AC-04/05/06 | unit | `ClickRecorderBackgroundServiceTests.*` | Mock `IClickEventWriter`; drive the service via the real `ChannelClickQueue` and `StartAsync`/`StopAsync`. |
| RSL-AC-06 | integration | existing `ClickPipelineTests.FlushBatch_ClickForMissingLink_OtherClicksAreStored` | Regression. |
| RSL-AC-07 | unit | `DatabaseInitializerTests` or `SqlResilienceTests.Startup_*` | Fake operation failing N−1 times; and failing `StartupMaxAttempts` times. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| Retry on the redirect path adds latency during an outage. | Only transient errors; cache hits are unaffected; request-path reads are bounded by a 3 s total budget (`Resilience:RequestTimeoutMs`, spec 010). Configurable. |
| Duplicate click rows if a commit succeeded but the ack was lost. | Accepted and documented (spec 010); analytics only. |
| `Microsoft.Extensions.Resilience` pulls in newer `Microsoft.Extensions.*` packages. | It's the .NET 8 line (8.10.0); check `dotnet list package --include-transitive` for conflicts at step 1. |
| Extracting `IClickEventWriter` changes a hot background service. | The integration tests from plan 008 (batch, shutdown) are the regression net. |

Rollback: revert the listed `src/` files and the package reference; no schema or data changes.
