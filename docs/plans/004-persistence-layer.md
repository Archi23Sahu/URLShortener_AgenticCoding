# Plan 004 — Persistence layer (greenfield, Task 4)

| | |
|---|---|
| Status | Implemented |
| Spec | [001 — Create short URL](../specs/001-create-short-url.md), [003 — Update and delete](../specs/003-update-delete-link.md) |
| ACs satisfied / added | persistence, duplicate-detection and concurrency ACs of the specs above |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 4 |

## Goal

Read and write links through a repository over EF Core with correct transactions and optimistic concurrency.

## Out of scope

Retries on transient SQL errors (added in [plan 011](011-polly-resilience.md)) and caching (plan 005).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Repositories/IShortUrlRepository.cs`, `ShortUrlRepository.cs` | Add / get / update / delete | The service never touches `DbContext` |
| `ShortUrlRepository.AddAsync` | Placeholder code, then `Code = Base62(Id + 62^5)` in one transaction | A row never exists without its final code |
| `src/Services/UrlNormalizer.cs` | Normalize, then SHA-256 | Duplicate create returns 200 with the existing link |
| `src/Services/UrlShortenerService.cs` | Orchestrates repository and generator; throws domain exceptions | Errors are exceptions, not result types |
| `RowVersion` on `ShortUrl` | Optimistic concurrency | Concurrent update surfaces as 409 |
| `src/Repositories/UtcDateTimeConverter.cs` | Mark `DateTime` read from SQL as UTC | Added later: see [plan 007](007-utc-expires-at.md) |

## Steps

1. Repository and entities — verify: `dotnet build`.
2. Service and normalizer — verify: `dotnet test tests/UnitTests`.
3. Concurrency and uniqueness against real SQL — verify: `dotnet test --filter "FullyQualifiedName~ApiEndToEndTests"`.

## Test strategy

| Concern | Level | Test | Notes |
|---------|-------|------|-------|
| Concurrent update gives a conflict | integration | `ApiEndToEndTests.Update_ConcurrentChange_ThrowsConcurrencyConflict` | two contexts, real SQL |
| Unique codes under concurrency | integration | `ApiEndToEndTests.Create_ManyDifferentUrls_ReturnsUniqueCodes` | |
| Reads | — | `AsNoTracking` for redirects and reports; tracked load only for updates | review point |

## Risks and rollback

- Request-path writes are never wrapped in retries (transactions, `RowVersion`).
- Duplicate lookup (`FindActiveByHashAsync`) matches on hash and normalized URL, and only reuses an active (unexpired) link.

## Docs to update

- [x] Specs 001 and 003

## Approval

- [ ] Plan approved by: ______ on ______
