---
name: run-tests
description: How to run this repo's xUnit tests correctly — prerequisites (SQL Server for integration tests), whole suite, one project, one class or method, coverage, and how to read common failures. Use when asked to run, re-run or debug tests, check coverage, or confirm a change didn't break anything ("run the tests", "is it green?", "run the cache tests", "coverage"). The test-runner subagent follows this skill; prefer delegating to it for full-suite runs. Do NOT use for writing tests (test-author) or for load testing (tests/PerformanceTests).
---

# Run tests

| Project | Needs | Typical time |
|---------|-------|--------------|
| `tests/UnitTests` | nothing | seconds |
| `tests/IntegrationTests` | SQL Server on `localhost,1433` (or `URLSHORTENER_TEST_DB`) | tens of seconds |

## 1. Prerequisite: SQL Server (integration tests only)

```bash
docker ps --filter "publish=1433"      # any SQL Server on 1433? (may be a container outside this compose project — use it)
docker compose up -d sqlserver         # only if nothing holds 1433; first start takes ~30 s to become healthy
docker compose ps sqlserver            # wait for "healthy"
```

`docker compose up` failing with `port is already allocated` means another SQL Server already owns 1433 — the tests can use it as-is.

Integration tests use their own database `UrlShortener_IntegrationTests` (created/migrated automatically by `ApiFactory`) — they never touch dev data. Redis is **not** needed: tests swap in an in-memory cache.

## 2. Commands (run from repo root)

```bash
dotnet test tests/UnitTests                                     # fast loop
dotnet test tests/IntegrationTests
dotnet test                                                     # whole solution (needs .NET SDK 10 for .slnx)
dotnet test --filter "FullyQualifiedName~CachedUrlShortenerServiceTests"            # one class
dotnet test --filter "FullyQualifiedName~ReportServiceTests.Daily_FillsMissingDaysWithZero"  # one test
dotnet test -c Release --collect:"XPlat Code Coverage" --results-directory TestResults        # what CI runs
```

Add `--logger "console;verbosity=normal"` to see each test name; `--no-build` after a successful `dotnet build` to save time.

## 3. Reading failures

| Symptom | Likely cause → action |
|---------|----------------------|
| `Could not prepare the integration-test database. Is SQL Server running?` | SQL Server down or not healthy yet → step 1. |
| Build error copying `UrlShortener.dll` / file in use | A running API instance (`dotnet run`, VS debugger) locks the output → stop it. |
| Test using `EventuallyAsync` times out | Click writer didn't flush within 15 s — check the background service logs; real failure, not flakiness, unless the machine is heavily loaded. |
| Unexpected 429 in an integration test | Test class used a rate-limited factory (`LimitedApiFactory`) or overrode `RateLimit:Enabled`. Normal tests use `ApiFactory`, which disables limiting. |
| Duplicate-detection test returns 200 instead of 201 | Test reused a URL — use `TestHelpers.UniqueUrl()`. |

## Verify / report

Report: command run, totals (passed / failed / skipped) copied from the output, and for each failure the test name plus the first assertion message. Never describe a run as passing without the actual totals, and say explicitly if integration tests were skipped because SQL Server was unavailable.
