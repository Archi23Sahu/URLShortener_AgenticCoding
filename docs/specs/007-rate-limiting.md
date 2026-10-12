# 007 — Rate limiting

| | |
|---|---|
| Status | Implemented |
| AC prefix | `RLM` |
| Last verified | 2026-10-12 |
| Related specs | 001, 002, 003, 006 (the endpoints it protects) |
| Plans | [010](../plans/010-rate-limit-coverage-gaps.md), [013](../plans/013-rate-limit-default-on.md) |

## Purpose

Caps requests per client IP so one client cannot exhaust the database (writes) or flood redirects and reports (reads).

## API contract

Two named policies, attached per controller with `[EnableRateLimiting]`:

| Policy | Applies to | Default limit |
|--------|-----------|---------------|
| `write` | `ShortenController`: POST, PUT, DELETE `/api/v1/shorten…` (`ShortenController.cs`) | 20 per 60 s |
| `read` | `RedirectController` `GET /{code}` and all `AnalyticsController` routes (`RedirectController.cs`, `AnalyticsController.cs`) | 300 per 60 s |

Not limited: `/health`, Swagger UI/JSON.

Rejection response (`src/RateLimiting/RateLimitingSetup.cs`): status **429**, `Content-Type: application/problem+json`, `Retry-After: <seconds, rounded up>`, body `{ "title": "Too many requests", "status": 429, "detail": "…", "traceId": "…" }`. A warning is logged with client IP, method and path.

## Behaviour

1. **Partition key** (`RateLimitPolicies.ClientKey`): the client's IPv4 address; for IPv6, the **/64 prefix** (a client normally controls a whole /64, so keying on the full address would give it a new bucket per request); an IPv4-mapped IPv6 address counts as its IPv4 address; `"unknown"` when there is no address.
2. Fixed window per partition; `QueueLimit = 0` (reject immediately, never wait); auto-replenish.
3. `PermitLimit < 1` and `WindowSeconds < 1` are clamped to 1.
4. `RateLimit:Enabled = false` → no limiter for any policy.
5. Options are read **per request** from `IOptions<RateLimitOptions>`, so test/config overrides apply without rebuilding the limiter.
6. `read` and `write` keep separate counters: a client's writes don't consume its read budget.
7. `UseRateLimiter()` runs after routing so endpoint attributes are visible, before controllers execute (`Program.cs`).

## Edge cases and failure modes

- Fixed window allows up to 2× the limit across a window boundary.
- Counters are in-process: N app instances ⇒ effective limit × N.
- Behind a reverse proxy without forwarded-headers middleware, every client shares the proxy's IP and therefore one bucket.

## Configuration

| Key | Code default | `appsettings.json` | Effect |
|-----|-------------|--------------------|--------|
| `RateLimit:Enabled` | `true` | `true` | Master switch. |
| `RateLimit:Write:PermitLimit` / `WindowSeconds` | `20` / `60` | `20` / `60` | `write` policy. |
| `RateLimit:Read:PermitLimit` / `WindowSeconds` | `300` / `60` | `300` / `60` | `read` policy. |

> **Note:** `src/appsettings.json` ships with `Enabled: true`, so limiting is **on** locally and in the compose stack (`appsettings.Development.json` does not override it). Integration tests (`ApiFactory`) and load tests turn it off explicitly (`RateLimit__Enabled=false`); `RateLimitPolicyTests.ShippedConfig_EnablesRateLimiting` fails if the shipped value is flipped back.

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| RLM-AC-01 | Given a write limit of 3, when a 4th POST arrives in the window, then 429 problem+json with `Retry-After` and title "Too many requests". | `RateLimitIntegrationTests.Writes_AfterLimit_Return429WithRetryAfter` |
| RLM-AC-02 | Given a read limit of 2, when a 3rd redirect arrives, then 429; `/health` is never limited. | `RateLimitIntegrationTests.Reads_AfterLimit_Return429_AndHealthIsNeverLimited` |
| RLM-AC-03 | A partition allows exactly `PermitLimit` requests, then rejects with retry-after metadata. | `RateLimitPolicyTests.Partition_AllowsUpToTheLimit_ThenRejects`, `RateLimitPolicyTests.Partition_RejectedLease_ReportsRetryAfter` |
| RLM-AC-04 | Different IPs get different partitions; the same IP gets the same one; no IP still gets a limiter. | `RateLimitPolicyTests.Partition_DifferentClients_GetDifferentPartitionKeys`, `RateLimitPolicyTests.Partition_SameClient_GetsTheSamePartitionKey`, `RateLimitPolicyTests.Partition_UnknownClientAddress_StillGetsALimiter` |
| RLM-AC-05 | Given `Enabled = false`, then requests are never rejected. | `RateLimitPolicyTests.Partition_Disabled_NeverRejects` |
| RLM-AC-06 | Given a non-positive `PermitLimit`, then it is treated as 1. | `RateLimitPolicyTests.Partition_NonPositiveLimit_IsTreatedAsOne` |
| RLM-AC-07 | Default write limits are stricter than read limits. | `RateLimitPolicyTests.Defaults_WritesAreStricterThanReads` |
| RLM-AC-08 | Given the read limit is exhausted, then analytics endpoints also return 429. | `RateLimitIntegrationTests.AnalyticsEndpoints_ShareReadLimit_Return429` |
| RLM-AC-09 | Given the write limit is exhausted, then PUT and DELETE also return 429. | `RateLimitIntegrationTests.PutAndDelete_ShareWriteLimit_Return429` |
| RLM-AC-10 | Given a client exhausts its write budget, then its reads still succeed (independent counters). | `RateLimitIntegrationTests.Reads_AfterWriteBudgetIsExhausted_AreNotRateLimited` |
| RLM-AC-11 | Given the shipped `src/appsettings.json`, then `RateLimit:Enabled` is `true` (limiting is on unless a test, load test or environment turns it off explicitly). | `RateLimitPolicyTests.ShippedConfig_EnablesRateLimiting` |
| RLM-AC-12 | Given IPv6 clients, when they share a /64, then they share one rate-limit partition; IPv4-mapped IPv6 addresses count as their IPv4 address. | `RateLimitPolicyTests.ClientKey_IPv6AddressesSharingA64_AreEqual`, `RateLimitPolicyTests.ClientKey_IPv6AddressesInDifferent64s_AreDifferent`, `RateLimitPolicyTests.ClientKey_IPv4MappedIPv6_EqualsTheIPv4Key`, `RateLimitPolicyTests.ClientKey_IPv4_IsTheAddress`, `RateLimitPolicyTests.ClientKey_NullAddress_IsUnknown`, `RateLimitPolicyTests.Partition_IPv6ClientRotatingWithinItsA64_SharesOneLimiter` |

## Known limitations

See *Edge cases*. No per-API-key or per-user limits; IP only.

- Behind a reverse proxy without forwarded-headers handling every visitor shares the proxy's IP for hashing and rate limiting; deployments must terminate at the app or add forwarded-headers support.
