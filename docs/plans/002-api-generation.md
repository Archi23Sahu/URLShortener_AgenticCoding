# Plan 002 — API surface (greenfield, Task 2)

| | |
|---|---|
| Status | Implemented |
| Spec | [001 — Create short URL](../specs/001-create-short-url.md), [002 — Redirect](../specs/002-redirect.md), [003 — Update and delete](../specs/003-update-delete-link.md) |
| ACs satisfied / added | the API-contract ACs of the specs above |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 2 |

## Goal

Expose create, redirect, update, delete and reporting over HTTP with consistent routes, status codes and error bodies.

## Out of scope

Authentication (a documented limitation), rate limiting (spec 007, [plan 010](010-rate-limit-coverage-gaps.md) and [plan 013](013-rate-limit-default-on.md)), and reports beyond the basic click count ([spec 006](../specs/006-analytics-reports.md)).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Controllers/ShortenController.cs`, `RedirectController.cs`, `AnalyticsController.cs` | Actions with `[ProducesResponseType]` | Swagger documents every status |
| `src/Controllers/ApiRoutes.cs` | `CodeSegment` (1–12 Base62 chars) | Malformed codes 404 before a controller runs |
| `src/DTOs/ShortUrlDtos.cs` | Request/response records | Explicit contracts |
| `src/Validators/` | FluentValidation, called explicitly with `ValidateAndThrowAsync` | One 400 format |
| `src/Exceptions/`, `src/Middleware/ExceptionHandlingMiddleware.cs` | Domain exceptions mapped to RFC 7807 `ProblemDetails` | 5xx bodies hide internals |

## Steps

1. DTOs and validators — verify: `dotnet build`.
2. Exceptions and middleware — verify: `dotnet test --filter "FullyQualifiedName~ExceptionHandlingMiddlewareTests"`.
3. Controllers on top of `IUrlShortenerService` — verify: `dotnet test --filter "FullyQualifiedName~ApiEndToEndTests"`.

## Test strategy

| Concern | Level | Test | Notes |
|---------|-------|------|-------|
| Happy paths and status codes | integration | `ApiEndToEndTests` | 201 vs 200 on duplicate, 302, 404, 410 |
| Abuse inputs | integration | `SecurityTests` | invalid URL, injection, oversized body, content type, header injection |

## Risks and rollback

- 301 would let browsers cache a link that can change or expire → 302 on purpose.
- Routes are versioned (`/api/v1/…`); breaking changes need a new version.

## Docs to update

- [x] Specs 001–003 (API contract tables)
- [x] README API reference

## Approval

- [ ] Plan approved by: ______ on ______
