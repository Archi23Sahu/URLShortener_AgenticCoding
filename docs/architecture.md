# Architecture

A single ASP.NET Core 8 Web API backed by SQL Server (EF Core 8), with Redis as a redirect cache, an in-process
queue + background worker for click analytics, a background cleanup job, and OpenTelemetry for metrics and traces.
Behaviour is specified per feature in [`docs/specs/`](specs/README.md); the schema is in [`ER-Diagram.md`](ER-Diagram.md).

## Component diagram

```mermaid
flowchart TB
    client([Client / browser])

    subgraph api["ASP.NET Core API (src/)"]
        direction TB
        mw["ExceptionHandlingMiddleware<br/>→ RFC 7807 problem+json"]
        rl["Rate limiter<br/>per-client fixed window (per IPv4 address, per /64 for IPv6): read / write"]
        subgraph ctrl["Controllers"]
            shorten["ShortenController<br/>POST · PUT · DELETE /api/v1/shorten"]
            redirect["RedirectController<br/>GET /{code}"]
            analytics["AnalyticsController<br/>GET /api/v1/analytics/{code}[/daily|/countries|/browsers]"]
        end
        health["/health<br/>database + redis checks"]

        subgraph svc["Service layer"]
            validation["FluentValidation<br/>Create/Update validators"]
            cached["CachedUrlShortenerService<br/>(cache-aside decorator + CacheHealth breaker)"]
            core["UrlShortenerService<br/>normalize · dedupe · Base62 · expiry"]
            reports["ReportService"]
            tracker["ClickTracker<br/>hash visitor · country · browser"]
        end

        queue[["ChannelClickQueue<br/>bounded, in memory"]]
        resilience["SqlResilience (Polly)<br/>Startup · Transient · RequestRead"]
        repo["ShortUrlRepository<br/>EF Core AppDbContext"]
        metrics["UrlShortenerMetrics<br/>Meter 'UrlShortener'"]

        subgraph bg["Background services"]
            writer["ClickRecorderBackgroundService<br/>batch insert via IClickEventWriter"]
            cleanup["ExpiredLinkCleanupService<br/>→ ExpiredLinkCleaner"]
        end
    end

    redis[("Redis<br/>link:{code} → id, url, expiry")]
    sql[("SQL Server<br/>ShortUrls · ClickEvents")]
    otel["Aspire dashboard<br/>(OTLP: metrics + traces)"]

    client --> mw --> rl --> ctrl
    shorten --> validation
    shorten --> cached
    redirect --> cached
    redirect --> tracker
    analytics --> cached
    analytics --> reports
    cached <--> redis
    cached --> core
    core --> resilience --> repo
    reports --> repo
    tracker --> queue --> writer
    writer --> resilience
    cleanup --> repo
    repo --> sql
    health --> sql
    health --> redis
    metrics -.-> otel
```

Key decisions behind the diagram (details in the README's *Trade-offs*):
- **Decorator for caching.** `IUrlShortenerService` resolves to `CachedUrlShortenerService`, which wraps `UrlShortenerService`. Callers can't bypass invalidation, and the core service knows nothing about Redis.
- **Reporting is separate.** `ReportService` reads the repository directly, so reporting changes never touch the redirect path.
- **Analytics are asynchronous.** The redirect only enqueues; a background worker writes in batches. Redirect latency is independent of the database's write load.

## Request flows

### Create a short URL

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant API as ShortenController
    participant V as Validator
    participant S as UrlShortenerService
    participant DB as SQL Server
    C->>API: POST /api/v1/shorten {url, expiryDays?}
    API->>V: ValidateAndThrowAsync
    V-->>API: ok (or 400 problem+json)
    API->>S: CreateAsync
    S->>S: normalize URL, SHA-256 hash
    S->>DB: active link with same hash + URL?
    alt duplicate found
        DB-->>S: existing row
        S-->>API: Created = false
        API-->>C: 200 {shortUrl, expiresAt}
    else new
        S->>DB: BEGIN TRAN · INSERT (placeholder code) · UPDATE Code = Base62(Id + 62^5) · COMMIT
        S-->>API: Created = true
        API-->>C: 201 Location: shortUrl
    end
```

### Redirect (cache hit and miss)

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant R as RedirectController
    participant Ca as CachedUrlShortenerService
    participant Re as Redis
    participant S as UrlShortenerService
    participant DB as SQL Server
    participant T as ClickTracker
    participant Q as ClickQueue
    C->>R: GET /{code}
    R->>Ca: ResolveAsync(code)
    alt breaker closed
        Ca->>Re: GET link:{code}
    end
    alt hit and not expired
        Re-->>Ca: {id, url, expiresAt}
    else miss / Redis error / breaker open
        Ca->>S: ResolveAsync(code)
        S->>DB: SELECT by Code (Polly RequestRead: transient retries, 3 s budget)
        alt not found / expired
            S-->>R: NotFoundException / LinkExpiredException
            R-->>C: 404 / 410 problem+json (no click recorded)
        end
        S-->>Ca: link
        Ca->>Re: SET link:{code} TTL = min(10 min, time to expiry)
    end
    Ca-->>R: link
    R->>T: Track(context, id)  (no I/O, never throws)
    T->>Q: TryEnqueue(click)  (dropped if full)
    R-->>C: 302 Location: target
```

### Click recording (background)

```mermaid
sequenceDiagram
    autonumber
    participant Q as ClickQueue
    participant W as ClickRecorderBackgroundService
    participant P as Polly Transient
    participant DB as SQL Server
    loop until shutdown
        W->>Q: wait for first click, then collect ≤ 100 clicks or 2 s
        W->>P: insert batch (fresh DbContext)
        P->>DB: INSERT ClickEvents (batch)
        alt transient error
            P->>DB: retry ≤ 3 (exponential, jitter)
            Note over W: still failing → drop batch, one error log
        else non-transient (e.g. link deleted → FK violation)
            W->>DB: insert row by row; drop only the bad click
            Note over W: a transient error here drops the rest of the batch
        end
    end
    Note over W: on shutdown the writer drains what is queued, within the host's 30 s timeout
```

### Analytics report

```mermaid
sequenceDiagram
    participant C as Client
    participant A as AnalyticsController
    participant Rs as ReportService
    participant DB as SQL Server
    C->>A: GET /api/v1/analytics/{code}/daily?from&to
    A->>Rs: GetDailyAsync
    Rs->>DB: link by code (404 if missing)
    Rs->>DB: clicks + distinct visitors per UTC day in [from, to+1)
    Rs-->>A: zero-filled day list
    A-->>C: 200 JSON
```

### Expired-link cleanup

`ExpiredLinkCleanupService` waits 30 s after start, then every `Cleanup:IntervalMinutes` (60) calls `ExpiredLinkCleaner.RunOnceAsync`. That deletes links whose `ExpiresAt` is older than `Cleanup:GracePeriodDays` (7), in batches of 1000; their clicks cascade. Until then an expired link answers **410**; afterwards **404**.

## Cross-cutting concerns

| Concern | Where | Spec |
|---------|-------|------|
| Validation | FluentValidation in `src/Validators/`, called explicitly in controllers | 001, 003 |
| Error handling | `ExceptionHandlingMiddleware` maps domain exceptions to 400/404/409/410, hides internals on 500 | 002 |
| Rate limiting | `RateLimitingSetup`: per-client fixed window (per IPv4 address, per /64 for IPv6), `write` 20/min, `read` 300/min, **on** by default | 007 |
| Resilience | `SqlResilience` (Polly): startup migration retry, transient-only retries, request-path time budget | 010 |
| Caching | Redis cache-aside with TTL ≤ link lifetime and a 30 s circuit breaker; in-memory fallback without Redis | 004 |
| Observability | `UrlShortenerMetrics` + OpenTelemetry (ASP.NET Core, HttpClient, runtime), OTLP → Aspire dashboard; Serilog console logs | 009 |
| Health | `/health`: database (Unhealthy if down) + redis (Degraded if down) | 009 |
| Time | Injected `TimeProvider` everywhere; all timestamps UTC | — |

## Deployment (docker-compose)

```mermaid
flowchart LR
    subgraph compose["docker compose"]
        api["api :8080"]
        sqlserver[("sqlserver :1433")]
        redis[("redis :6379")]
        dash["aspire-dashboard<br/>UI 127.0.0.1:18888"]
    end
    api --> sqlserver
    api --> redis
    api -- "OTLP :18889 (internal)" --> dash
```

The API waits for SQL Server and Redis to be healthy, then applies migrations at startup (Polly retry). See the README Limitations for the fact that SQL Server and Redis ports are published on all interfaces in this development stack.
