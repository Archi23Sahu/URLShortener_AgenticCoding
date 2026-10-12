# 008 — Expired link cleanup

| | |
|---|---|
| Status | Implemented |
| AC prefix | `CLN` |
| Last verified | 2026-10-12 |
| Related specs | 002 (410 vs 404), 004 (no cache eviction needed), 006 (history is erased) |
| Plans | [009](../plans/009-delete-cascade-gaps.md) |

## Purpose

Periodically deletes links that expired more than a grace period ago, so the table and its click history don't grow forever, while still answering 410 (not 404) for recently expired links.

## API contract

No endpoints. `ExpiredLinkCleanupService` (hosted, timer) runs `ExpiredLinkCleaner.RunOnceAsync` (scoped, unit-testable) (`Program.cs`).

## Behaviour

**Timer** (`src/BackgroundServices/ExpiredLinkCleanupService.cs`)
1. `Cleanup:Enabled = false` → logs "disabled" and exits; no passes run.
2. Waits 30 s after start (lets migrations finish), runs one pass immediately, then every `IntervalMinutes` (min 1) via `PeriodicTimer`.
3. Each pass gets its own DI scope (DbContext is scoped). A failed pass is logged at Error and retried at the next tick; host shutdown stops the loop cleanly.

**Pass** (`src/Services/ExpiredLinkCleaner.cs`)
4. `cutoff = now − GracePeriodDays` (min 0 days).
5. Repeatedly calls `DeleteExpiredAsync(cutoff, BatchSize)` until a batch deletes fewer than `BatchSize` rows. Cancellation is checked before each batch.
6. A batch selects up to `BatchSize` Ids with `ExpiresAt < cutoff` (strict) ordered by `ExpiresAt` then `Id` (oldest first, in the order of `IX_ShortUrls_ExpiresAt`), then `ExecuteDelete`s exactly those rows (`ShortUrlRepository.cs`). `ClickEvents` cascade.
7. No cache eviction: a cached entry's TTL never exceeds the link's remaining lifetime, so it is already gone (`ExpiredLinkCleaner.cs`).

**Visible effect**: between `ExpiresAt` and `ExpiresAt + GracePeriodDays` the link answers 410; after the pass that deletes it, 404.

## Edge cases and failure modes

- Multiple app instances each run cleanup; deletes are idempotent, just redundant.
- Exact multiple of `BatchSize` expired rows → one extra empty batch, then stop.
- Integration tests disable cleanup (`ApiFactory.cs`) so it never races test data.

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `Cleanup:Enabled` | `true` | Master switch. |
| `Cleanup:IntervalMinutes` | `60` (min 1) | Time between passes. |
| `Cleanup:GracePeriodDays` | `7` (min 0) | How long an expired link keeps answering 410 (and keeps its analytics). |
| `Cleanup:BatchSize` | `1000` (min 1) | Links per delete statement. Their click rows cascade in the same statement, so a batch is bounded by links, not clicks. |

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| CLN-AC-01 | Given nothing past the cutoff, then a pass deletes nothing. | `ExpiredLinkCleanerTests.RunOnce_NothingExpired_DeletesNothing` |
| CLN-AC-02 | The cutoff is `now − GracePeriodDays`; with 0 grace it is `now`. | `ExpiredLinkCleanerTests.RunOnce_CutoffIsNowMinusGracePeriod`, `ExpiredLinkCleanerTests.RunOnce_ZeroGrace_UsesNowAsTheCutoff` |
| CLN-AC-03 | Full batches repeat until a short batch; an exact multiple stops after one empty batch. | `ExpiredLinkCleanerTests.RunOnce_FullBatches_KeepGoingUntilAShortBatch`, `ExpiredLinkCleanerTests.RunOnce_ExactMultipleOfBatch_StopsAfterAnEmptyBatch` |
| CLN-AC-04 | Given an already-cancelled token, then the pass throws before touching the repository. | `ExpiredLinkCleanerTests.RunOnce_AlreadyCancelled_ThrowsBeforeTouchingTheDatabase` |
| CLN-AC-05 | Given the repository throws, then the pass propagates (so the timer can log it). | `ExpiredLinkCleanerTests.RunOnce_RepositoryFails_PropagatesSoTheTimerCanLogIt` |
| CLN-AC-06 | Given a pass throws, then the service logs and runs again at the next interval. | `ExpiredLinkCleanupServiceTests.Run_PassThrows_ServiceKeepsRunningAndRunsAgainAtTheNextInterval` |
| CLN-AC-07 | Given `Enabled = false`, then no pass runs. | `ExpiredLinkCleanupServiceTests.Run_Disabled_NeverTouchesTheRepositoryAndTheTaskCompletes` |
| CLN-AC-08 | Against real SQL: only rows with `ExpiresAt < cutoff` are deleted, and their `ClickEvents` go with them. | `DeletionTests.Cleanup_DeletesOnlyLinksPastGraceAndTheirClicks` |
| CLN-AC-09 | Given a link expired less than `GracePeriodDays` ago, then after a pass it still answers 410. | `DeletionTests.Cleanup_LinkExpiredInsideGracePeriod_StillAnswers410` |
| CLN-AC-10 | Given more expired links than the batch size, when a batch is deleted, then the oldest expired links go first. | `DeletionTests.DeleteExpired_MoreExpiredThanBatchSize_DeletesOldestFirst` |

## Known limitations

- Cleanup permanently erases click history for deleted links. Longer grace = longer analytics retention.
- The 30 s startup delay is fixed (not configurable).
