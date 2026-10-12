# Plan 009 — Prove deletes remove click history

| | |
|---|---|
| Status | Implemented |
| Spec | [003 — Update and delete](../specs/003-update-delete-link.md), [008 — Expired link cleanup](../specs/008-expired-link-cleanup.md) |
| ACs satisfied / added | `DEL-AC-03`, `CLN-AC-08` — acceptance criteria that previously had no test |

## Goal

Prove against real SQL Server what the specs claim and today's tests only infer:
- **DEL-AC-03:** `DELETE /api/v1/shorten/{code}` removes the link's `ClickEvents` rows. This relies on the database cascade.
- **CLN-AC-08:** a cleanup pass deletes exactly the links with `ExpiresAt < now − GracePeriodDays`, and their clicks with them. It must not delete links still inside the grace period or still active.

## Out of scope

- CLN-AC-06, 07 and 09 (timer resilience, the disabled switch, the 410-within-grace interaction), which are P2/P3.
- Any change to cleanup or delete behaviour.

## Changes

| File | Change | Why |
|------|--------|-----|
| `tests/IntegrationTests/DeletionTests.cs` *(new)* | `DeletionTests : IClassFixture<ApiFactory>` with both tests and a private helper to insert `ClickEvent` rows directly. | Cascades and the `ExpiresAt < cutoff` query only exist in SQL. |
| `docs/specs/003-update-delete-link.md`, `docs/specs/008-expired-link-cleanup.md` | Replace **GAP** with test names. | Traceability. |

## Steps

1. **DEL-AC-03.** `CreateAsync` a link. Generate clicks with `VisitAsync` ×2, then `EventuallyAsync` until 2 `ClickEvents` rows exist for its id (read through `AppDbContext`). `DELETE` it, expect 204, then count `ClickEvents` with that `ShortUrlId`: expect 0. — verify: `dotnet test --filter "FullyQualifiedName~DeletionTests"`
2. **CLN-AC-08.** Use `InsertLinkAsync` to insert three links with `ExpiresAt` = now − 8 days (past the 7-day grace), now − 6 days (inside grace), and now + 1 day (active). Insert one `ClickEvent` for each directly. Resolve `ExpiredLinkCleaner` from a new DI scope and call `RunOnceAsync`. Assert:
   - the 8-day link and its click are gone;
   - the 6-day link, the active link and their clicks remain.

   Assert on these ids only; the return value can include other tests' old rows. — verify: same filter.
3. **Full suite.** — verify: test-runner, full suite.
4. **Review** with both reviewers, then update the specs' Tests columns and run `check-specs.sh`.

## Test strategy

| AC | Level | Test (Class.Method) | Notes |
|----|-------|---------------------|-------|
| DEL-AC-03 | integration | `DeletionTests.Delete_LinkWithClicks_RemovesItsClickEvents` | Real clicks through the redirect, so it also covers clicks arriving through the pipeline. |
| CLN-AC-08 | integration | `DeletionTests.Cleanup_DeletesOnlyLinksPastGraceAndTheirClicks` | `Cleanup:Enabled=false` in `ApiFactory` only stops the *timer*. The scoped `ExpiredLinkCleaner` can still be called directly. `GracePeriodDays` keeps its default of 7. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| **The shared test DB is persistent across runs and shared by test classes running in parallel.** `RunOnceAsync` deletes *every* link more than 7 days past expiry, not only the test's own. | Other tests insert expired links at now − 5 min, so they're never touched during a run. Rows left over from older runs that are past the grace may be deleted, which is harmless; no test depends on old rows. Assertions use only the test's own ids, never the count `RunOnceAsync` returns. |
| **DEL-AC-03 timing:** a click still queued when the link is deleted would hit the FK. | Wait (`EventuallyAsync`) until both clicks are stored *before* deleting. |
| **`InsertLinkAsync` uses a fixed `CreatedAt`** (now − 40 days). | It doesn't matter for these assertions. |

Rollback: delete `DeletionTests.cs`.
