# Plans

One plan per change, numbered in the order the work was done. A plan says *how* a change is made; the spec it links to says *what* the system must do (see [`docs/specs/`](../specs/README.md)). Plan numbers are independent of spec numbers. Use [`_TEMPLATE.md`](_TEMPLATE.md) for new plans.

## Index

### Greenfield: building the system (scenario: [greenfield](../scenarios/greenfield.md))

| Plan | Task | Specs |
|------|------|-------|
| [001](001-database-design.md) | Database design | [001](../specs/001-create-short-url.md), [003](../specs/003-update-delete-link.md), [005](../specs/005-click-tracking.md) |
| [002](002-api-generation.md) | API surface | [001](../specs/001-create-short-url.md), [002](../specs/002-redirect.md), [003](../specs/003-update-delete-link.md) |
| [003](003-short-code-algorithm.md) | Short-code algorithm | [001](../specs/001-create-short-url.md) |
| [004](004-persistence-layer.md) | Persistence layer | [001](../specs/001-create-short-url.md), [003](../specs/003-update-delete-link.md) |
| [005](005-redis-cache.md) | Redis redirect cache | [002](../specs/002-redirect.md), [004](../specs/004-redirect-cache.md) |
| [006](006-click-analytics.md) | Click analytics | [005](../specs/005-click-tracking.md), [006](../specs/006-analytics-reports.md) |

### Brownfield: changing the running system (scenario: [brownfield](../scenarios/brownfield.md))

| Plan | Change | Specs |
|------|--------|-------|
| [007](007-utc-expires-at.md) | `expiresAt` returned as UTC | [001](../specs/001-create-short-url.md), [003](../specs/003-update-delete-link.md) |
| [008](008-click-pipeline-gaps.md) | Click pipeline failure-path tests | [002](../specs/002-redirect.md), [005](../specs/005-click-tracking.md) |
| [009](009-delete-cascade-gaps.md) | Delete removes click history | [003](../specs/003-update-delete-link.md), [008](../specs/008-expired-link-cleanup.md) |
| [010](010-rate-limit-coverage-gaps.md) | Rate-limit coverage | [007](../specs/007-rate-limiting.md) |
| [011](011-polly-resilience.md) | Polly retry policies | [005](../specs/005-click-tracking.md), [010](../specs/010-resilience.md) |
| [012](012-observability.md) | Metrics, monitoring, Redis health check | [009](../specs/009-observability.md) |
| [013](013-rate-limit-default-on.md) | Rate limiting on by default | [007](../specs/007-rate-limiting.md) |
| [014](014-test-coverage.md) | Redis integration and database load tests | [004](../specs/004-redirect-cache.md) |

### Documentation

| Plan | Change |
|------|--------|
| [015](015-documentation.md) | README, architecture, ER diagram, scenario write-ups |

## Next number

The next free number is **016**. Add the plan to this index when you create it.
