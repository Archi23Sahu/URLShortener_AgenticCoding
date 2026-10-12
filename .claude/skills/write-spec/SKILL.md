---
name: write-spec
description: Creates or updates a feature spec in docs/specs/ so it stays the source of truth for behaviour and test traceability. Use when adding a feature, changing behaviour, reverse-engineering a spec from existing code, adding/retiring acceptance criteria, or after tests were added for GAP ACs ("write a spec for…", "update spec 005", "mark these ACs covered"). Do NOT use for implementation plans (use write-plan) or for documenting internals that have no externally observable behaviour.
---

# Write or update a spec

Template, conventions and status lifecycle: `docs/specs/README.md`. Acceptance rubric: `docs/PLAN.md` → *Rubric A*. Read both before writing.

## New spec

1. Pick the next free number from the index in `docs/specs/README.md` and a 3-letter AC prefix not already used (`CRT, RED, UPD, DEL, CCH, CLK, RPT, RLM, CLN, OBS, RSL` are taken).
2. Copy the template; status `Draft`.
3. Fill **API contract** from the controller (`src/Controllers/`), DTOs (`src/DTOs/`), validators (`src/Validators/`) and the exception → status mapping in `src/Middleware/ExceptionHandlingMiddleware.cs`. Include 429 if the controller has `[EnableRateLimiting]`, 415 for JSON bodies.
4. Fill **Behaviour** with numbered rules, each citing the file and symbol that implements it (no line numbers). For a feature not built yet, cite the plan instead and mark the rule *(planned)*; remove the marker when it is implemented.
5. Fill **Configuration** from `src/Configurations/*Options.cs` (code default) **and** `src/appsettings.json` (effective default) — call out when they differ.
6. Write **ACs**: one observable outcome each, Given/When/Then. Cover the happy path, every listed status code, each failure mode in *Edge cases*, and boundaries (≤ vs <, exactly-the-limit).
7. Tests column: exact `Class.Method` names, or **GAP**.
8. Add the spec to the index table in `docs/specs/README.md` and to the inventory in `docs/PLAN.md`. Mark every AC without a test **GAP**; the spec stays `Approved`, not `Implemented`, until none remain.

## Greenfield vs brownfield

- **Greenfield:** write the spec before the code. Status `Draft`; cite the plan for rules not built yet and mark them *(planned)*; every AC is **GAP** until its test exists. Add the spec's plans to the `Plans` row once they are written.
- **Brownfield:** the code exists, so cite its files and symbols and mark only genuinely untested ACs **GAP**. Add each new plan to the spec's `Plans` row.

## Updating an existing spec

- Never renumber ACs. Retire with ~~strikethrough~~ and a one-line reason. New ACs take the next number.
- Behaviour change ⇒ status back to `Draft` until the user approves.
- When tests are added for a GAP AC, replace **GAP** with the test names.
- Re-verify every cited file and symbol in sections you touch (`grep` the symbol); update *Last verified*.
- Unclear intent in code ⇒ an *Open question* in the `Draft` spec for the user to resolve before approval; never a guessed rule.

## Verify

```bash
bash .claude/skills/write-spec/check-specs.sh --unmapped
```

Must exit 0 (every cited test exists). Any test listed as UNMAPPED that belongs to this feature should be added to an AC. Then check the spec against Rubric A item by item and tell the user which ACs are GAP.
