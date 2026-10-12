# Plan 001 — Database design (greenfield, Task 1)

| | |
|---|---|
| Status | Implemented |
| Spec | [001 — Create short URL](../specs/001-create-short-url.md), [003 — Update and delete](../specs/003-update-delete-link.md), [005 — Click tracking](../specs/005-click-tracking.md) |
| ACs satisfied / added | schema-level behaviour of the specs above (unique code, cascade delete, duplicate lookup) |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 1 |

## Goal

Persist short links and their click history in SQL Server so that redirects are an index seek, duplicates are detectable, and deleting a link removes its clicks.

## Out of scope

Behaviour of the API, the code generator and the cache (plans 002–006). The `Browser` column on clicks was added later (migration `AddBrowserToClickEvents`, see [brownfield](../scenarios/brownfield.md)).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Entities/` | `ShortUrl`, `ClickEvent` entities | Two tables are enough: links and clicks |
| `src/Configurations/*Configuration.cs` | `bigint IDENTITY` PKs; unique case-sensitive `Code`; non-unique `OriginalUrlHash`; `ExpiresAt` index; composite `(ShortUrlId, ClickedAt)`; cascade FK; `RowVersion` | PK/FK, index and query-performance review (see ER diagram) |
| `src/Repositories/AppDbContext.cs` | DbSets and model-wide conventions | Single place for mapping |
| `src/Migrations/20261008044138_InitialCreate.cs` | Initial migration | Reproducible schema |
| `docs/schema.sql`, `docs/schema.generated.sql`, `docs/ER-Diagram.md` | Hand-written mirror, EF-generated script, diagram and review | Schema documentation |

## Steps

1. Entities and configurations — verify: `dotnet build`.
2. Initial migration — verify: `dotnet ef migrations script --project src` matches `docs/schema.generated.sql`.
3. Review keys and indexes against the queries each one serves — verify: [`docs/ER-Diagram.md`](../ER-Diagram.md).

## Test strategy

| Concern | Level | Test | Notes |
|---------|-------|------|-------|
| Cascade and cleanup against real SQL | integration | `DeletionTests` | see [plan 009](009-delete-cascade-gaps.md) |
| Case-sensitive codes | unit | `CachedUrlShortenerServiceTests.Resolve_CodesAreCaseSensitive` | plus `CS_AS` collation in the migration |

## Risks and rollback

- A 2048-char URL cannot be indexed → duplicate detection uses a SHA-256 hash column, not the URL.
- Rollback: `dotnet ef database update 0` on a throwaway database.

## Docs to update

- [x] `docs/schema.sql` and `docs/schema.generated.sql`
- [x] `docs/ER-Diagram.md`

## Approval

- [ ] Plan approved by: ______ on ______
