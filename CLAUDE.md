# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

ASP.NET Core 8 Web API URL shortener: SQL Server via EF Core, Redis cache-aside on redirects, asynchronous click analytics. Single app project at `src/UrlShortener.csproj`; solution file is `UrlShortener.slnx` (needs .NET SDK 10 to load the `.slnx`; projects target `net8.0`).

## Commands

```bash
# Infrastructure (SQL Server on 1433, Redis on 6379)
docker compose up -d sqlserver redis
docker compose up --build              # full stack, API on http://localhost:8080 (migrates on startup)

# Build / run
dotnet build
dotnet run --project src               # Database:MigrateOnStartup is false by default in appsettings.json

# Tests (integration tests REQUIRE SQL Server running; they use DB "UrlShortener_IntegrationTests",
# overridable via env var URLSHORTENER_TEST_DB)
dotnet test
dotnet test tests/UnitTests
dotnet test --filter "FullyQualifiedName~UrlShortenerServiceTests"
dotnet test --filter "FullyQualifiedName~UrlShortenerServiceTests.MethodName"
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults   # what CI runs
# Tests that need a real Redis are opt-in: set URLSHORTENER_TEST_REDIS (they use [RedisFact] and are skipped otherwise)

# EF Core migrations
dotnet ef migrations add <Name> --project src
dotnet ef migrations script --project src -o docs/schema.generated.sql

# Load test against a running API (not part of the solution; run API with RateLimit__Enabled=false)
dotnet run --project tests/PerformanceTests -c Release -- [baseUrl] [requests] [concurrency] [mode]   # mode: redirect (default) | create | mixed
```

Tests use xUnit + FluentAssertions (+ Moq in unit tests). No separate linter is configured.

## Architecture

Request pipeline (`src/Program.cs`): `ExceptionHandlingMiddleware` → Swagger → optional migrate-with-retry → `UseRateLimiter` → controllers, plus `/health` (database check; Redis down reports Degraded, not Unhealthy).

**Errors are exceptions, not result types.** Services throw `NotFoundException` (404), `LinkExpiredException` (410), `ConcurrencyConflictException` (409), `ValidationException`/`InvalidRequestException` (400; a plain `ArgumentException` from framework code is a 500 and its message is never echoed); `Middleware/ExceptionHandlingMiddleware.cs` maps them to `ProblemDetails`. Controllers call FluentValidation's `ValidateAndThrowAsync` explicitly; MVC's implicit `[Required]` is disabled so all 400s share one format.

**Decorator wiring for caching.** `IUrlShortenerService` resolves to `CachedUrlShortenerService`, which wraps the concrete `UrlShortenerService` (registered separately in `Program.cs`). Only `ResolveAsync` reads the cache; `UpdateAsync`/`DeleteAsync` must invalidate. 404/410 are never cached; TTL is capped at the link's remaining lifetime. Redis failures must never fail a request — they're logged, recorded in `CacheHealth` (circuit breaker), and fall back to SQL. With no Redis connection string, an in-memory distributed cache is used.

**Reporting is deliberately separate.** `ReportService` (daily/countries/browsers) talks to the repository directly and does not go through the cached service, so reporting changes don't touch the redirect path.

**Short code generation.** `ShortUrlRepository.AddAsync` inserts with a unique placeholder code, then sets `Code = Base62(Id + 62^5)` in the same transaction (`ShortCodeGenerator`). Codes are ≥6 chars, sequential and guessable (known trade-off). Create performs duplicate detection via SHA-256 of the normalized URL (`UrlNormalizer`) and returns 200 with the existing link instead of 201.

**Click analytics are fire-and-forget.** `RedirectController` → `ClickTracker.Track` (no I/O, never throws; hashes IP+UA+salt, resolves country from a header, detects browser) → bounded `ChannelClickQueue` (drops when full) → `ClickRecorderBackgroundService` batches inserts into `ClickEvents` using a new DI scope/`DbContext` per batch, falling back to row-by-row inserts when a batch fails (e.g. FK violation after link deletion). Batch inserts go through `IClickEventWriter` (`Analytics/ClickEventWriter.cs`) and are wrapped by the service in the `Transient` retry pipeline. On graceful shutdown the queue is drained within the host's 30 s timeout.

**Observability.** Logs go through Serilog; metrics and traces through OpenTelemetry, exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (compose points it at the Aspire dashboard on `http://127.0.0.1:18888`, local development only). Spans exclude `/health` and `/swagger` and drop the user agent.

**Background services** that need `DbContext` must create a scope via `IServiceScopeFactory` (they are singletons). `ExpiredLinkCleanupService` runs `ExpiredLinkCleaner` on a timer after a 30s startup delay; deleting a `ShortUrl` cascades to its `ClickEvents`.

**Other conventions**
- Inject `TimeProvider` for all "now" logic (tests depend on it); all times are UTC. `DateTime` values read through EF are marked UTC by `Repositories/UtcDateTimeConverter` (model-wide convention in `AppDbContext`), so responses serialize with `Z`.
- Short-code route constraint lives in `Controllers/ApiRoutes.CodeSegment` (1–12 Base62 chars); reuse it for new `{code}` routes.
- Rate limiting: per-client fixed window (per IPv4 address, per /64 for IPv6), policies `read`/`write` applied via `[EnableRateLimiting]` on controllers; options are read per request. **On** by default in `appsettings.json`; integration tests (`ApiFactory`) and load tests turn it off explicitly.
- Transient SQL retries go through `Resilience/SqlResilience` (Polly): `RequestRead` (time-budgeted) for request-path reads, `Transient` for background work, `Startup` for migrations. Never wrap request-path writes in retries (transactions / `RowVersion`).
- Record metrics only through `Observability/UrlShortenerMetrics`; never put codes, URLs, IPs or user agents in metric tags.
- Redirects are 302 on purpose (links can change/expire).
- Config sections map to `src/Configurations/*Options.cs`; EF entity mapping is in `src/Configurations/*Configuration.cs`. `docs/schema.sql` is hand-written to mirror those configurations — update it alongside schema changes.

## Documentation map

`README.md` (overview, API, trade-offs, limitations, AI collaboration log) · `docs/architecture.md` (component diagram and request flows) · `docs/ER-Diagram.md` (schema and index review) · `docs/scenarios/` (greenfield, brownfield, ambiguous requirement) · `docs/specs/` · `docs/plans/`.

## Agentic workflow

Behaviour changes follow the gated loop in `docs/PLAN.md` (spec → plan → tests → implement → run → review → close); the `feature-workflow` skill drives it. Stop at every approval gate.

- **Specs** `docs/specs/NNN-*.md` are the source of truth for behaviour; every AC lists its tests or is marked **GAP** (a spec with a GAP is not `Implemented`). After touching specs or tests, run `bash .claude/skills/write-spec/check-specs.sh` (must exit 0).
- **Plans** `docs/plans/NNN-*.md` from `docs/plans/_TEMPLATE.md`. Index in `docs/plans/README.md` (001–006 greenfield build, 007–014 brownfield changes, 015 documentation).
- **Skills** (`.claude/skills/`): `feature-workflow`, `write-spec`, `write-plan`, `add-api-endpoint`, `ef-schema-change`, `run-tests`.
- **Subagents** (`.claude/agents/`): `test-author` (writes tests, `tests/` only) → `test-runner` (runs them, never edits) → `code-quality-reviewer` ∥ `security-reviewer` (the five-axis staff review: correctness, readability and simplicity, architecture, performance | security; read-only; launch both in one message). Fix Blocker/Major findings before closing.

## Integration tests

`tests/IntegrationTests/ApiFactory.cs` boots the real app via `WebApplicationFactory<Program>` against real SQL Server, swaps Redis for an in-memory cache, and disables rate limiting and cleanup. Connection strings are set through environment variables in its static constructor because `Program.cs` reads the Redis setting while building the host, before test config callbacks run. Subclasses pass config overrides through the protected constructor (see `RateLimitIntegrationTests`).

CI (`.github/workflows/ci.yml`) runs restore/build/test in Release with a SQL Server service container, and separately builds `docker/Dockerfile`.
