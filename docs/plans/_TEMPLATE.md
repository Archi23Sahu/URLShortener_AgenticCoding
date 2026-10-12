# Plan NNN — <change title>

<!-- Copy to docs/plans/NNN-<slug>.md. NNN is the next free plan number (independent of spec numbers). -->

| | |
|---|---|
| Status | Draft → Approved → Done |
| Spec | [00N — Name](../specs/00N-name.md) |
| ACs satisfied / added | `XXX-AC-NN`, … |
| Scenario | Greenfield / Brownfield / Docs (see [index](README.md)) |
| Author / date | YYYY-MM-DD |

## Goal

One or two sentences: the user-visible outcome, phrased in terms of the ACs above.

## Out of scope

What this plan deliberately does not change.

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/...` | | |
| `tests/...` | | |
| `docs/specs/00N-*.md` | Update Behaviour / ACs / Tests column | Keep spec as source of truth |

## Steps

Each step must leave the solution building, and say how it's verified.

1. … — verify: `dotnet build`
2. … — verify: `dotnet test --filter "FullyQualifiedName~..."`
3. …

## Test strategy

| AC | Level | Test (Class.Method) | Notes |
|----|-------|---------------------|-------|
| XXX-AC-NN | unit / integration | | |

## Risks and rollback

- Risk → mitigation.
- Migrations: down-migration exists and was tested? Data backfill needed? `docs/schema.sql` updated?
- Cache / queue / background-service impact (specs 004, 005, 008)?

## Docs to update

- [ ] Spec status, Tests column and `Plans` row
- [ ] `docs/plans/README.md` index
- [ ] `docs/PLAN.md` feature inventory
- [ ] `docs/schema.sql` + `docs/schema.generated.sql` (schema changes only)
- [ ] `CLAUDE.md` (only if an architectural convention changed)

## Approval

- [ ] Plan approved by: ______ on ______
