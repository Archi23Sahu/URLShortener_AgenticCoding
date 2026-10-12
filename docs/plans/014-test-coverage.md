# Plan 014 — Redis integration tests and database load tests

| | |
|---|---|
| Status | Implemented |
| Spec | [004 — Redirect cache](../specs/004-redirect-cache.md) |
| ACs satisfied / added | `CCH-AC-17` (existing GAP), `CCH-AC-18` (new) |
| Source | Requirement "Testing Strategy": integration tests against Redis; database load tests. |

## Goal

Test the cache against a **real Redis**, and extend the load test from redirect-only to database writes and a mixed workload, as the requirement's Testing Strategy asks. Also tidy the test projects.

## Changes

| File | Change | Why |
|------|--------|-----|
| `tests/IntegrationTests/RedisFactAttribute.cs` *(new)* | `[RedisFact]`: skips with a clear reason unless `URLSHORTENER_TEST_REDIS` is set (e.g. `localhost:6379`). | Redis is optional locally; required in CI. |
| `tests/IntegrationTests/RedisApiFactory.cs` *(new)* | `ApiFactory` subclass that, in `ConfigureTestServices`, swaps the in-memory cache for `AddStackExchangeRedisCache` (same options as `Program.cs`, separate `InstanceName` prefix per run). | Real Redis without touching process-wide env vars. |
| `tests/IntegrationTests/RedisCacheTests.cs` *(new)* | CCH-AC-18 (key written on redirect, removed on update and delete) and CCH-AC-17 (Redis pointed at `127.0.0.1:1`: redirect still 302 from SQL, within ~2 s). | Cache tested against a real Redis. |
| `.github/workflows/ci.yml` | Add a `redis:7-alpine` service and `URLSHORTENER_TEST_REDIS=localhost:6379` on the test step. | Redis tests run in CI. |
| `tests/PerformanceTests/Program.cs` | 4th argument `mode`: `redirect` (default, current behaviour), `create` (unique-URL POSTs: DB write load), `mixed` (creates, redirects and analytics reads). Reports p50/p95/p99 and throughput per operation, and warns on 429s. Existing usage keeps working. | Database load tests. |
| `UrlShortener.slnx` | Add `tests/PerformanceTests/PerformanceTests.csproj`. | It is currently outside the solution (housekeeping). |
| `tests/IntegrationTests/UnitTest1.cs` | Delete (empty placeholder). | Housekeeping. |

## Steps

1. **Tests (test-author):** `RedisFactAttribute`, `RedisApiFactory`, `RedisCacheTests`. Code exists, so they should pass when `URLSHORTENER_TEST_REDIS` is set and be skipped otherwise. — verify: test-runner twice, with and without the variable (your `urlshortener-redis` container is on 6379).
2. CI service + env. — verify: YAML review (CI can't run locally).
3. PerformanceTests modes; add the project to `.slnx`; delete `UnitTest1.cs`. — verify: `dotnet build UrlShortener.slnx`; run each mode once against a local API with `RateLimit__Enabled=false`, with small numbers (e.g. 200 requests) to check it works. These are not benchmarks.
4. Full suite → reviewers → close.

## Test strategy

| AC | Level | Test | Notes |
|----|-------|------|-------|
| CCH-AC-18 | integration (Redis) | `RedisCacheTests.Redirect_PopulatesRedis_UpdateAndDeleteRemoveKey` | Read the key through the factory's `IDistributedCache` (or `IConnectionMultiplexer`) to avoid prefix guessing. |
| CCH-AC-17 | integration | `RedisCacheTests.RedisUnreachable_RedirectStillServedFromSql` | Doesn't need a running Redis, so use `[Fact]`, not `[RedisFact]`. Also exercises OBS-AC-09 if plan 012 is done first. |
| Database load | manual | `dotnet run --project tests/PerformanceTests -c Release -- <url> 200 50 create` (and `mixed`) | Smoke run only; real numbers are for the demo. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| Redis tests leave keys behind. | Unique `InstanceName` per run plus a short TTL (10 min max anyway). |
| A developer without Redis sees skipped tests. | The skip reason names the env var; the README documents it. |
| CCH-AC-17's timing assertion is flaky on slow machines. | Generous bound (≤ 5 s) and assert on the 302 first; the point is "doesn't hang or 500". |

Rollback: delete the new files and revert CI, `.slnx` and PerformanceTests.
