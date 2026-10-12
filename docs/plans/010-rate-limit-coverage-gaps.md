# Plan 010 — Prove every write and read endpoint is rate limited

| | |
|---|---|
| Status | Implemented |
| Spec | [007 — Rate limiting](../specs/007-rate-limiting.md) |
| ACs satisfied / added | `RLM-AC-08`, `RLM-AC-09` — acceptance criteria that previously had no test |

## Goal

Today only `POST /api/v1/shorten` (write) and `GET /{code}` (read) are proven to return 429. This plan proves the rest:
- **RLM-AC-08:** the analytics endpoints share the `read` policy.
- **RLM-AC-09:** `PUT` and `DELETE` share the `write` policy.

## Out of scope

- RLM-AC-10 (read and write budgets are independent), which is P3.
- Changing the shipped rate-limit defaults (specified in spec 007).

## Changes

| File | Change | Why |
|------|--------|-----|
| `tests/IntegrationTests/RateLimitIntegrationTests.cs` | Add 2 tests. Each creates **its own** `LimitedApiFactory` (`using var factory = new LimitedApiFactory();`) instead of using the class fixture. | Limiter state lives inside each app instance. Within the class fixture, every test spends the same budget (write 3, read 2 per 60 s), so new tests would interfere with the 2 existing ones and with each other. |
| `docs/specs/007-rate-limiting.md` | Replace **GAP** for AC-08 and AC-09 with test names. | Traceability. |

## Steps

1. **RLM-AC-08.** With a fresh `LimitedApiFactory` (read limit 2), call `GET /api/v1/analytics/zzzzzz9` → 404, then `GET /api/v1/analytics/zzzzzz9/daily` → 404, then `GET /api/v1/analytics/zzzzzz9/countries` → **429** with `Retry-After` and `application/problem+json`. A 404 still spends a permit. The three different routes show the analytics actions share one `read` budget. — verify: `dotnet test --filter "FullyQualifiedName~RateLimitIntegrationTests"`
2. **RLM-AC-09.** With a fresh `LimitedApiFactory` (write limit 3): `PUT /api/v1/shorten/zzzzzz9` → 404, `DELETE` → 404, `PUT` → 404, then `DELETE` → **429** with `Retry-After`. Unknown codes are enough because rejected 404s still spend permits, so the test doesn't need to create links, which would themselves spend write permits. — verify: same filter.
3. **Full suite.** — verify: test-runner, full suite. The existing `Writes_AfterLimit_Return429WithRetryAfter` and `Reads_AfterLimit_Return429_AndHealthIsNeverLimited` must still pass.
4. **Review** with both reviewers, then update spec 007's Tests column and run `check-specs.sh`.

## Test strategy

| AC | Level | Test (Class.Method) | Notes |
|----|-------|---------------------|-------|
| RLM-AC-08 | integration | `RateLimitIntegrationTests.AnalyticsEndpoints_ShareReadLimit_Return429` | Covers summary, daily and countries. `browsers` uses the same controller-level attribute, so it's covered by construction. |
| RLM-AC-09 | integration | `RateLimitIntegrationTests.PutAndDelete_ShareWriteLimit_Return429` | Alternates PUT and DELETE so both verbs are shown to spend the same budget and to be rejected. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| **Per-test factories cost about 1 s of host start-up each.** | Two extra hosts is acceptable. They also make the tests order-independent, unlike sharing the fixture. |
| **Partitioning:** under `TestServer`, `RemoteIpAddress` is null, so every request lands in the `"unknown"` partition. | That is still one bucket per factory, which is exactly what these tests need. Partitioning by IP is covered by unit tests (`RateLimitPolicyTests`). |
| **Window expiry mid-test:** the limit window is 60 s. | Each test makes 3–4 requests in well under a second. |

Rollback: remove the 2 tests.
