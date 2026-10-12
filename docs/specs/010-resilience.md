# 010 — Resilience (Polly retry policies)

| | |
|---|---|
| Status | Implemented |
| AC prefix | `RSL` |
| Last verified | 2026-10-12 |
| Related specs | 002 (redirect), 005 (click tracking) |
| Plans | [011](../plans/011-polly-resilience.md) |
| Source | Requirement "Reliability & Production Features": *Retry policies using Polly* |

## Purpose

Survive short, transient SQL Server failures (failover, throttling, dropped connections, a container still starting) without failing requests or losing clicks, and without retrying errors that will never succeed.

## API contract

No endpoints. Pipelines are built with Polly v8 through `Microsoft.Extensions.Resilience`.

## Behaviour

1. **Transient classification** (`src/Resilience/SqlTransientErrorDetector.cs`): an exception is transient if it, or any inner exception, is a `TimeoutException`, a `SocketException` (SqlClient on Linux reports a refused connection this way), or a `SqlException` with an error number in the transient set: timeouts (`-2`), connection loss (`20, 64, 233, 10053, 10054, 10060`), server not found / connection refused (`2, 53, 10061, 11001`; `10061` confirmed empirically against a closed port), database not available yet (`4060, 4221`), throttling (`10928, 10929, 40501, 40540`), Azure SQL failover (`40143, 40197, 40613, 49918-49920`), deadlock victim (`1205`). Everything else (FK violations, validation, `NotFoundException`, concurrency conflicts) is **not** transient.
2. **`Startup` pipeline** (`SqlResilience.Startup`, used by `DatabaseInitializer`): retries *any* exception except cancellation while applying migrations; exponential backoff from `StartupBaseDelaySeconds`, capped at `StartupMaxDelaySeconds`; `StartupMaxAttempts` attempts in total, then logs Critical "giving up" and rethrows the last error (startup fails). Worst case with defaults: 2+4+8+16+5×30 = 180 s.
3. **`Transient` pipeline** (`SqlResilience.Transient`, background work): retries transient errors only, `SqlMaxRetries` times, exponential backoff from `SqlBaseDelayMs` with jitter. `SqlMaxRetries = 0` disables it.
4. **`RequestRead` pipeline** (`SqlResilience.RequestRead`, request path): the same retry **inside a total time budget** of `RequestTimeoutMs` (timeout is the outer strategy, so it bounds all attempts together). Exceeding it throws Polly's `TimeoutRejectedException` (→ 500). `UrlShortenerService.ResolveAsync` runs the cache-miss SQL read through it. Request-path writes (create/update/delete) are **not** retried: they use explicit transactions or `RowVersion`, and a blind retry could duplicate a row or mask a conflict.
5. **Click writer** (`ClickRecorderBackgroundService` + `IClickEventWriter`), which retries transient failures as a whole batch and falls back to row-by-row only for non-transient ones:
   - the batch insert runs through `Transient`;
   - if it still fails **transiently**, the batch is dropped with **one** error log (no per-row attempts against a database that is down);
   - if it fails **non-transiently** (e.g. FK violation for a deleted link), it falls back to row-by-row (CLK-AC-09 unchanged);
   - if a single-click insert in that fallback fails **transiently**, the database has gone away: the remaining clicks of the batch are dropped without further attempts (counted as `insert_failed`) so the writer is not stalled by one timeout per click.
6. Each retry is logged at Warning with the attempt number and delay, using message templates; no user input is logged.

## Edge cases and failure modes

- A batch whose commit succeeded but whose acknowledgement was lost could be inserted twice on retry; accepted for analytics (rare, bounded to one batch).
- Background retries never delay a redirect. On the redirect path, a cache miss during a database brownout is bounded by `RequestTimeoutMs` (3 s by default) in total, not by the 30 s SQL command timeout × attempts.

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `Resilience:StartupMaxAttempts` | `10` | Migration attempts at startup (total). |
| `Resilience:StartupBaseDelaySeconds` | `2` | First startup backoff delay (exponential). |
| `Resilience:StartupMaxDelaySeconds` | `30` | Cap on a single startup delay. |
| `Resilience:SqlMaxRetries` | `3` | Retries for transient SQL errors; `0` disables. |
| `Resilience:SqlBaseDelayMs` | `200` | First transient-retry delay (exponential, jittered). |
| `Resilience:RequestTimeoutMs` | `3000` | Total budget for a request-path read, retries included. |

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| RSL-AC-01 | Given a `TimeoutException`, a `SocketException` or a transient `SqlException` (directly or as inner exception), then it is classified transient; a FK `DbUpdateException`, `NotFoundException` or `ConcurrencyConflictException` is not. | `SqlTransientErrorDetectorTests.IsTransient_TimeoutException_ReturnsTrue`, `SqlTransientErrorDetectorTests.IsTransient_TimeoutNestedInDbUpdateException_ReturnsTrue`, `SqlTransientErrorDetectorTests.IsTransient_DbUpdateExceptionWithNonTimeoutInner_ReturnsFalse`, `SqlTransientErrorDetectorTests.IsTransient_NotFoundException_ReturnsFalse`, `SqlTransientErrorDetectorTests.IsTransient_LinkExpiredException_ReturnsFalse`, `SqlTransientErrorDetectorTests.IsTransient_ConcurrencyConflictException_ReturnsFalse`, `SqlTransientErrorDetectorTests.IsTransient_InvalidOperationException_ReturnsFalse`, `SqlTransientErrorDetectorTests.IsTransient_Null_ReturnsFalse`; `SocketException`: `SqlTransientErrorDetectorTests.IsTransient_SocketException_ReturnsTrue`, `SqlTransientErrorDetectorTests.IsTransient_SocketExceptionNestedInDbUpdateException_ReturnsTrue`. Transient `SqlException` numbers are not unit-testable (no public constructor); `10061` was verified with a probe against a closed port. |
| RSL-AC-02 | Given the SQL read fails transiently once on a cache miss, when resolving, then the redirect still succeeds and the read was attempted twice. | `UrlShortenerServiceTests.ResolveAsync_TransientReadFailure_RetriesAndSucceeds`, `SqlResilienceTests.Transient_TimeoutEveryAttempt_RetriesSqlMaxRetriesThenThrows`, `SqlResilienceTests.Transient_SqlMaxRetriesZero_DoesNotRetry` |
| RSL-AC-03 | Given a non-transient error on the redirect read, then it is not retried (one attempt). | `UrlShortenerServiceTests.ResolveAsync_NonTransientFailure_IsNotRetried`, `SqlResilienceTests.Transient_NonTransientError_IsNotRetried` |
| RSL-AC-04 | Given a click batch insert fails transiently once, then the whole batch is stored on retry, with no row-by-row fallback. | `ClickRecorderBackgroundServiceTests.Flush_TransientFailureOnce_RetriesWholeBatchWithoutRowByRow` |
| RSL-AC-05 | Given a click batch keeps failing transiently, then after `SqlMaxRetries` retries the batch is dropped without per-row inserts. | `ClickRecorderBackgroundServiceTests.Flush_TransientFailureEveryAttempt_DropsBatchWithoutRowByRow` |
| RSL-AC-06 | Given a click batch fails non-transiently (FK), then it falls back to row-by-row (CLK-AC-09 behaviour). | `ClickPipelineTests.FlushBatch_ClickForMissingLink_OtherClicksAreStored`, `ClickRecorderBackgroundServiceTests.Flush_NonTransientBatchFailure_FallsBackToRowByRow` |
| RSL-AC-07 | Given migration fails N−1 times then succeeds, then startup completes; given it fails `StartupMaxAttempts` times, then startup fails with the last error. | `SqlResilienceTests.Startup_FailsUntilLastAttempt_Completes`, `SqlResilienceTests.Startup_FailsEveryAttempt_ThrowsLastErrorAfterMaxAttempts` |
| RSL-AC-08 | Given a request-path read whose attempts keep hanging or failing transiently, then `RequestRead` gives up within `RequestTimeoutMs` (plus a small margin) with `TimeoutRejectedException`, regardless of how many retries remain. | `SqlResilienceTests.RequestRead_HangingOperation_GivesUpWithinBudget`, `SqlResilienceTests.RequestRead_TransientFailuresBeyondBudget_GivesUpWithinBudget`, `SqlResilienceTests.RequestRead_TransientFailureOnce_Succeeds` |
| RSL-AC-09 | Given the database becomes unavailable during the row-by-row fallback, then the remaining clicks of the batch are dropped without further attempts. | `ClickRecorderBackgroundServiceTests.Flush_TransientFailureDuringRowByRow_StopsAndDropsTheRemainingClicks`, `ClickRecorderBackgroundServiceTests.Flush_TransientFailureAfterSomeRowsWereStored_DropsOnlyTheRemainder` |

## Known limitations

- No circuit breaker on SQL: during a long outage every redirect miss still spends up to `RequestTimeoutMs` before failing. The cache (spec 004) absorbs most redirect traffic.
- The Redis cache keeps its own lightweight breaker (`CacheHealth`, spec 004); it is not rebuilt on Polly.
