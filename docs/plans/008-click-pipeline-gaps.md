# Plan 008 — Cover the click pipeline's failure paths

| | |
|---|---|
| Status | Implemented |
| Spec | [005 — Click tracking](../specs/005-click-tracking.md), [002 — Redirect](../specs/002-redirect.md) |
| ACs satisfied / added | `CLK-AC-09`, `CLK-AC-10`, `RED-AC-08` (negative half) — acceptance criteria that previously had no test |

## Goal

Prove three click-pipeline guarantees that exist in code but no test checks:
- **CLK-AC-09:** a click for a deleted link doesn't discard the other clicks in its batch.
- **CLK-AC-10:** clicks still queued at a graceful shutdown are written before the app stops.
- **RED-AC-08:** a 404 or 410 redirect queues no click.

## Out of scope

- Any production code change. If a test shows the code doesn't behave as the spec says, stop and report. It becomes a bug fix with its own plan.
- CLK-AC-08 (batching by size and by time) and CLK-AC-11 (unique visitors), which are P2.
- Changing how the click writer retries failures (specified separately in spec 010).

## Changes

| File | Change | Why |
|------|--------|-----|
| `tests/IntegrationTests/ClickPipelineTests.cs` *(new)* | `ClickPipelineTests : IClassFixture<ApiFactory>` with the CLK-AC-09 test; `ClickShutdownTests` with the CLK-AC-10 test, which builds its own factory per test because it must dispose it. | The click writer is a hosted service, so these behaviours only show up with the real host and real SQL. No existing class covers the pipeline directly. |
| `tests/UnitTests/Controllers/RedirectControllerTests.cs` *(new)* | RED-AC-08 negative case. | Proving "no click was queued" through HTTP would need a fixed wait. A unit test with a mocked queue is exact. |
| `docs/specs/005-click-tracking.md`, `docs/specs/002-redirect.md` | Replace **GAP** with test names; *Last verified* = close date. | Traceability. |

## Steps

1. **CLK-AC-09: a bad click doesn't sink its batch.** Resolve the singleton `IClickQueue` from `factory.Services`. Enqueue, back to back, a valid click for a link created with `CreateAsync`, a click whose `ShortUrlId` doesn't exist (e.g. `-1`, which forces an FK violation), and a second valid click. All three should land in one flush window. `EventuallyAsync` until the valid link has 2 `ClickEvents` rows. — verify: `dotnet test --filter "FullyQualifiedName~ClickPipelineTests"`
2. **CLK-AC-10: graceful shutdown drains the queue.** Create `new FlushOnShutdownFactory()`, an `ApiFactory` subclass with overrides `Analytics:FlushIntervalSeconds=300` and `Analytics:BatchSize=1000`, so nothing is written on a timer during the test. Create a link and enqueue N=5 clicks directly. **Dispose the factory**, which stops the host and runs the shutdown path in `ClickRecorderBackgroundService.cs:69-86`. Then count rows with a fresh `AppDbContext` on `ApiFactory.ConnectionString`: expect 5. — verify: `dotnet test --filter "FullyQualifiedName~ClickShutdownTests"`
3. **RED-AC-08 negative: 404 and 410 queue nothing.** Build `RedirectController` with `Mock<IUrlShortenerService>`, where `ResolveAsync` throws `NotFoundException` (one case) or `LinkExpiredException` (the other). Use a real `ClickTracker` over `Mock<IClickQueue>` and a `DefaultHttpContext`. Assert the exception propagates and `TryEnqueue` is never called (`Times.Never`). — verify: `dotnet test tests/UnitTests --filter "FullyQualifiedName~RedirectControllerTests"`
4. **Full suite.** — verify: test-runner, full suite, expecting 269 + new tests with no regressions.
5. **Review** with code-quality-reviewer and security-reviewer in parallel, then update the specs' Tests columns; `check-specs.sh` exits 0.

## Test strategy

| AC | Level | Test (Class.Method) | Notes |
|----|-------|---------------------|-------|
| CLK-AC-09 | integration | `ClickPipelineTests.FlushBatch_ClickForMissingLink_OtherClicksAreStored` | Enqueue directly. Going through HTTP can't produce a click for a link that doesn't exist. `ApiFactory` already sets `FlushIntervalSeconds=1`, so the 3 clicks share one window. |
| CLK-AC-10 | integration | `ClickShutdownTests.Shutdown_QueuedClicks_AreWrittenBeforeStop` | Its own factory instance, disposed inside the test. Assert through a new `AppDbContext`, because the factory's services are gone after dispose. |
| RED-AC-08 (404) | unit | `RedirectControllerTests.Redirect_UnknownCode_QueuesNoClick` | `Times.Never` on `IClickQueue.TryEnqueue`. |
| RED-AC-08 (410) | unit | `RedirectControllerTests.Redirect_ExpiredLink_QueuesNoClick` | Same. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| **CLK-AC-09 is timing-sensitive.** If the clicks split across two flush windows, the test still passes, because the valid clicks are written either way, but it no longer proves the "batch survives" path. | Enqueue all three synchronously before yielding. The log line "retrying individually" shows the batch path ran; the final report should say whether it appeared. Optionally use a dedicated factory with a larger `FlushIntervalSeconds` so the batch is filled long before the window closes. |
| **CLK-AC-10 depends on the host shutdown timeout.** The .NET 8 default is 30 s; this repo doesn't override `HostOptions`. | 5 clicks flush in milliseconds. If the test is ever slow, the cause is SQL, not the timeout. |
| **CLK-AC-10's first click can start a batch** before the shutdown signal arrives. | Code check: `batch` is declared outside the `try` (`ClickRecorderBackgroundService.cs:40`), so clicks already collected are still flushed at `:86`. This is exactly what the test should prove. |
| **Shared integration DB.** | Clicks reference links created by the test itself, and assertions filter by those link ids. |
| **The 404/410 unit test only tests the controller.** | The positive path (one click on a 302) is already covered end-to-end by `FullLifecycle`. Together they cover RED-AC-08. |

Rollback: delete the new test files. No production code changes.
