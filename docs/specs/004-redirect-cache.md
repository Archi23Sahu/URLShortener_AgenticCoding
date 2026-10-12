# 004 — Redirect cache

| | |
|---|---|
| Status | Implemented |
| AC prefix | `CCH` |
| Last verified | 2026-10-12 |
| Related specs | 002 (redirect), 003 (update/delete invalidation), 008 (cleanup) |
| Plans | [005](../plans/005-redis-cache.md), [014](../plans/014-test-coverage.md) |

## Purpose

Serves `code → (id, url, expiresAt)` from Redis so most redirects never touch SQL Server. The cache is an optimisation only: any Redis problem degrades to SQL, never to an error.

## API contract

No endpoints. Implemented as a decorator: `IUrlShortenerService` resolves to `CachedUrlShortenerService` wrapping the concrete `UrlShortenerService` (`src/Program.cs`).

Cache entry:
- Key: `urlshortener:link:{code}` (instance name `Program.cs` + prefix `CachedUrlShortenerService.cs`). Case-sensitive.
- Value: JSON-serialized `ResolvedLink { Id, Url, ExpiresAt }`.

## Behaviour

1. **Resolve** (`CachedUrlShortenerService.cs`)
   - Read the entry. If present **and** `ExpiresAt > now`, return it without calling SQL (hit).
   - Otherwise call the inner service. 404/410 exceptions propagate and **nothing is cached**.
   - On success, store the entry with absolute TTL = `min(Cache:TtlMinutes, ExpiresAt − now)`; never cache a link with no remaining lifetime.
2. **Update / Delete** call the inner service first; only on success remove the entry. If the inner call throws, the entry is kept.
3. **Create / GetAnalytics** pass straight through.
4. **Circuit breaker** (`src/Caching/CacheHealth.cs`): any Redis read/write/remove failure records a failure; for `Cache:FailureCooldownSeconds` afterwards reads and writes skip Redis entirely. Singleton, lock-free (`Interlocked`).
5. **Invalidation ignores the breaker**: remove is attempted even when the circuit is open, with `CancellationToken.None`, because a stale entry is worse than a slow request.
6. An unreadable entry (`JsonException`) is a miss and is overwritten; it does not trip the breaker.
7. The caller's own cancellation is re-thrown, not swallowed as a cache failure (`when (!ct.IsCancellationRequested)` filters).
8. **Redis client settings** (`Program.cs`): `AbortOnConnectFail=false` (app starts with Redis down), connect timeout 1000 ms, sync/async timeouts 500 ms.
9. **No Redis configured** (`ConnectionStrings:Redis` empty) → `AddDistributedMemoryCache()`; same code path, in-process (`Program.cs`).

## Edge cases and failure modes

| Situation | Result |
|-----------|--------|
| Redis down at startup | App starts; the first redirect pays ≈1.5–2.5 s (connect 1 s + command timeout 0.5 s, checked on a ~1 s heartbeat), then the breaker skips Redis for the cooldown (30 s). When the cooldown ends there is no single probe: every request that arrives while Redis is still unreachable pays the timeout until one failure re-opens the breaker. |
| Redis read throws | Served from SQL, breaker opens. |
| Redis write throws | Link still returned. |
| Invalidation throws | Update/delete still succeed; entry may be stale for ≤ `TtlMinutes` (warning logged). |
| Cleanup deletes an expired link | No eviction needed: its entry's TTL already ended at `ExpiresAt` (`ExpiredLinkCleaner.cs`). |

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `ConnectionStrings:Redis` | `localhost:6379` | Empty → in-memory cache. |
| `Cache:TtlMinutes` | `10` (min 1) | Max entry lifetime; also the worst-case staleness after a missed invalidation. |
| `Cache:FailureCooldownSeconds` | `30` (min 1) | Breaker open time after a failure. |

The `CachedUrlShortenerService` log category is raised to Debug only by `src/appsettings.Development.json`; the shipped `appsettings.json` does not enable it, so a production redirect writes no log line on a cache hit or miss (the `urlshortener.cache.lookups` metric records them).

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| CCH-AC-01 | Given an empty cache, when resolved twice, then the first call reads the inner service and stores, the second is a hit. | `CachedUrlShortenerServiceTests.Resolve_Miss_ReadsInnerAndStores_ThenSecondCallIsAHit` |
| CCH-AC-02 | Given a cached entry, when resolved, then the inner service is not called. | `CachedUrlShortenerServiceTests.Resolve_Hit_DoesNotCallInner` |
| CCH-AC-03 | Given a cached entry whose `ExpiresAt` has passed, then the inner service decides (410). | `CachedUrlShortenerServiceTests.Resolve_CachedEntryIsPastExpiry_FallsBackToInner`, `SecurityTests.ExpiredLink_StillReturns410OnSecondVisit_NotFromCache` |
| CCH-AC-04 | Given a link with remaining lifetime L, then TTL = min(TtlMinutes, L). | `CachedUrlShortenerServiceTests.Resolve_Miss_TtlIsSmallerOfMaxAndRemainingLifetime` |
| CCH-AC-05 | Given the inner service throws NotFound, then it propagates and nothing is cached. | `CachedUrlShortenerServiceTests.Resolve_InnerThrowsNotFound_PropagatesAndCachesNothing` |
| CCH-AC-06 | Given a cached link, when updated successfully, then the entry is removed. | `CachedUrlShortenerServiceTests.Update_Success_RemovesCachedEntry` |
| CCH-AC-07 | Given the inner update throws, then the cached entry is kept. | `CachedUrlShortenerServiceTests.Update_InnerThrows_KeepsCachedEntry` |
| CCH-AC-08 | Given a cached link, when deleted successfully, then the entry is removed; a NotFound delete propagates. | `CachedUrlShortenerServiceTests.Delete_Success_RemovesCachedEntry`, `CachedUrlShortenerServiceTests.Delete_NotFound_Propagates` |
| CCH-AC-09 | Given cache remove throws, then the update still returns its result. | `CachedUrlShortenerServiceTests.Update_CacheRemoveFails_StillReturnsTheUpdate` |
| CCH-AC-10 | Given cache read throws, then the link is served from the inner service. | `CachedUrlShortenerServiceTests.Resolve_CacheReadThrows_FallsBackToInner` |
| CCH-AC-11 | Given cache write throws, then the link is still returned. | `CachedUrlShortenerServiceTests.Resolve_CacheWriteThrows_StillReturnsTheLink` |
| CCH-AC-12 | Given a cache failure, then the cache is skipped for the cooldown and retried after it. | `CachedUrlShortenerServiceTests.Resolve_AfterCacheFailure_SkipsCacheDuringCooldown_ThenRetries`, `CacheHealthTests.RecordFailure_BlocksUntilCooldownPasses`, `CacheHealthTests.IsAvailable_Initially_True` |
| CCH-AC-13 | Given a corrupt entry, then it is treated as a miss and overwritten. | `CachedUrlShortenerServiceTests.Resolve_CorruptCacheEntry_IsTreatedAsMissAndOverwritten` |
| CCH-AC-14 | Given codes differing only in case, then they use different entries. | `CachedUrlShortenerServiceTests.Resolve_CodesAreCaseSensitive` |
| CCH-AC-15 | Create and analytics delegate to the inner service unchanged. | `CachedUrlShortenerServiceTests.Create_And_Analytics_AreDelegatedToInner` |
| CCH-AC-16 | Given the breaker is open, when a link is updated, then removal is still attempted. | `CachedUrlShortenerServiceTests.Update_WhileBreakerIsOpen_StillAttemptsToRemoveTheEntry` |
| CCH-AC-17 | Given a real but unreachable Redis endpoint, then redirects still succeed from SQL within the configured timeouts. | `RedisCacheTests.RedisUnreachable_RedirectStillServedFromSql` |
| CCH-AC-18 | Given a real Redis, when a link is redirected, then `urlshortener:link:{code}` exists in Redis; when the link is updated or deleted, the key is removed. | `RedisCacheTests.Redirect_PopulatesRedis_UpdateAndDeleteRemoveKey` (runs only when URLSHORTENER_TEST_REDIS is set) |
| CCH-AC-19 | Given the shipped configuration, then the cache decorator is not logged at Debug (only the Development settings enable it). | `RateLimitPolicyTests.ShippedConfig_DoesNotEnableDebugLoggingForCacheDecorator` |

## Known limitations

- **Read/invalidate race:** a resolve that misses and reads the old row from SQL can call `TrySetAsync` *after* a concurrent update/delete has removed the key, re-caching the old target for up to `TtlMinutes`. Bounded by the TTL. After a **delete**, the stale entry keeps redirecting the deleted link for that time, and each such click fails its foreign key and is dropped by the click writer.
- Cache hits are trusted as-is — the cached `Url` is not re-validated, so anyone with write access to Redis can redirect any code; restrict network access to Redis.
- Negative results (404) are not cached, so floods of unknown codes reach SQL; rate limiting (spec 007) is the mitigation.
- With the in-memory fallback each app instance has its own cache, so invalidation does not reach other instances (stale ≤ TTL). Use Redis for multi-instance deployments.
