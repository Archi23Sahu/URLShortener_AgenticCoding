# Plan 013 — Rate limiting on by default

| | |
|---|---|
| Status | Implemented |
| Spec | [007 — Rate limiting](../specs/007-rate-limiting.md) |
| ACs satisfied / added | `RLM-AC-11` (new) |
| Source | Requirement: production-ready rate limiting middleware (on by default). |

## Goal

Ship with rate limiting **enabled**, so a deployment can't run unthrottled by accident. Contexts that legitimately send heavy traffic from one IP (integration tests, load tests) turn it off explicitly.

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/appsettings.json` | `RateLimit:Enabled` `false` → `true`. | RLM-AC-11. Code default is already `true`. |
| `tests/IntegrationTests/ApiFactory.cs` | No change: it already sets `RateLimit:Enabled=false`. | Verify only. |
| `tests/PerformanceTests/Program.cs` | No code change; its header already tells you to run the API with `RateLimit__Enabled=false`. | Verify only. |
| `docker-compose.yml` | No change: the compose stack now runs **with** limiting (production-like). Comment documenting how to disable it for load tests. | Clarity. |
| `tests/UnitTests/RateLimiting/RateLimitPolicyTests.cs` | `ShippedConfig_EnablesRateLimiting`: bind `RateLimitOptions` from the `appsettings.json` copied to the test output. | RLM-AC-11. |

## Steps

1. **Test first (test-author):** `ShippedConfig_EnablesRateLimiting` fails today. — verify: test-runner filter.
2. Flip the setting. — verify: the new test passes.
3. Full suite (integration tests must be unaffected, because `ApiFactory` disables limiting) → reviewers → close.

## Test strategy

| AC | Level | Test | Notes |
|----|-------|------|-------|
| RLM-AC-11 | unit | `RateLimitPolicyTests.ShippedConfig_EnablesRateLimiting` | Loads `src/appsettings.json` from the test output directory (Web SDK copies it to referencing projects). If it isn't copied, link it into the test project as content. |

## Risks and rollback

| Risk | Mitigation |
|------|-----------|
| Local demos hit 429 sooner (20 writes/min per IP). | That is the intended behaviour; README explains `RateLimit__Enabled=false` for load tests. |
| Load test numbers become meaningless if someone forgets to disable it. | `PerformanceTests` prints a warning when it sees 429s (small addition, done in plan 014). |

Rollback: set the value back to `false`.
