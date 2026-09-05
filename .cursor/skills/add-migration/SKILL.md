---
name: add-migration
description: Adds an EF Core migration to the platform or tenant schema following the expand/contract and lock-safety rules, and verifies it with the migration check and squawk. Use when a model change needs a migration, or when asked to "add a migration", "create a migration", or fix "pending model changes".
disable-model-invocation: true
---

# Add a migration

Migrations run across every tenant database during live shifts. Follow `ef-migrations.mdc`; this skill is the procedure.

## Procedure

```
- [ ] 1. Decide: is the change additive (new table, nullable column, constant default, concurrent index)? If not, split into expand now / contract next release
- [ ] 2. Change the model and the DbContext configuration (snake_case is automatic)
- [ ] 3. dotnet ef migrations add <PascalCaseName> --project backend/src/<Owner> --output-dir Data/Migrations
- [ ] 4. Open the generated file: remove nothing, but check for AlterColumn rewrites, non-concurrent indexes on existing tables, data statements
- [ ] 5. Destructive step? Add [RequiresSnapshot] to the migration class
- [ ] 6. dotnet ef migrations has-pending-model-changes --project backend/src/<Owner>   (must print none)
- [ ] 7. dotnet ef migrations script <Previous> <New> --idempotent --project backend/src/<Owner> | squawk   (must be clean)
- [ ] 8. Run the owning service's integration tests (they migrate a fresh container)
- [ ] 9. Commit the migration with the model change, message: sp<N>: <task> - migration <Name>
```

`<Owner>` is `Platform` for the platform DB, `WmsCore/Api` for tenant DBs.

## Concurrent index

```csharp
migrationBuilder.CreateIndex(
    name: "ix_change_log_warehouse_seq",
    table: "change_log",
    columns: ["warehouse_id", "seq"])
    .Annotation("Npgsql:CreatedConcurrently", true);
```

Concurrent index creation cannot run inside a transaction; the migrator handles that when the annotation is present. Put the index in its own migration so a failure leaves nothing half-done.

## Backfill instead of data migration

```csharp
// Not in a migration. In backend/src/WmsCore/Backfills/<Name>Backfill.cs:
public sealed class LocationPathBackfill : Backfill
{
    protected override async Task<int> RunBatchAsync(TenantDbContext db, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE location SET path = compute_path(id) WHERE path IS NULL LIMIT 5000", ct);
}
```

## If squawk complains

Fix the migration. Do not add a suppression. Common fixes: nullable column instead of NOT NULL, constant default instead of expression, separate concurrent index migration, two-release rename.
