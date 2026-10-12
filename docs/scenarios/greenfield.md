# Scenario 1: Greenfield development

**Requirement:** build a URL shortener from scratch.

This document maps each task in the assessment to what was built, the engineering decision taken, and how it was validated. Behaviour is specified in [`docs/specs/`](../specs/README.md); the specs are the contract for every later change.

> The baseline was planned in build order by plans [001](../plans/001-database-design.md)–[006](../plans/006-click-analytics.md), one per task below. Brownfield plans 007–014 follow.
## Task 1: Database design

| | |
|---|---|
| **Delivered** | Two tables, `ShortUrls` and `ClickEvents`, with EF Core configurations in `src/Configurations/`. Diagram, indexes and the PK/FK review: [`docs/ER-Diagram.md`](../ER-Diagram.md). SQL: `docs/schema.sql` (hand-written mirror) and `docs/schema.generated.sql` (from EF). |
| **Engineer review points** | **PK/FK:** surrogate `bigint IDENTITY` keys; `ClickEvents.ShortUrlId` cascades on delete. **Indexes:** unique case-sensitive `Code`; non-unique `OriginalUrlHash` (a 2048-char URL can't be indexed directly); `ExpiresAt` for cleanup; composite `(ShortUrlId, ClickedAt)` for every report. **Query performance:** redirect is an index seek on `UX_ShortUrls_Code`; reports are range seeks. |
| **Validation** | Cascade and cleanup against real SQL: `DeletionTests` ([plan 009](../plans/009-delete-cascade-gaps.md)). Case-sensitive codes: `CachedUrlShortenerServiceTests.Resolve_CodesAreCaseSensitive` (cache key) and the `CS_AS` collation in the migration. |

## Task 2: API generation

| | |
|---|---|
| **Delivered** | `ShortenController`, `RedirectController`, `AnalyticsController`; request/response records in `src/DTOs/`; Swagger via `[ProducesResponseType]` on every action. |
| **Engineer review points** | **Naming:** `/api/v1/…` versioned routes, `{code}` constrained by `ApiRoutes.CodeSegment` (1–12 Base62 chars) so malformed codes 404 before reaching a controller. **API standards:** 201 vs 200 for create-vs-duplicate, 302 (not 301) for redirects, 410 for expired. **Error handling:** every failure is a domain exception mapped to RFC 7807 `problem+json` by `ExceptionHandlingMiddleware`; 5xx bodies hide internals. |
| **Validation** | `ApiEndToEndTests`, `SecurityTests` (invalid URL, injection, oversized bodies, content type, header injection). |

## Task 3: URL shortening algorithm

| | |
|---|---|
| **AI options** | Random strings, GUID/UUID, hash truncation, counter + Base62. |
| **Engineer decision** | **Base62 of the database identity**, not GUID/UUID: a 6-character code instead of 22–36, and no collision handling because the identity is unique. `Code = Base62(Id + 62^5)` guarantees at least 6 characters. See `Services/Base62Encoder.cs`, `Services/ShortCodeGenerator.cs`. |
| **Trade-off accepted** | Codes are sequential and therefore guessable. Links are not treated as secret; this is listed under Limitations. |
| **Validation** | `Base62EncoderTests` (round-trip, uniqueness of the first 10 000 values, invalid input), `ShortCodeGeneratorTests`. |

## Task 4: Persistence layer

| | |
|---|---|
| **Delivered** | `IShortUrlRepository` / `ShortUrlRepository` over EF Core `AppDbContext`; migrations in `src/Migrations/`. |
| **Engineer review points** | **Transactions:** create inserts with a placeholder code, then sets the real code in the same transaction, so a row never exists without its final code. **Concurrency:** `RowVersion` optimistic locking on update, surfaced as 409. **Reads:** `AsNoTracking` for redirects and reports; tracked load only for updates. |
| **Validation** | `ApiEndToEndTests.Update_ConcurrentChange_ThrowsConcurrencyConflict` (two contexts, real SQL), `Create_ManyDifferentUrls_ReturnsUniqueCodes` (25 concurrent creates). |

## Task 5: Redis cache

| | |
|---|---|
| **Delivered** | `CachedUrlShortenerService`: a cache-aside decorator around the real service, plus a `CacheHealth` circuit breaker. |
| **Engineer review points** | **TTL:** `min(Cache:TtlMinutes, link's remaining lifetime)`, so an expired link is never served from cache. **Cache-hit optimisation:** a hit never touches SQL. **Invalidation:** update and delete remove the key *after* the database change succeeds. **Failure mode:** any Redis error degrades to SQL; the breaker skips Redis for 30 s so a dead cache doesn't slow every redirect. |
| **Validation** | 18 acceptance criteria in [spec 004](../specs/004-redirect-cache.md): unit tests with fake clocks, and `RedisCacheTests` against a real Redis. |
| **Known limitation** | A read/invalidate race can re-cache an old target for up to the TTL (documented in spec 004). |

## Task 6: Analytics

| | |
|---|---|
| **AI options** | Write the click inside the redirect request; fire-and-forget task; message broker; in-process queue with a background writer. |
| **Engineer decision** | **Asynchronous processing** through a bounded in-memory channel and a batching background service, so redirect latency is independent of database write load. The accepted cost is eventual consistency (about two seconds) and at-most-once delivery (a crash loses queued clicks). |
| **Engineer review points** | Bounded queue (a spike can't grow memory); batch fallback to row-by-row so one click for a deleted link can't discard its neighbours; queue drained on graceful shutdown; visitor identity is a salted SHA-256 so no raw IP is stored. |
| **Validation** | `ClickPipelineTests` (batch survives a bad click, queued clicks written on shutdown), `ClickRecorderBackgroundServiceTests`, `ClickTrackerTests`. |

## Validation overall

| Layer | Where | Count at the time of writing |
|-------|-------|------------------------------|
| Unit tests | `tests/UnitTests` | see `dotnet test` output |
| Integration tests (real SQL Server; Redis when `URLSHORTENER_TEST_REDIS` is set) | `tests/IntegrationTests` | see `dotnet test` output |
| Performance tests | `tests/PerformanceTests` (`redirect`, `create`, `mixed` modes) | manual |
| Security tests | `tests/IntegrationTests/SecurityTests.cs` | included above |

## Deliverable

A working URL shortener: `docker compose up --build` starts SQL Server, Redis, the API on `http://localhost:8080` and a monitoring dashboard on `http://127.0.0.1:18888`. See the [README](../../README.md).
