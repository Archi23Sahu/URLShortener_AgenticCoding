# Plan 015 — Documentation deliverables

| | |
|---|---|
| Status | Implemented |
| Spec | All (001–010) — documents the system as built; no behaviour change |
| ACs satisfied / added | none (documentation) |
| Source | Requirement: README, architecture documentation, scenario write-ups. |

## Goal

Produce every documentation deliverable the requirement lists, accurate to the code **after** plans 011–014.

## Changes

| File | Content |
|------|---------|
| `README.md` *(new, repo root)* | Overview; tech stack; quick start (compose, local, tests incl. Redis tests, load tests); API reference with examples (all 5 endpoints + reports + `/health`); configuration table; reliability features (each linked to its spec); **Trade-offs** (Base62 vs GUID, Redis cache, async analytics, SQL Server); **Limitations** (the requirement's list plus the ones found here: sequential codes, unauthenticated update/delete, cache race, port exposure in compose); **AI Collaboration Log** (table: phase / how AI was used / engineer's role); **AI prompts and review decisions** (the gated workflow, with real examples from this repo: what the reviewers proposed and what was accepted or rejected); **Lessons learned**; links to `docs/`. |
| `docs/architecture.md` *(new)* | **Component diagram** (Mermaid): client → API → service layer → validation / cache / analytics → EF Core → SQL Server, plus background jobs and telemetry. **Request flows** (Mermaid sequence): create, redirect (hit/miss), click recording, report, cleanup. Cross-cutting: errors, rate limiting, resilience, observability. |
| `docs/ER-Diagram.md` | **Rewrite**: the current file describes tables that don't exist (`Urls`, `Clicks`, `IsActive`, `OwnerId`…). Mermaid `erDiagram` of `ShortUrls` and `ClickEvents` from `src/Configurations/*Configuration.cs`, plus column types, keys, indexes (with which query uses each), cascade, collation, and the PK/FK, index and query-performance review the requirement's Task 1 asks for. |
| `docs/scenarios/greenfield.md` *(new)* | Tasks 1–6 from the requirement, each mapped to the code and spec that delivers it, the engineer decision (Base62, async analytics…) and its validation (tests). The baseline is planned in plans 001–006 (one per task), which `greenfield.md` links to. |
| `docs/scenarios/brownfield.md` *(new)* | Dependency analysis → impact analysis → implementation → tests → review, using the real brownfield changes: the `AddBrowserToClickEvents` migration, and plans 007/011/012/013 (each touched existing code). Deliverables: updated architecture, migration scripts (`src/Migrations`, `docs/schema.generated.sql`), regression testing (suite totals per plan). |
| `docs/scenarios/ambiguous-requirement.md` *(new)* | "Users want better reporting" → the clarifying questions → the product owner's answer (daily, country, browser) → spec 006 → API design → implementation → validation. |
| `CLAUDE.md` | Point to README and `docs/architecture.md`. |

## Greenfield scenario

- **Goal:** `docs/scenarios/greenfield.md` answers "build a URL shortener from scratch" (Tasks 1–6) using only evidence in the repository.
- **Sources:** code under `src/`, specs 001–006, the tests they cite, `docs/ER-Diagram.md`, `docs/schema.sql`.
- **Baseline plans:** [001](001-database-design.md)–[006](006-click-analytics.md), one per requirement task, written in build order. Brownfield plans 007–014 follow them, and this documentation plan is last.
- **Verification:** each path, class and test name in `greenfield.md` and plans 001–006 are checked against the repository; `check-specs.sh` still exits 0.

## Steps

1. After plans 011–014 are closed: write `docs/ER-Diagram.md` and `docs/architecture.md`; check every table, index and flow against the code (`grep` each name).
2. Scenario write-ups (greenfield baseline planned in plans 001–006).
3. README.
4. **Review:** code-quality-reviewer checks for accuracy (every claim checked against code and specs, and every command actually runs); security-reviewer checks that no secrets appear in the docs and that the limitations are stated honestly.
5. Confirm the deliverables checklist in `docs/PLAN.md` is satisfied.

## Verification

- Every command in the README is run once (compose up, tests, load test smoke).
- Mermaid blocks render (GitHub renders Mermaid natively).
- `bash .claude/skills/write-spec/check-specs.sh` still exits 0.

## Risks

| Risk | Mitigation |
|------|-----------|
| Docs drift from code. | Written last; reviewers check claims against the code; README links to specs instead of duplicating them. |
| Overstating AI's role, or the engineer's. | Log only what is evidenced in this repo; placeholders for the rest. |
