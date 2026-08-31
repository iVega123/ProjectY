# PostgreSQL migration operations

AuthGate, MotoHub, and RiderManager own separate PostgreSQL databases and use
EF Core migrations as the only schema creation and evolution mechanism. A
`DbContext` constructor must never create, migrate, or contact a database.

## Local startup

The root Compose stack runs one-shot migration services after PostgreSQL is
healthy:

- `auth-gate-migrations`
- `moto-hub-migrations`
- `rider-manager-migrations`

Each container starts the corresponding application with `--migrate` and exits
after `Database.MigrateAsync()` completes. Application containers depend on a
successful migration exit, so a failed migration prevents incompatible code
from starting.

```bash
docker compose up --build
```

The three migration services are idempotent. Re-running one applies only
pending migrations:

```bash
docker compose run --rm auth-gate-migrations
docker compose run --rm moto-hub-migrations
docker compose run --rm rider-manager-migrations
```

The clean baselines in this repository replace databases previously created by
`EnsureCreated()`. A disposable local PostgreSQL volume from an older checkout
must be removed before the first migration-based startup. Stop Compose and
remove only that stack's `postgres-data` volume; do not delete persistent data
from a shared or production environment. Persistent databases require a backup
and an explicitly reviewed adoption plan before the baseline is marked as
applied.

## Creating a schema change

Restore the repository-pinned EF tool, add the migration to the owning project,
and verify that the snapshot exactly matches the model:

```bash
dotnet tool restore
dotnet ef migrations add <MigrationName> --project AuthGate/AuthGate/AuthGate.csproj --startup-project AuthGate/AuthGate/AuthGate.csproj --output-dir Migrations
dotnet ef migrations has-pending-model-changes --project AuthGate/AuthGate/AuthGate.csproj --startup-project AuthGate/AuthGate/AuthGate.csproj
```

Use the equivalent project path for MotoHub or RiderManager. Commit the
migration, its designer file, and the updated model snapshot together. Review
both `Up()` and `Down()` before deployment; generated code is not assumed to be
safe merely because it compiles.

## Rollback

Prefer a forward corrective migration after a migration has reached a shared
environment. Rolling a schema backward is allowed only when the target
migration's `Down()` is non-destructive and the previous application version is
ready to deploy.

1. Stop the affected application and take a database backup.
2. Check out the application version whose migration set will remain deployed.
3. Set `ConnectionStrings__Postgresql` in the current shell without committing
   it to a file.
4. Preview the rollback SQL and have it reviewed:

   ```bash
   dotnet ef migrations script <CurrentMigration> <PreviousMigration> --project AuthGate/AuthGate/AuthGate.csproj --startup-project AuthGate/AuthGate/AuthGate.csproj
   ```

5. Apply the reviewed rollback:

   ```bash
   dotnet ef database update <PreviousMigration> --project AuthGate/AuthGate/AuthGate.csproj --startup-project AuthGate/AuthGate/AuthGate.csproj
   ```

6. Deploy the compatible application version and verify readiness.

If `Down()` drops or rewrites data, restore the backup or ship a forward repair;
do not use the generated rollback. Clear the connection string from the shell
after the operation.
