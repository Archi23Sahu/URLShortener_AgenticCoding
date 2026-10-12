---
name: ef-schema-change
description: Procedure for changing the SQL Server schema through EF Core in this repo — entity + IEntityTypeConfiguration, migration, keeping docs/schema.sql and docs/schema.generated.sql in sync, and applying it locally and in tests. Use when adding/removing/renaming a column, index, table or constraint, or changing a column type or collation ("add a column", "new table", "add an index", "migration"). Do NOT use for query-only changes in the repository that don't alter the schema.
---

# EF Core schema change

Precedent to copy: migration `src/Migrations/20261010022547_AddBrowserToClickEvents.cs` (added a nullable column to an existing table).

## 1. Model

- Entity in `src/Entities/` (XML doc on every new property: meaning, units, nullability reason).
- Mapping in `src/Configurations/<Entity>Configuration.cs` — explicit column types (`varchar(n)`, `char(n)`, `datetime2`), `IsRequired`, max lengths. Don't rely on conventions.
- Index names follow `UX_<Table>_<Cols>` (unique) / `IX_<Table>_<Cols>` (non-unique), with a comment saying which query uses it.
- New columns on tables with existing rows (`ShortUrls`, `ClickEvents`) must be nullable or have a default; readers must handle null for legacy rows (see how `ReportService` maps a null `Browser` to `Unknown`).
- `ShortUrls.Code` must keep the case-sensitive collation `SQL_Latin1_General_CP1_CS_AS`.

## 2. Migration

Needs the EF CLI (`dotnet tool install --global dotnet-ef --version 8.*` if `dotnet ef` is missing).

```bash
dotnet ef migrations add <PascalCaseName> --project src
```

Review the generated `Up` **and** `Down`: no unintended drops, data-preserving renames (`RenameColumn`, not drop + add), `Down` actually reverses `Up`. `AppDbContextModelSnapshot.cs` must change in the same commit.

## 3. Docs

- `docs/schema.sql` is **hand-written** to mirror the configurations — edit it to match.
- Regenerate the authoritative script and compare:
  ```bash
  dotnet ef migrations script --project src -o docs/schema.generated.sql
  ```
  Differences should be limited to `__EFMigrationsHistory` and constraint naming.
- Update `docs/ER-Diagram.md` if tables or relationships changed.
- Update the affected spec's *API contract* / *Behaviour* if the change is visible.

## 4. Apply

- Local DB: `dotnet ef database update --project src` (SQL Server from `docker compose up -d sqlserver`), or run with `Database__MigrateOnStartup=true`.
- Compose stack migrates on startup automatically.
- Integration tests: `ApiFactory` calls `Database.Migrate()` on the `UrlShortener_IntegrationTests` database, so the new migration is applied on the next test run.

## Verify

1. `dotnet build`
2. `dotnet ef migrations script --project src --idempotent` succeeds.
3. Full test suite passes (integration tests exercise the migrated schema) — use `run-tests` or the `test-runner` agent.
4. `git diff docs/schema.sql docs/schema.generated.sql` shows the expected change in both.
