# Plan 007 — Return `expiresAt` as UTC everywhere

| | |
|---|---|
| Status | Implemented |
| Spec | [001 — Create short URL](../specs/001-create-short-url.md), [003 — Update and delete](../specs/003-update-delete-link.md) |
| ACs satisfied / added | `CRT-AC-16`, `UPD-AC-10` (new); regression guard for `CRT-AC-02`, `UPD-AC-02`, `RPT-AC-03/04` |
| Source | Requirement: all timestamps returned by the API are UTC. |

## Goal

Every `expiresAt` the API returns is UTC and serializes with `Z` — including the 200 duplicate-create response (CRT-AC-16) and an update without `expiryDays` (UPD-AC-10), which today return values read from SQL `datetime2` with `DateTimeKind.Unspecified`.

## Root cause

`datetime2` stores no offset. EF Core materializes it as `DateTimeKind.Unspecified`; `UrlShortenerService.ToResponse` (`src/Services/UrlShortenerService.cs:128-129`) passes the entity value straight into `ShortUrlResponse`, and System.Text.Json writes an Unspecified `DateTime` without an offset. The 201 path is unaffected only because it returns the in-memory `now`-based value (`Kind = Utc`).

## Approach

Fix it once at the data boundary, not per response: an EF Core **model-wide convention** in `AppDbContext.ConfigureConventions` that applies a value converter to every `DateTime` property — identity on write (all writes already come from `TimeProvider.GetUtcNow().UtcDateTime`), `DateTime.SpecifyKind(v, DateTimeKind.Utc)` on read. This also corrects `CreatedAt`, `ClickEvent.ClickedAt` and the `ResolvedLink.ExpiresAt` stored in the cache, and any `DateTime` column added later.

Rejected: `SpecifyKind` inside `ToResponse` — fixes only this symptom and leaves every other read path Unspecified.

## Out of scope

- Converting the API to `DateTimeOffset` (a breaking DTO change).
- Other Q-/S- findings and open questions in specs 001/003.

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Repositories/AppDbContext.cs` | Override `ConfigureConventions`: `Properties<DateTime>().HaveConversion<UtcDateTimeConverter>()`. | Single fix point for all reads. |
| `src/Repositories/UtcDateTimeConverter.cs` *(new)* | `ValueConverter<DateTime, DateTime>`: write `v`, read `DateTime.SpecifyKind(v, DateTimeKind.Utc)`; XML doc explaining why. | Named, reusable, documented. |
| `tests/IntegrationTests/ApiEndToEndTests.cs` | Add tests for CRT-AC-16, UPD-AC-10 and a Kind check on values read via EF (see Test strategy). | Behaviour is only observable with real SQL + JSON. |
| `docs/specs/001-create-short-url.md`, `003-update-delete-link.md` | Fill the Tests column and set status to Implemented. | Spec stays source of truth. |

No migration, no `docs/schema.sql` change: the provider type stays `datetime2`.

## Steps

1. **Tests first (test-author).** Add the integration tests below; they must **fail** against current code on the `Z` assertion. — verify: test-runner with `--filter "FullyQualifiedName~ApiEndToEndTests"` → new tests fail, all others pass.
2. **Converter.** Add `UtcDateTimeConverter`. — verify: `dotnet build`
3. **Convention.** Register it in `AppDbContext.ConfigureConventions`. — verify: `dotnet build`
4. **No schema drift.** — verify: `dotnet ef migrations has-pending-model-changes --project src` prints "No changes have been made to the model since the last migration." (baseline confirmed before starting).
5. **Green.** — verify: test-runner full suite; new tests pass, nothing else regresses (pay attention to `ReportEndpointTests.Daily_*`, which group by `ClickedAt.Date` in SQL).
6. **Review** (code-quality-reviewer ∥ security-reviewer) on the changed files.
7. **Close** docs per the table above; `bash .claude/skills/write-spec/check-specs.sh` exits 0.

## Test strategy

| AC | Level | Test (Class.Method) | Notes |
|----|-------|---------------------|-------|
| CRT-AC-16 | integration | `ApiEndToEndTests.Create_DuplicateUrl_ReturnsExpiresAtInUtc` | POST X twice. Assert on the **raw JSON** of the 200 response: `expiresAt` string ends with `Z` and parses to the same instant as the 201 response. (Deserializing to `DateTime` would hide the bug.) |
| UPD-AC-10 | integration | `ApiEndToEndTests.Update_WithoutExpiryDays_ReturnsUnchangedExpiresAtInUtc` | Create, PUT new URL without `expiryDays`, raw-JSON `expiresAt` ends with `Z` and equals the create response's instant. |
| (root cause guard) | integration | `ApiEndToEndTests.Repository_ReadsDateTimesAsUtcKind` | Via `IShortUrlRepository.GetByCodeAsync`, `ExpiresAt.Kind` and `CreatedAt.Kind` are `Utc`. Protects future endpoints. |

Unit tests aren't useful here: the defect only exists across the SQL → EF → JSON boundary.

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| A converter could stop EF translating `ClickedAt.Date` in `GetDailyClicksAsync` (`ShortUrlRepository.cs:62-73`) or the range filters. | Provider type is unchanged (`DateTime` → `datetime2`), so translation should be identical; step 5 runs `ReportEndpointTests` against real SQL to prove it. |
| Model snapshot drift forces a migration. | Step 4 checks it explicitly; if drift appears, stop and report rather than adding an empty migration blindly. |
| Existing cache entries were serialized without `Z`. | They deserialize as Unspecified with the same ticks; expiry comparison uses ticks, so behaviour is unchanged. All such entries expire within `Cache:TtlMinutes` (10). |
| Clients that parsed the offset-less value as UTC by convention. | They now get an explicit `Z` for the same instant — strictly more correct; no status codes change. |
| Rows written with a non-UTC Kind in future. | Out of scope; writes all use `TimeProvider.GetUtcNow().UtcDateTime` (`CLAUDE.md` convention, reviewer checklist item 8). |

Rollback: revert the two `src/` files; no data or schema to undo.
