# Plan 005 — Redis redirect cache (greenfield, Task 5)

| | |
|---|---|
| Status | Implemented |
| Spec | [004 — Redirect cache](../specs/004-redirect-cache.md), [002 — Redirect](../specs/002-redirect.md) |
| ACs satisfied / added | `CCH-AC-*` |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 5 |

## Goal

Serve redirects from Redis without SQL on a hit, and never fail a request because Redis is down.

## Out of scope

Cache for reports (reporting deliberately bypasses the cache); Redis integration tests and the cache-health check came later ([plan 014](014-test-coverage.md), [plan 012](012-observability.md)).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Caching/CachedUrlShortenerService.cs` | Cache-aside decorator over `UrlShortenerService` | Controllers and the core service stay unaware of caching |
| `src/Caching/CacheHealth.cs` | Circuit breaker, skips Redis for 30 s after failures | A dead cache must not slow every redirect |
| `src/Program.cs` | `IUrlShortenerService` resolves to the decorator; in-memory distributed cache when no Redis string | Local runs and tests need no Redis |
| `src/Configurations/CacheOptions.cs` | `Cache:TtlMinutes` | Tunable TTL |

## Rules

- Only `ResolveAsync` reads the cache; `UpdateAsync` and `DeleteAsync` invalidate **after** the database change succeeds.
- TTL = `min(Cache:TtlMinutes, remaining lifetime)`; 404 and 410 are never cached.
- Any Redis error is logged, recorded in `CacheHealth`, and falls back to SQL.

## Steps

1. Decorator and options — verify: `dotnet test --filter "FullyQualifiedName~CachedUrlShortenerServiceTests"`.
2. Circuit breaker (breaker and failure fall-back cases are in the same test class) — verify: `dotnet test --filter "FullyQualifiedName~CachedUrlShortenerServiceTests"`.
3. Wire in `Program.cs` — verify: `dotnet test --filter "FullyQualifiedName~ApiEndToEndTests"`.

## Test strategy

Acceptance criteria and tests are owned by [spec 004](../specs/004-redirect-cache.md): unit tests with a fake clock, and `RedisCacheTests` against a real Redis.

## Risks and rollback

- A read/invalidate race can re-cache an old target for up to the TTL → documented in spec 004.
- Rollback: remove the `Redis` connection string; the in-memory cache takes over.

## Docs to update

- [x] Spec 004
- [x] README Trade-offs (Redis cache)

## Approval

- [ ] Plan approved by: ______ on ______
