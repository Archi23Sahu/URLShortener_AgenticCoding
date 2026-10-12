# Scenario 2: Brownfield enhancement

**Existing system:** a working URL shortener (create, redirect, update, delete, basic analytics).

**New requirements:** add analytics, expiration, Redis, rate limiting.

That is the scenario the assessment describes. The baseline is built in [greenfield.md](greenfield.md); this page covers the later changes made to the running codebase with the method below: reporting, resilience, observability, a default-config change and a UTC fix.

```
Existing code → dependency analysis → impact analysis → implementation suggestions
             → code generation → test generation → engineer review
```

In this repository that loop is the gated workflow in [`docs/PLAN.md`](../PLAN.md): spec → plan → tests first → implement → full test run → code-quality and security review → close. Each brownfield change below, except the browser column, has a plan under `docs/plans/` where that analysis is written down.

## Worked examples from this repository

### Expiry timestamps lost their UTC marker ([plan 007](../plans/007-utc-expires-at.md))

| Step | What was found |
|------|----------------|
| **Dependency analysis** | `expiresAt` flows SQL `datetime2` → EF entity → `ShortUrlResponse` → JSON, and also into the Redis cache entry (`ResolvedLink`). |
| **Impact analysis** | EF returns `datetime2` as `DateTimeKind.Unspecified`; System.Text.Json writes that without an offset, so clients read it as local time. Only the 201 path was correct (it returns the in-memory UTC value). A patch in `ToResponse` would have fixed one symptom and left every other read path (`CreatedAt`, `ClickedAt`, the cached value) wrong. |
| **Change** | One EF value converter registered for **every** `DateTime` property (`UtcDateTimeConverter` in `AppDbContext.ConfigureConventions`). No schema change, no migration. |
| **Regression protection** | Three tests written first and shown to fail for the right reason, then green; the redirect, report and cache tests that touch the same columns all passed unchanged. |

### Click writer failure handling ([plan 011](../plans/011-polly-resilience.md))

| Step | What was found |
|------|----------------|
| **Dependency analysis** | The click writer (`ClickRecorderBackgroundService`) ran every insert through a fresh DI scope and, on any failure, retried each click individually. |
| **Impact analysis** | That fallback was designed for one case, a foreign-key violation for a deleted link. During a database outage it would make up to 100 doomed connection attempts per batch and stall the writer while the queue filled. |
| **Change** | Extracted `IClickEventWriter`; Polly retries transient failures as a whole batch and drops a persistently failing batch once; only non-transient failures fall back to row-by-row. Redirect reads got a time-budgeted retry. |
| **Regression protection** | The batch-survives-a-bad-click and shutdown-drain integration tests added earlier ([plan 008](../plans/008-click-pipeline-gaps.md)) stayed green and were the safety net for the refactor. |

### Observability added across five existing classes ([plan 012](../plans/012-observability.md))

Metrics had to be recorded at the controller, cache decorator, click tracker and click writer without changing their behaviour. Every constructor change was driven by tests: the existing unit tests were updated to build the new `UrlShortenerMetrics` dependency, and metric assertions use `MetricCollector` against real instruments.

### Rate limiting enabled by default ([plan 013](../plans/013-rate-limit-default-on.md))

A one-line configuration change with a wide blast radius. The impact analysis listed every context that sends traffic from one IP: integration tests opt out explicitly (`ApiFactory`), the load tester's header says to start the API with limiting off, and the compose stack now runs with limiting **on** and documents how to disable it. A unit test fails if the shipped configuration ever disables limiting.

### Earlier brownfield change: browser analytics

Adding browser statistics required storing the browser at click time. Migration `20261010022547_AddBrowserToClickEvents` adds a **nullable** column so existing rows stay valid; reports treat `NULL` as `Unknown`. See [spec 006](../specs/006-analytics-reports.md) and [`docs/ER-Diagram.md`](../ER-Diagram.md).

## Deliverables

| Deliverable | Where |
|-------------|-------|
| Updated architecture | [`docs/architecture.md`](../architecture.md) |
| Database migration scripts | `src/Migrations/` (EF migrations) and `docs/schema.generated.sql` (generated script) |
| Regression testing | The full unit and integration suite is run after every plan; results per plan are recorded in `docs/plans/` and `docs/PLAN.md`. The suite grew from 266 to over 320 tests with no regression introduced by any plan. |

## Lessons

- Fix the cause once at the boundary (a converter, a pipeline) rather than patching each symptom.
- Write the failing test first, and read the failure message: it proves the test actually detects the bug.
- A refactor of a hot background service is only as safe as the integration tests around it, so those were added *before* the refactor ([plan 008](../plans/008-click-pipeline-gaps.md)).
