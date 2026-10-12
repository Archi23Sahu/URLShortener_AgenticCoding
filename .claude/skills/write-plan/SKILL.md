---
name: write-plan
description: Turns an Approved spec (or a set of GAP ACs) into an implementation plan at docs/plans/NNN-<slug>.md using docs/plans/_TEMPLATE.md. Use after a spec is approved and before any code or tests are written ("plan this", "write a plan for spec 007", "plan the P1 gaps"). Do NOT use for writing the spec itself (write-spec) or for trivial one-line fixes.
---

# Write an implementation plan

Template: `docs/plans/_TEMPLATE.md`. Acceptance rubric: `docs/PLAN.md` → *Rubric B*.

## Steps

1. **Inputs.** Read the spec and list the AC IDs in scope. If the spec is `Draft`, stop and ask for approval first. Read the code each AC touches — the plan must name real files and symbols.
2. **Number.** Next free number in `docs/plans/` (plans are numbered independently of specs). Slug = short kebab-case goal, e.g. `016-close-click-writer-gaps.md`. Add the plan to the index in `docs/plans/README.md`.
3. **Changes table.** Every file to create or modify, including tests and docs. Check these cross-cutting touch points from `CLAUDE.md`:
   - writes to a link → go through `IUrlShortenerService` so `CachedUrlShortenerService` invalidates;
   - new endpoint → `add-api-endpoint` checklist (route constraint, rate-limit policy, validator, exception mapping);
   - schema change → `ef-schema-change` (migration + `docs/schema.sql` + `docs/schema.generated.sql`);
   - background services → new DI scope per unit of work;
   - "now" → injected `TimeProvider`.
4. **Steps.** Ordered so the solution builds after each one; each step ends with `— verify:` and a concrete command (`dotnet build`, `dotnet test --filter "FullyQualifiedName~X"`).
5. **Test strategy.** One row per AC: unit (Moq + fake `TimeProvider`, no I/O) when the logic is isolated; integration (`ApiFactory`, real SQL Server) when it depends on HTTP pipeline, EF/SQL behaviour, background services, or rate limiting. Name the intended test methods (`Method_Scenario_Expected`).
6. **Risks and rollback.** Mandatory for migrations (Down method, data on existing rows), cache semantics, the click pipeline, and anything that changes a status code clients may rely on.
7. **Docs to update.** Tick the boxes that apply.
8. Status `Draft`.

## Verify

- Every AC in scope appears in the Test strategy table.
- Every file in Steps appears in the Changes table and exists (or is marked *new*).
- Check Rubric B item by item, then present the Changes table, Steps and Risks to the user for approval. Do not start implementing until they approve.
