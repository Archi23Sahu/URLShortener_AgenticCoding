# URL Shortener

A URL shortening service built with **.NET 8, ASP.NET Core, EF Core, SQL Server and Redis**, with production-oriented features (caching, rate limiting, retries, observability) and developed with an engineer-led, AI-assisted workflow. Every feature has a spec, every phase of the build has a plan, and AI-generated code and tests went through human review and automated reviewer agents. It is **not hardened for public exposure**: read [Limitations](#limitations) first (no authentication, development credentials in the defaults).

- Shorten a URL, redirect with a 302, update or delete a link, expire links automatically.
- Click analytics: totals, unique visitors, daily clicks, countries, browsers.
- Redis cache-aside on the redirect path, with a circuit breaker so a dead cache never breaks redirects.
- Rate limiting, Polly retries, health checks, structured logs, OpenTelemetry metrics and traces.

> Documentation map: **[architecture](docs/architecture.md)** · **[ER diagram and schema review](docs/ER-Diagram.md)** · **[feature specs](docs/specs/README.md)** · **[plans index](docs/plans/README.md)** · **[engineering workflow and rubric](docs/PLAN.md)** · **[scenario write-ups](docs/scenarios/)**

## Technology stack

| Area | Choice |
|------|--------|
| Runtime / API | .NET 8, ASP.NET Core Web API, C#; Swagger / OpenAPI |
| Database / ORM | SQL Server 2022, Entity Framework Core 8 (migrations) |
| Cache | Redis 7 (`Microsoft.Extensions.Caching.StackExchangeRedis`) |
| Validation | FluentValidation |
| Resilience | Polly v8 (`Microsoft.Extensions.Resilience`) |
| Logging / telemetry | Serilog; OpenTelemetry (metrics + traces over OTLP) |
| Tests | xUnit, FluentAssertions, Moq |
| CI/CD, containers | GitHub Actions, Docker, docker-compose |

## Quick start

### With Docker (everything)

```bash
docker compose up --build
```

| Service | URL |
|---------|-----|
| API | http://localhost:8080 (Swagger at `/swagger`) |
| Monitoring dashboard (metrics, traces) | http://127.0.0.1:18888 (anonymous, loopback-only, local development only) |
| Health | http://localhost:8080/health |

Migrations are applied automatically at startup. Rate limiting is **on**; for load tests from a single IP, uncomment `RateLimit__Enabled: "false"` in `docker-compose.yml`.

The compose stack is a **development stack**: the API runs with `ASPNETCORE_ENVIRONMENT=Development`, SQL Server uses a development `sa` password (override it with `MSSQL_SA_PASSWORD` in the environment or an `.env` file), and the dashboard has authentication disabled.

### Locally

```bash
docker compose up -d sqlserver redis      # dependencies only
dotnet run --project src                   # http://localhost:5265 (Swagger at /swagger)
```

`Database:MigrateOnStartup` is `false` by default; either set `Database__MigrateOnStartup=true` or run
`dotnet ef database update --project src`.

Set `Shortener__BaseUrl=http://localhost:5265` so returned short URLs point at the locally running API (the default is the compose address, `http://localhost:8080`). The API examples below use the compose address and port.

> The compose file publishes SQL Server (1433) and Redis (6379) on all interfaces with development credentials. Don't run it on an untrusted network (see [Limitations](#limitations)).

## API

Interactive docs: `/swagger`. Contracts, status codes and acceptance criteria live in the [specs](docs/specs/README.md).

```bash
# Create (201 with Location; 200 with the existing link if the URL is already active)
curl -X POST http://localhost:8080/api/v1/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"https://www.example.com/very/long/url","expiryDays":30}'
# {"shortUrl":"http://localhost:8080/100001","expiresAt":"2026-11-11T10:00:00Z"}

# Redirect (302; 404 unknown, 410 expired)
curl -i http://localhost:8080/100001

# Update target and optionally reset expiry (404, 409 on concurrent change, 410 if expired)
curl -X PUT http://localhost:8080/api/v1/shorten/100001 \
  -H "Content-Type: application/json" -d '{"url":"https://example.org/new","expiryDays":10}'

# Delete a link and its click history (204)
curl -X DELETE http://localhost:8080/api/v1/shorten/100001

# Analytics
curl http://localhost:8080/api/v1/analytics/100001                     # clicks, uniqueVisitors, countries
curl "http://localhost:8080/api/v1/analytics/100001/daily?from=2026-10-01&to=2026-10-11"
curl http://localhost:8080/api/v1/analytics/100001/countries
curl http://localhost:8080/api/v1/analytics/100001/browsers
```

| Endpoint | Success | Errors |
|----------|---------|--------|
| `POST /api/v1/shorten` | 201 / 200 (duplicate) | 400, 413, 415, 429 |
| `GET /{code}` | 302 | 404, 410, 429 |
| `PUT /api/v1/shorten/{code}` | 200 | 400, 404, 409, 410, 413, 429 |
| `DELETE /api/v1/shorten/{code}` | 204 | 404, 429 |
| `GET /api/v1/analytics/{code}[/daily\|/countries\|/browsers]` | 200 | 400, 404, 429 |
| `GET /health` | 200 `Healthy` / `Degraded`, 503 `Unhealthy` | |

All errors are RFC 7807 `application/problem+json` with a `traceId`. Countries are keyed by ISO 3166-1 alpha-2 code (`"IN"`, not `"India"`), taken from a CDN/proxy header (`CF-IPCountry` by default).

## Configuration

Set in `src/appsettings.json` or as environment variables (`Section__Key`).

| Key | Default | Meaning |
|-----|---------|---------|
| `ConnectionStrings:Default` / `:Redis` | local dev values | SQL Server and Redis; an empty Redis string uses an in-memory cache |
| `Shortener:BaseUrl` / `DefaultExpiryDays` / `MaxExpiryDays` | `http://localhost:8080` / 30 / 365 | Short-link prefix and lifetimes |
| `Cache:TtlMinutes` / `FailureCooldownSeconds` | 10 / 30 | Redirect-cache lifetime cap and breaker cooldown |
| `RateLimit:Enabled` | **`true`** | Per-IP fixed window; `Write` 20/min, `Read` 300/min |
| `Analytics:Salt` | `dev-only-change-me` | **Change in every real environment** (visitor hashing) |
| `Analytics:QueueCapacity` / `BatchSize` / `FlushIntervalSeconds` / `CountryHeader` | 10000 / 100 / 2 / `CF-IPCountry` | Click pipeline |
| `Cleanup:Enabled` / `IntervalMinutes` / `GracePeriodDays` / `BatchSize` | true / 60 / 7 / 1000 | Expired-link cleanup |
| `Resilience:*` | see [spec 010](docs/specs/010-resilience.md) | Polly retries and the request-path time budget |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | Enables OTLP export when set |

## Reliability and production features

| Feature | Where | Spec | Plans |
|---------|-------|------|-------|
| Duplicate URL detection, configurable expiration | `UrlShortenerService`, `UrlNormalizer` | [001](docs/specs/001-create-short-url.md) | [004](docs/plans/004-persistence-layer.md), [007](docs/plans/007-utc-expires-at.md) |
| Redis caching with invalidation and circuit breaker | `CachedUrlShortenerService`, `CacheHealth` | [004](docs/specs/004-redirect-cache.md) | [005](docs/plans/005-redis-cache.md), [014](docs/plans/014-test-coverage.md) |
| Asynchronous click analytics | `ClickTracker` → queue → `ClickRecorderBackgroundService` | [005](docs/specs/005-click-tracking.md) | [006](docs/plans/006-click-analytics.md), [008](docs/plans/008-click-pipeline-gaps.md), [011](docs/plans/011-polly-resilience.md) |
| Reports: daily, country, browser | `ReportService` | [006](docs/specs/006-analytics-reports.md) | [006](docs/plans/006-click-analytics.md) |
| Rate limiting middleware | `RateLimitingSetup` | [007](docs/specs/007-rate-limiting.md) | [010](docs/plans/010-rate-limit-coverage-gaps.md), [013](docs/plans/013-rate-limit-default-on.md) |
| Background cleanup of expired links | `ExpiredLinkCleanupService` | [008](docs/specs/008-expired-link-cleanup.md) | [009](docs/plans/009-delete-cascade-gaps.md) |
| Metrics, monitoring, health checks | `UrlShortenerMetrics`, OpenTelemetry, `/health` | [009](docs/specs/009-observability.md) | [012](docs/plans/012-observability.md) |
| Retry policies using Polly | `SqlResilience` | [010](docs/specs/010-resilience.md) | [011](docs/plans/011-polly-resilience.md) |
| Global exception handling, FluentValidation, Serilog | `ExceptionHandlingMiddleware`, `src/Validators/` | [002](docs/specs/002-redirect.md), [003](docs/specs/003-update-delete-link.md) | [002](docs/plans/002-api-generation.md) |

## Testing

```bash
docker compose up -d sqlserver redis        # integration tests need SQL Server; Redis is optional
dotnet test                                  # unit + integration; Redis tests are skipped without the variable below
URLSHORTENER_TEST_REDIS=localhost:6379 dotnet test      # also run the tests that need a real Redis
#   PowerShell:  $env:URLSHORTENER_TEST_REDIS="localhost:6379"; dotnet test
```

| Kind | Where | What it covers |
|------|-------|----------------|
| Unit | `tests/UnitTests` | Base62, URL normalization, services, validators, cache decorator, click pipeline, resilience, metrics, rate-limit policy |
| Integration | `tests/IntegrationTests` | Real SQL Server (own database `UrlShortener_IntegrationTests`): full API lifecycle, reports, rate limiting, cascades, cleanup, health; real Redis when enabled |
| Security | `tests/IntegrationTests/SecurityTests.cs` | SQL injection, invalid and unsafe URLs, XSS, header injection, oversized bodies, expired links |
| Performance | `tests/PerformanceTests` | `redirect`, `create` (database writes), `mixed` load modes |

```bash
# start the API with limiting off (PowerShell: $env:RateLimit__Enabled="false"; dotnet run --project src --launch-profile https):
RateLimit__Enabled=false dotnet run --project src --launch-profile https      # https://localhost:7037
# then, in another terminal:
dotnet run --project tests/PerformanceTests -c Release -- https://localhost:7037 1000 1000 redirect
dotnet run --project tests/PerformanceTests -c Release -- https://localhost:7037 500 100 create
dotnet run --project tests/PerformanceTests -c Release -- https://localhost:7037 1000 200 mixed
```

#### Measured results

Run on 2026-10-12, API in Release mode with `RateLimit__Enabled=false`, over HTTP on `localhost`, SQL Server and Redis in Docker on the same machine (Intel Core Ultra 9 185H, 22 logical cores, 32 GB), load generator on the same machine. Warm run (second of two for the redirect and mixed rows); single-machine numbers, so read them as relative, not absolute.

| Scenario | Requests / concurrency | Throughput | p50 | p95 | p99 | Failures |
|----------|------------------------|-----------:|----:|----:|----:|---------:|
| Redirect, one hot link (Redis hits) | 1000 / **1000** | 2,656 req/s | 209 ms | 356 ms | 364 ms | 0 (all 302) |
| Redirect, one hot link | 5000 / 200 | 3,381 req/s | 56 ms | 80 ms | 132 ms | 0 (all 302) |
| Create (database writes: insert + code update) | 500 / 100 | 457 req/s | 54 ms | 859 ms | 951 ms | 0 (all 201) |
| Mixed 80% redirect / 10% create / 10% analytics | 1000 / 200 | 2,422 req/s | redirect 56 ms · create 140 ms · analytics 104 ms | redirect 125 ms · create 215 ms · analytics 175 ms | redirect 136 ms · create 223 ms · analytics 188 ms | 0 |

What they show: 1000 simultaneous requests all succeed with no errors or 429s; at that concurrency the latency is mostly queueing (all 1000 start at once), which is why the same endpoint shows a 56 ms median at concurrency 200. Create is the slowest path because each request is a transaction (insert, then set the code); its long p95 tail was seen with 100 concurrent writers against a single SQL Server container and was not investigated further. These are not capacity guarantees for a deployed environment.

Full run on 2026-10-12: **379 tests, all passing** (261 unit + 118 integration, with Redis enabled; 377 passed and 2 skipped without it), 0 build warnings. Run `dotnet test` for current numbers.

## Trade-offs

| Decision | Choice and reason | Cost |
|----------|-------------------|------|
| **Base62 vs GUID** | Base62 of the database identity: 6-character codes, unique by construction, no retry loop. | Codes are sequential and guessable. |
| **Redis cache** | Cache-aside on the redirect path takes most reads off SQL Server; TTL never exceeds the link's lifetime. | Invalidation complexity; a rare read/invalidate race can serve an old target for up to the TTL. |
| **Asynchronous analytics** | Redirect latency is independent of database writes; batching cuts insert cost. | Eventual consistency (~2 s) and at-most-once delivery: a crash or full queue loses clicks. |
| **SQL Server** | Transactional reliability, rowversion optimistic locking, strong relational modelling. | Heavier to run locally than SQLite; PostgreSQL would need the collation changed. |
| **302 not 301** | Links can be updated, expire or be deleted; browsers cache 301 forever. | Slightly more traffic to the service. |
| **Exceptions for domain errors** | One mapping point to RFC 7807 responses. | Exceptions on the 404/410 path (cheap relative to the I/O). |
| **OTLP, not a Prometheus endpoint** | Stable packages; works with any collector. | Needs a collector (an Aspire dashboard container is included). |

## Limitations

- **Authentication is not implemented.** Anyone who knows a code can update, delete or read analytics for it. With sequential codes, links are enumerable. (An expected limitation of the assessment.)
- Duplicate detection means the first creator of a URL owns the code later creators receive, and can retarget it. Because creating a URL that already exists returns its code, anyone who knows a target URL can also look up its short code.
- SQL Server and Redis ports in `docker-compose.yml` are published on all interfaces. Redis has no password, so an exposed port is full administrative access to Redis (and a poisoned cache entry redirects without URL validation); the `sa` login with the shipped password allows command execution on the SQL Server container. Bind these ports to loopback and set a Redis password before sharing the stack.
- Rate limiting is per instance and keyed on the connection IP (IPv6 clients by their /64: clients sharing a /64, such as a cloud subnet or an office LAN, share one bucket, as clients behind one IPv4 NAT address do; a client that holds many /64s gets one bucket per /64, so per-client limiting is not abuse-proof). **Behind a reverse proxy or CDN, which is the intended deployment, the app sees only the proxy's address**: there is no forwarded-headers support, so all clients share one bucket (300 reads and 20 writes per minute in total) and a single client can exhaust it for everyone. Terminate client connections at the app, or add forwarded-headers support with known proxies, before putting it behind one.
- Single region, basic analytics (no export), no custom domains, no QR codes.
- **Vulnerable transitive packages:** `dotnet list package --vulnerable` flags three High-severity advisories (`System.Text.Json`, `System.Formats.Asn1`, `Microsoft.Extensions.Caching.Memory`) plus two lower ones, all pulled in by EF Core 8.0.8 / SqlClient 5.1.5. Fix: upgrade EF Core.
- **Development defaults are not rejected at startup:** the salt `dev-only-change-me` and the `sa` connection string ship in `appsettings.json`. Override `Analytics:Salt`, `ConnectionStrings__Default` and `MSSQL_SA_PASSWORD` in any shared or real environment.
- **Swagger UI is served in every environment.**

## AI-assisted engineering

The goal is responsible AI-assisted engineering, not blind generation: AI proposes, the engineer reviews and decides, tests and reviewer agents validate, and every decision is written down.

### Workflow

```
Requirement → spec (acceptance criteria)            ⏸ engineer approves
            → plan (files, steps, risks)             ⏸ engineer approves
            → tests written first (test-author)
            → implementation
            → full test run (test-runner)
            → quality review ∥ security review       → findings fixed or explained
            → close: specs and plan marked Implemented
```

Project-specific skills (`.claude/skills/`) encode each step; reviewer and test agents (`.claude/agents/`) have narrow tools, and the reviewers are read-only. The workflow and the rubrics used to accept each artifact are in [`docs/PLAN.md`](docs/PLAN.md). Plans are numbered in build order (001–006 greenfield build, 007–014 brownfield changes, 015 documentation); see the [plans index](docs/plans/README.md).

### AI Collaboration Log

The project was built from an empty repository with agentic coding, one phase at a time, using the gated workflow above. Every phase has a spec (`docs/specs/`) and a plan (`docs/plans/`; index in [`docs/plans/README.md`](docs/plans/README.md)); nothing was implemented before its plan was approved.

| # | Phase | Plans and specs | How AI was used | Engineer's role |
|---|-------|-----------------|-----------------|-----------------|
| 0 | Setup | [`CLAUDE.md`](CLAUDE.md), [`docs/PLAN.md`](docs/PLAN.md), `.claude/` | Drafted the project instructions, the skills (`feature-workflow`, `write-spec`, `write-plan`, `add-api-endpoint`, `ef-schema-change`, `run-tests`) and four subagents with narrow tools (test author, test runner, read-only quality and security reviewers). | Set the rules the agents work under: approval gates, acceptance rubrics, "tests fail first", reviewers never edit. |
| 1 | Requirements | specs [001](docs/specs/001-create-short-url.md)–[006](docs/specs/006-analytics-reports.md) | Analysed the assessment brief for ambiguities and edge cases, then drafted each spec: API contract, behaviour rules, failure modes and Given/When/Then acceptance criteria. Every AC without a test was marked **GAP**. | Answered the open questions, fixed the scope (no authentication, no custom domains, no QR codes) and approved each spec before planning. |
| 2 | Database design | [plan 001](docs/plans/001-database-design.md) | Proposed the two-table model, keys, indexes and cascade, and wrote the EF configurations and initial migration. | Reviewed keys and indexes against the queries they serve: unique case-sensitive `Code`, non-unique URL hash, composite `(ShortUrlId, ClickedAt)`. |
| 3 | API surface | [plan 002](docs/plans/002-api-generation.md) | Generated controllers, DTOs, validators, RFC 7807 error mapping and Swagger metadata. | Added the `CodeSegment` route constraint, chose 302 over 301 and the 200-versus-201 duplicate behaviour, and approved the status-code matrix. |
| 4 | Short-code algorithm | [plan 003](docs/plans/003-short-code-algorithm.md) | Compared random strings, GUIDs, hash truncation and Base62 of the identity on length, collisions and retry cost. | Chose Base62 of the identity (`Id + 62^5`) and accepted sequential, guessable codes as a documented trade-off. |
| 5 | Persistence | [plan 004](docs/plans/004-persistence-layer.md) | Wrote the repository, duplicate detection, the placeholder-then-code transaction and `RowVersion` concurrency. | Required one transaction for create, optimistic locking surfaced as 409, and no retries around request-path writes. |
| 6 | Redis cache | [plan 005](docs/plans/005-redis-cache.md), spec [004](docs/specs/004-redirect-cache.md) | Wrote the cache-aside decorator and the circuit breaker, plus unit tests with a fake clock. | Decided TTL is capped at the link's remaining lifetime, 404/410 are never cached, and Redis failures fall back to SQL. |
| 7 | Click analytics | [plan 006](docs/plans/006-click-analytics.md), specs [005](docs/specs/005-click-tracking.md), [006](docs/specs/006-analytics-reports.md) | Compared writing inline, fire-and-forget, a broker and a bounded queue with a background writer; implemented the chosen design and the report endpoints. | Chose asynchronous processing and accepted at-most-once delivery; required salted hashing so no raw IP is stored. |
| 8 | Hardening: correctness and coverage | plans [007](docs/plans/007-utc-expires-at.md)–[010](docs/plans/010-rate-limit-coverage-gaps.md) | Closed the **GAP**s with tests (click-writer failure paths, delete cascade, every rate-limited endpoint) and fixed the UTC bug at the data boundary, tests first. | Required each new test to fail for the stated reason before the fix, and rejected weakened assertions. |
| 9 | Hardening: resilience, observability, defaults | plans [011](docs/plans/011-polly-resilience.md)–[014](docs/plans/014-test-coverage.md), specs [009](docs/specs/009-observability.md), [010](docs/specs/010-resilience.md) | Added Polly pipelines, metrics and traces over OTLP, the Redis health check, rate limiting on by default, and Redis integration and load tests. | Chose OpenTelemetry over a prerelease Prometheus exporter, decided which operations are never retried, and pinned image tags and package versions. |
| 10 | Review (every phase) | quality and security reviewers | Two read-only agents reviewed each change: quality (correctness, readability, architecture, performance) and security (threat model, abuse paths). | Verified the claims that mattered against the code (for example, measured the SQL error number for a refused connection) and accepted, rejected or deferred each finding (see below). |
| 11 | Documentation | [plan 015](docs/plans/015-documentation.md) | Drafted the README, architecture, ER review and the three scenario write-ups; a script verifies every cited test exists. | Edited each claim against the code and wrote the limitations as found. |

### Prompts, review decisions and what was accepted

Prompts for each assessment task, written against the stated constraints (.NET 8, SQL Server, EF Core, Redis, Polly, FluentValidation, Serilog). Each "Review decision" matches what is in the code today.

| # | Task | Prompt (engineer) | Review decision |
|---|------|-------------------|-----------------|
| 1 | Requirements | *"Here is the assessment brief for a production-ready URL shortener on .NET 8 / SQL Server / EF Core / Redis. List the ambiguities, edge cases and missing details before we design anything."* | Accepted the edge cases (expiry, duplicates, deleted links with clicks); fixed scope: no authentication, no custom domains, no QR codes, all stated as limitations. |
| 2 | Database design | *"Propose an ER diagram and EF Core entities for short links and click events. Justify keys, indexes and cascade behaviour for redirect-by-code and per-link reporting."* | Identity PK plus unique index on `Code`; **non-unique** index on the URL hash for duplicate detection (an expired link not yet cleaned up must not block re-shortening); index on `ExpiresAt` for the cleanup scan; cascade delete to `ClickEvents`; `rowversion` for concurrency. |
| 3 | API generation | *"Generate controllers, request/response DTOs and Swagger metadata for POST/PUT/DELETE `/api/v1/shorten` and `GET /{code}`. Use RFC 7807 errors and FluentValidation, and keep controllers thin."* | Added `ApiRoutes.CodeSegment` route constraint, explicit `ValidateAndThrowAsync`, status-code matrix (400/404/409/410/413/415/429). |
| 4 | Short-code algorithm | *"Compare Base62 of a database id, GUID, hash truncation and random codes for a short URL: length, collisions, guessability, retry cost."* | **Base62 of the identity** (`Id + 62^5`, 6+ characters, unique by construction, no retry loop); sequential codes accepted as a documented trade-off. |
| 5 | Persistence | *"Write the repository for creating a link: insert, derive the code, and commit atomically. Show how optimistic concurrency should surface to the caller on update."* | Insert with placeholder then set code in one transaction; `DbUpdateConcurrencyException` mapped to 409; no retries around request-path writes. |
| 6 | Redis cache | *"Add cache-aside to the redirect path as a decorator. Cover invalidation on update/delete, TTL, and what happens when Redis is down."* | TTL capped at the link's remaining lifetime; 404/410 never cached; Redis failures logged and bypassed by a circuit breaker; health reported Degraded, not Unhealthy. |
| 7 | Analytics | *"Design click tracking that adds no database write to the redirect. Define unique visitors without storing raw IPs."* | Bounded in-memory channel plus batching background writer; salted hash of IP and user agent; at-most-once delivery accepted. |
| 8 | Reliability | *"Add Polly retries for transient SQL errors, rate limiting, global exception handling, health checks, Serilog and metrics. State which operations must never be retried."* | Three pipelines (startup, background, time-budgeted request reads); writes never retried; OpenTelemetry over OTLP instead of a prerelease Prometheus exporter. |
| 9 | Brownfield | *"Given the basic shortener, produce a dependency and impact analysis for adding analytics, expiration, Redis and rate limiting, then the migration scripts and regression tests."* | Changes ordered by risk; schema changes shipped as EF migrations plus `docs/schema.sql`; regression suite kept green at each step. |
| 10 | Ambiguous requirement | *"Product says 'users want better reporting'. Generate clarifying questions, then turn the answers (daily clicks, country, browser) into an API design."* | Three read-only report endpoints on a separate service, so the redirect path is untouched; countries as ISO codes; no export. |
| 11 | Tests | *"Write xUnit tests with FluentAssertions and Moq for the Base62 encoder, URL service, validators and analytics. Add integration tests against real SQL Server and security cases for SQL injection, XSS and expired links."* | Added the missing negative and boundary cases, required new tests to fail first, rejected assertions weakened to pass. |
| 12 | Documentation | *"Draft the README: architecture, request flow, trade-offs, limitations. Don't claim anything the code does not do."* | Edited every claim against the code; limitations written as found, including the vulnerable transitive packages. |

Review findings and what was decided:

| Proposal / finding | Decision | Reason |
|--------------------|----------|--------|
| Reviewer: `expiresAt` loses its `Z` on 200 responses | **Accepted**: fixed once at the data boundary (UTC converter, `docs/plans/007-utc-expires-at.md`) | Patching `ToResponse` would have left `CreatedAt`, click times and the cached value wrong. |
| Reviewer: request-path retries have no total time limit | **Accepted**: added a 3 s budget (`RequestRead`) and corrected the spec's "~1.4 s" claim | Each attempt could wait out a 30 s SQL timeout. |
| Reviewer: "connection refused" may not count as transient | **Verified, then accepted**: probed SqlClient against a closed port, got error 10061, added it | Without it a stopped SQL Server brings back the 100-row insert fallback. |
| Reviewer: add a positive control to the "no click on 404/410" unit tests | **Accepted** | The tracker swallows exceptions, so a "never enqueued" assertion could pass vacuously. |
| Reviewer: `/health` logs a stack trace on every call while Redis is down | **Accepted**: Degraded result carries no exception | `/health` is public. |
| Reviewer: traces store user agents and paths | **Accepted** in part: user agent removed, `/health` and `/swagger` filtered; paths remain and are documented | Short codes are the thing the service handles; the limitation is stated in spec 009. |
| Security: unauthenticated update/delete is a Blocker | **Documented, not fixed** | The assessment lists "authentication not implemented" as an expected limitation. |
| Security: compose publishes Redis and SQL Server on all interfaces | **Left unchanged on the engineer's instruction** | Recorded as a known limitation. |
| Newest Aspire dashboard tag (13.6.x) | **Rejected**: pinned 13.5.2 | 13.6.x was under two weeks old. |
| Prometheus `/metrics` endpoint | **Rejected** | Exporter is prerelease; OTLP packages are stable. |

### Lessons learned

- **Specs make the gaps visible.** Writing acceptance criteria exposed behaviours with no test, including the click writer's failure path and every non-POST rate limit; they were marked **GAP** in the specs and have since been closed with tests (no spec has a GAP now).
- **Watch the test fail first.** Seeing a new test fail for the stated reason is the only proof it can detect the bug it was written for (done for the UTC fix and the rate-limit default).
- **Reviewers earn their keep on second-order effects:** the retry-amplification path, the stale-cache race, the log-flooding health check. None of these appear in a happy-path test run.
- **Verify, don't trust.** Package versions, image tags, SQL error numbers and cited tests were each checked against a real source before they went into a document.
- **A failing check beats a comment.** A unit test now fails if the shipped config disables rate limiting, and a script fails if a spec cites a missing test.
