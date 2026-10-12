# Feature specs

One spec per feature. A spec is the contract: what the feature does, how it fails, and how we know it works.
Plans (`docs/plans/`) describe *how* a change is made; specs describe *what* the system must do.

## Index

| ID | Spec | AC prefix | Status |
|----|------|-----------|--------|
| 001 | [Create short URL](001-create-short-url.md) | `CRT` | Implemented |
| 002 | [Redirect](002-redirect.md) | `RED` | Implemented |
| 003 | [Update and delete a link](003-update-delete-link.md) | `UPD`, `DEL` | Implemented |
| 004 | [Redirect cache](004-redirect-cache.md) | `CCH` | Implemented |
| 005 | [Click tracking](005-click-tracking.md) | `CLK` | Implemented |
| 006 | [Analytics reports](006-analytics-reports.md) | `RPT` | Implemented |
| 007 | [Rate limiting](007-rate-limiting.md) | `RLM` | Implemented |
| 008 | [Expired link cleanup](008-expired-link-cleanup.md) | `CLN` | Implemented |
| 009 | [Observability](009-observability.md) | `OBS` | Implemented |
| 010 | [Resilience](010-resilience.md) | `RSL` | Implemented |

## Status lifecycle

`Draft` → `Approved` (human sign-off; implementation may start) → `Implemented` (every acceptance criterion has a passing test) → `Deprecated`

## Conventions

- **Acceptance-criteria IDs are permanent.** Never renumber; retire an AC by striking it through with a reason.
- **Every AC maps to tests.** Test names are written exactly as they appear in code so they can be searched. An AC without a test is marked **GAP** and keeps its spec out of `Implemented`.
- **Every behaviour claim cites code** by file and symbol (for example `src/Caching/CachedUrlShortenerService.cs`, `ResolveAsync`) or by a test. Line numbers are not used: they go stale on every edit.
- **No invented requirements.** If the code's intent is unclear, record it as an open question in a `Draft` spec and resolve it before the spec is approved. Approved and implemented specs have none.
- Times are UTC. Status codes are listed exhaustively, including 429 and 415 where they apply.
- Verify traceability with `bash .claude/skills/write-spec/check-specs.sh` (every cited test must exist; `--unmapped` also lists tests no spec cites).

## Template

Copy this when adding a spec (`NNN-kebab-name.md`, next free number):

```markdown
# NNN — <Feature name>

| | |
|---|---|
| Status | Draft |
| AC prefix | `XXX` |
| Last verified | YYYY-MM-DD |
| Related specs | 00N |
| Plans | [NNN](../plans/NNN-slug.md) |

## Purpose
One paragraph: what the feature is for and who calls it.

## API contract
Method, route, request body, response body, and a table of every status code with when it occurs.

## Behaviour
Numbered rules, each citing the file and symbol that implements it.

## Edge cases and failure modes
What happens when inputs are at limits or a dependency (SQL, Redis, queue) fails.

## Configuration
| Key | Default | Effect |

## Acceptance criteria
| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| XXX-AC-01 | Given …, when …, then … | `TestClass.TestMethod` or **GAP** |

## Known limitations
Deliberate trade-offs and their consequence.

## Open questions
(Draft specs only.) Anything that needs a human decision; remove once answered and record the answer in Behaviour.
```
