---
name: feature-workflow
description: Runs the full gated development loop for this repo — spec → plan → tests → implement → run tests → quality + security review → close. Use when the user asks to build a feature, change behaviour, fix a bug that changes behaviour, or add tests for acceptance criteria marked GAP in a spec ("implement X", "add feature", "close the GAPs in spec 005", "run the workflow"). Do NOT use for pure refactors with no behaviour change (skip to steps 5–6), for questions, or for editing docs only.
---

# Feature workflow

The process is defined in `docs/PLAN.md` → *Workflow*; this skill is how to execute it. Never skip a ⏸ gate: stop, summarise, and wait for the user's explicit approval before continuing.

## 0. Orient

- Identify the spec(s) involved from `docs/specs/README.md`. If the request maps to acceptance criteria marked **GAP** in a spec, list the AC IDs.
- If no spec covers the change, it's a new feature → step 1 creates one.
- Tell the user which steps you'll run and which you'll skip (and why).

## Greenfield vs brownfield

- **Greenfield (building a system from nothing):** run steps 1–2 for each task in build order. Specs start `Draft` with every AC marked **GAP**; plans are numbered in build order and list the specs they satisfy. Step 3 is TDD (tests fail until step 4). Record the plan in `docs/plans/README.md` under *Greenfield*.
- **Brownfield (changing a running system):** specs already exist, so identify the ACs affected; closing **GAP**s means the code already exists and tests should pass on first run; behaviour changes set the spec back to `Draft`. Record the plan under *Brownfield*.

## 1. Spec ⏸

Use the `write-spec` skill. New spec → status `Draft`. Changed behaviour on an existing spec → add/modify ACs (never renumber) and set status back to `Draft`.

Gate: show the user the new/changed ACs and any open questions (a spec cannot be approved with unresolved ones). On approval set status `Approved`.

## 2. Plan ⏸

Use the `write-plan` skill to create `docs/plans/NNN-<slug>.md`.

Gate: show the plan's Changes table, Steps and Risks. On approval tick the plan's Approval box.

## 3. Tests (test-author)

Launch the `test-author` subagent with:
- the spec path and the exact AC IDs to cover,
- the approved plan path,
- whether the production code already exists (gap closing) or not yet (TDD — tests are expected to fail until step 4).

Review its test matrix against the ACs before moving on: every AC in scope must map to at least one test.

## 4. Implement

Implement the plan's steps in order in the main session. Use `add-api-endpoint` or `ef-schema-change` when the plan touches those areas. Follow `CLAUDE.md` conventions. Keep the plan's step list accurate if reality diverges (and tell the user).

## 5. Run tests (test-runner)

Launch the `test-runner` subagent. Default scope: full suite. For a quick loop, pass a filter first, then the full suite before step 6.

If tests fail: diagnose in the main session, fix code or (if the test is wrong) send it back to `test-author`, and repeat step 5. Never weaken an assertion just to get green — if an AC itself looks wrong, go back to the step-1 gate.

## 6. Review (parallel)

Run the five-axis staff review: launch `code-quality-reviewer` (correctness, readability and simplicity, architecture, performance) and `security-reviewer` (security) **in the same message** so they run concurrently. Give both: the list of changed files (or "uncommitted changes"), the spec path(s), and the plan path.

Then:
- Fix every Blocker and Major (or explain to the user why a finding is wrong).
- Re-run step 5 after fixes.
- Minor/Nit: list them for the user; fix only if cheap and in scope.

## 7. Close

- Spec: status `Implemented`, Tests column updated (no stale GAPs for covered ACs), *Last verified* = today.
- `docs/PLAN.md`: update the feature inventory counts.
- Plan: status `Done`.
- Run `bash .claude/skills/write-spec/check-specs.sh` — must exit 0.

Final report to the user: ACs closed, test results (numbers from test-runner), review verdicts, anything left open. Do not commit unless asked.
