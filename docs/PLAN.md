# Engineering workflow and plan

How changes are made to this repository: specs define behaviour, plans define changes, skills encode repo-specific procedures, and subagents review and test. The workflow keeps an engineer in the loop at every approval gate.

## Workflow

Every feature or behaviour change follows the same gated loop. ⏸ = stop for human approval.

```
1. Spec        write or update docs/specs/NNN-*.md            ⏸ status Draft → Approved
2. Plan        write docs/plans/NNN-<slug>.md from the spec    ⏸ approve the plan
3. Tests       test-author: test matrix from ACs, then xUnit code (new behaviour: shown failing first)
4. Build       implement the plan
5. Verify      test-runner: build + run tests, report results verbatim
6. Review      five-axis staff review: code-quality-reviewer (correctness, readability and simplicity,
                architecture, performance) ∥ security-reviewer (security)
7. Close       fix Blocker/Major → re-run step 5 → spec status Implemented
```

Small fixes (no behaviour change) may skip steps 1–2 but never 5–6.

| Piece | Where |
|-------|-------|
| Skills (procedures) | `.claude/skills/`: `feature-workflow`, `write-spec`, `write-plan`, `add-api-endpoint`, `ef-schema-change`, `run-tests` |
| Agents (narrow tools) | `.claude/agents/`: `test-author` (writes tests), `test-runner` (runs them), `code-quality-reviewer` and `security-reviewer` (read-only; together they perform the five-axis review) |
| Plan index (greenfield → brownfield) | [`docs/plans/README.md`](plans/README.md) |
| Project conventions | [`CLAUDE.md`](../CLAUDE.md) |
| Traceability check | `bash .claude/skills/write-spec/check-specs.sh` (every cited test must exist) |

## Feature inventory

Every acceptance criterion (AC) is traced to tests. A spec is `Implemented` only when none is marked **GAP**.

| Spec | Feature | ACs | Without a test |
|------|---------|-----|----------------|
| [001](specs/001-create-short-url.md) | Create short URL | 17 | 0 |
| [002](specs/002-redirect.md) | Redirect | 11 | 0 |
| [003](specs/003-update-delete-link.md) | Update and delete | 15 | 0 |
| [004](specs/004-redirect-cache.md) | Redirect cache | 19 | 0 |
| [005](specs/005-click-tracking.md) | Click tracking | 11 | 0 |
| [006](specs/006-analytics-reports.md) | Analytics reports | 15 | 0 |
| [007](specs/007-rate-limiting.md) | Rate limiting | 12 | 0 |
| [008](specs/008-expired-link-cleanup.md) | Expired link cleanup | 10 | 0 |
| [009](specs/009-observability.md) | Observability | 10 | 0 |
| [010](specs/010-resilience.md) | Resilience | 9 | 0 |

Test suite: 379 tests (261 unit, 118 integration) pass with Redis enabled; without `URLSHORTENER_TEST_REDIS` the two tests that need a real Redis are skipped.

## Known limitations

Maintained in the [README](../README.md#limitations) and in each spec's *Known limitations* section.

## Requirement deliverables checklist

From the assessment's *Final Deliverables Checklist*.

| Deliverable | Evidence |
|-------------|----------|
| .NET 8 URL shortener service | `src/` |
| ASP.NET Core REST APIs | `src/Controllers/`; specs 001–003, 006 |
| SQL Server / PostgreSQL schema | `src/Migrations/`, `docs/schema.sql`, `docs/schema.generated.sql`, `docs/ER-Diagram.md` |
| Entity Framework Core implementation | `src/Repositories/`, `src/Configurations/` |
| Redis caching | `src/Caching/`; spec 004; `RedisCacheTests` against a real Redis |
| Analytics module | `src/Analytics/`, `ReportService`; specs 005, 006 |
| Unit and integration tests | `tests/UnitTests`, `tests/IntegrationTests`; performance: `tests/PerformanceTests` |
| Swagger documentation | `/swagger` (`[ProducesResponseType]` on actions) |
| Docker support | `docker/Dockerfile`, `docker-compose.yml` |
| Architecture diagram | `docs/architecture.md` |
| AI-assisted engineering workflow documentation | `README.md` (AI section), this file, `.claude/skills/`, `.claude/agents/` |
| Greenfield, brownfield and ambiguous-requirement scenarios | `docs/scenarios/` |
| README with AI prompts, review decisions, trade-offs, limitations, lessons learned | `README.md` |

## Rubric

Used to accept every artifact this workflow produces. An artifact passes only if every item holds.

**A. Spec**
1. Every behaviour claim cites a file and symbol, or a test; nothing invented. Unknowns are resolved before approval.
2. API contract lists every status code, including 400/404/409/410/415/429 where applicable.
3. ACs have permanent IDs, use Given/When/Then, and are individually testable.
4. Every AC lists exact test names (`Class.Method`) or **GAP**; every listed test exists.
5. Failure modes covered where relevant (Redis down, queue full, concurrent update, expiry boundary).
6. Configuration keys listed with real defaults.

**B. Plan**
1. Links the spec and the ACs it satisfies or adds.
2. Lists every file to change and why.
3. Ordered steps, each independently verifiable.
4. Test strategy per AC (unit vs integration).
5. Risks and rollback (mandatory for migrations).
6. Docs to update (`docs/schema.sql`, spec, `CLAUDE.md`).
7. Ends with an approval gate.

**C. Skill**
1. `description` states when to use and when not to, with trigger phrases.
2. Only real paths and commands from this repo.
3. Ends with a verification step.
4. Links `CLAUDE.md` / specs rather than restating them; no generic advice.
5. ≤ ~150 lines.

**D. Subagent**
1. Single responsibility; least-privilege tools; edit scope stated.
2. Explicit inputs and a fixed output format.
3. Project-specific checklist (not generic advice).
4. Every finding cites `file:line` and a concrete failure scenario, and is re-checked before reporting.
5. Reports failures and skipped steps honestly (especially `test-runner`).

**E. Review severity** (both reviewers)

| Level | Meaning | Verdict |
|-------|---------|---------|
| Blocker | Wrong behaviour, data loss, or exploitable vulnerability | Request changes |
| Major | Convention violation likely to cause bugs, or an AC without a test | Request changes |
| Minor | Maintainability / clarity | Approve with comments |
| Nit | Style | Approve |
