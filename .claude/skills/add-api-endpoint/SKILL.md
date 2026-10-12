---
name: add-api-endpoint
description: Checklist for adding or changing an HTTP endpoint in this ASP.NET Core API so it matches existing conventions — routing with ApiRoutes.CodeSegment, rate-limit policy, FluentValidation, exception-to-ProblemDetails mapping, cache invalidation, Swagger metadata and integration tests. Use when adding a controller action, a new route, a new request/response DTO, or a new error status ("add an endpoint", "new API for…", "expose X over HTTP"). Do NOT use for background services, schema-only changes, or non-HTTP logic.
---

# Add or change an API endpoint

Conventions are summarised in `CLAUDE.md`; this is the step-by-step. Reference implementations: `src/Controllers/ShortenController.cs` (writes + validation), `src/Controllers/AnalyticsController.cs` (reads + query params).

## 1. Contract first

The endpoint's spec (`docs/specs/`) must list the route, request/response, and every status code. Update it before coding (`write-spec`).

## 2. DTOs — `src/DTOs/`

- Positional `record`s with XML `<param>` docs (they feed Swagger). Optional fields are nullable with defaults, e.g. `int? ExpiryDays = null`.
- Responses never expose entities (`src/Entities/`) or `RowVersion`.

## 3. Validation — `src/Validators/`

- One `AbstractValidator<TRequest>`; it's auto-registered by `AddValidatorsFromAssemblyContaining` in `Program.cs`.
- Reuse `UrlRules.ApplyUrlRules` for any URL field; bounds come from `IOptions<…Options>`, not literals.
- In the action call `await _validator.ValidateAndThrowAsync(request, ct);` first. Don't add `[Required]` — implicit required is disabled on purpose so all 400s share one shape.

## 4. Controller — `src/Controllers/`

- `[ApiController]`, `[Produces("application/json")]` for JSON APIs, route under `api/v1/…`.
- **Rate limiting:** class-level `[EnableRateLimiting(RateLimitPolicies.Write)]` for anything that writes, `RateLimitPolicies.Read` for reads/redirects. An endpoint without one is unprotected — that's a review Blocker unless the spec says otherwise (like `/health`).
- **Short codes in routes:** always `ApiRoutes.CodeSegment` (e.g. `[HttpGet(ApiRoutes.CodeSegment + "/daily")]`), never a bare `{code}`.
- `[ProducesResponseType]` for every status in the spec, including `ProblemDetails` for 4xx and **429**.
- Take `CancellationToken ct` and pass it down.

## 5. Errors

- Signal failures by throwing: `NotFoundException` (404), `LinkExpiredException` (410), `ConcurrencyConflictException` (409), `InvalidRequestException` (400). Don't return `NotFound()`/`BadRequest()` from actions.
- A new failure type needs a new exception in `src/Exceptions/ShortenerExceptions.cs` **and** a mapping line in `src/Middleware/ExceptionHandlingMiddleware.cs` (otherwise it becomes a 500).

## 6. Service wiring

- Anything that changes a link's target, expiry or existence must go through `IUrlShortenerService` and, in `src/Caching/CachedUrlShortenerService.cs`, remove the cache entry after the inner call succeeds.
- Reporting/read-only analytics belongs in `IReportService`, not the cached service.
- Inject `TimeProvider` for "now"; register new services in `Program.cs` with the right lifetime (scoped if they use `AppDbContext`).

## 7. Tests

- Integration test in `tests/IntegrationTests/` with `IClassFixture<ApiFactory>` and `factory.CreateNoRedirectClient()`; use `TestHelpers` (`UniqueUrl`, `CreateAsync`, `VisitAsync`, `InsertLinkAsync`, `EventuallyAsync`).
- Cover each status code in the spec. For problem responses assert `application/problem+json` and `traceId`.
- Rate limiting: add a case to `RateLimitIntegrationTests` (uses `LimitedApiFactory`).
- Validator rules: unit tests in `tests/UnitTests/Validators/`.

## Verify

1. `dotnet build` — no new warnings.
2. Run the new tests, then the full suite (`run-tests` skill or the `test-runner` agent).
3. Open Swagger (`/swagger`) or check `[ProducesResponseType]` lists match the spec's status table.
4. `bash .claude/skills/write-spec/check-specs.sh` exits 0 after updating the spec's Tests column.
