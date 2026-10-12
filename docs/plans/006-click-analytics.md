# Plan 006 — Click analytics (greenfield, Task 6)

| | |
|---|---|
| Status | Implemented |
| Spec | [005 — Click tracking](../specs/005-click-tracking.md), [006 — Analytics reports](../specs/006-analytics-reports.md) |
| ACs satisfied / added | `CLK-AC-*`, `RPT-AC-*` |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 6 |

## Goal

Record every redirect as a click and report totals, daily counts, countries and browsers, without adding database write latency to a redirect.

## Options considered

| Option | Verdict |
|--------|---------|
| Write the click inside the redirect request | Redirect latency tied to SQL write load |
| Fire-and-forget task | Unbounded, no batching, lost on shutdown |
| Message broker | Extra infrastructure for one consumer |
| **Bounded in-process queue + batching background writer** | **Chosen** |

Accepted cost: eventual consistency (about two seconds) and at-most-once delivery (a crash loses queued clicks).

## Out of scope

Failure-path test coverage ([plan 008](008-click-pipeline-gaps.md)) and the writer's retry policy ([plan 011](011-polly-resilience.md)). Country and browser reports were added with spec 006; the `Browser` column came with its own migration (see [brownfield](../scenarios/brownfield.md)).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Analytics/ClickTracker.cs` | No I/O, never throws; hashes IP + UA + salt, resolves country from a header, detects browser | Redirect path stays fast and safe; no raw IP stored |
| `src/Analytics/ClickQueue.cs` | Bounded channel; drops when full | A spike cannot grow memory |
| `src/BackgroundServices/ClickRecorderBackgroundService.cs` | Batch insert, new scope per batch; row-by-row fallback; drain on shutdown | One bad click (deleted link) cannot discard its neighbours |
| `src/Services/ReportService.cs`, `src/Controllers/AnalyticsController.cs` | Daily / countries / browsers reports straight from the repository | Reporting stays off the cached redirect path |

## Steps

1. Tracker, hasher, resolvers — verify: `dotnet test --filter "FullyQualifiedName~AnalyticsTests"`.
2. Queue and background writer — verify: `dotnet test --filter "FullyQualifiedName~ClickRecorderBackgroundServiceTests"`.
3. Reports — verify: `dotnet test --filter "FullyQualifiedName~ReportServiceTests"`.
4. End to end — verify: `dotnet test --filter "FullyQualifiedName~ClickPipelineTests"`.

## Test strategy

Owned by specs 005 and 006. Key integration tests: `ClickPipelineTests` (batch survives a bad click; queued clicks are written on shutdown).

## Risks and rollback

- Clicks can be lost on a crash → documented limitation.
- Writer is a singleton: it creates a DI scope per batch.

## Docs to update

- [x] Specs 005 and 006
- [x] README Trade-offs (async analytics)

## Approval

- [ ] Plan approved by: ______ on ______
