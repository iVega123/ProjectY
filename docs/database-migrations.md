# PostgreSQL migration operations

AuthGate, MotoHub, and RiderManager own separate PostgreSQL databases and use
EF Core migrations as the only schema creation and evolution mechanism. A
`DbContext` constructor must never create, migrate, or contact a database.

`rental-core` is deliberately not on that list. Its schema is applied from
`deploy/db/sql` by the `cockroach-init` service, because the same files have to
run unchanged on CockroachDB and PostgreSQL, and two tools owning one schema is
how a schema drifts. EF Core still maps `motorcycles`, but it neither creates
nor evolves the table.

## The #135 cutover is destructive by design

#135 moved rentals out of MongoDB and removed the store. There is no backfill,
no dual-read window and no ObjectId compatibility lookup, because there is no
installation to carry forward: this repository has only ever run on a developer
machine and in CI. The cutover is `docker compose down -v` and a fresh stack.

That is a boundary, not a claim that migrating would have been easy. An
installation holding real rentals would need at least:

- every rental copied, closed history included, carrying `rider_name`,
  `additional_costs` and `status_message`. The pre-cutover mirror wrote nine
  columns and none of those three, so migrated rows would read back as `0` and
  empty text rather than as what was settled. (#137 has since dropped the last
  two from `rentals` altogether — settlement moved to `invoices`, and
  `004_settlement_moves_to_billing.sql` is where they leave. The paragraph
  stands as written because it describes what the #135 cutover would have had to
  carry at the time it happened.)
- a stable external identifier. The mirror derived a row id by zero-padding a
  12-byte ObjectId into a UUID, while issued URLs and already-published
  `rental.started` events carry the ObjectId itself — so a migrated rental's
  `rental.closed` would not correlate with its own start.
- pending event envelopes drained out of each rental document into `outbox`.
  A Kafka backlog at cutover would otherwise be dropped instead of retried,
  which is worse than a delay: the events were already committed.
- `rider_projection` seeded, because the `rental-rider-projection-v1` consumer
  group already has committed offsets and `AutoOffsetReset.Earliest` will not
  replay what that group has read. Existing riders would resolve as unverified
  until each happened to emit a new event.

None of that is implemented, and none of it should be inferred from the code.
If this project ever acquires an installation worth migrating, the list above is
where that work starts, and it is its own change — not a step folded into the
one that removed the store.

## Local startup

The root Compose stack runs one-shot migration services after PostgreSQL is
healthy:

- `auth-gate-migrations`
- `moto-hub-migrations`
- `rider-manager-migrations`

Each container starts the corresponding application with `--migrate=true` and exits
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
dotnet ef migrations add <MigrationName> --project services/AuthGate/AuthGate/AuthGate.csproj --startup-project services/AuthGate/AuthGate/AuthGate.csproj --output-dir Migrations
dotnet ef migrations has-pending-model-changes --project services/AuthGate/AuthGate/AuthGate.csproj --startup-project services/AuthGate/AuthGate/AuthGate.csproj
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
   dotnet ef migrations script <CurrentMigration> <PreviousMigration> --project services/AuthGate/AuthGate/AuthGate.csproj --startup-project services/AuthGate/AuthGate/AuthGate.csproj
   ```

5. Apply the reviewed rollback:

   ```bash
   dotnet ef database update <PreviousMigration> --project services/AuthGate/AuthGate/AuthGate.csproj --startup-project services/AuthGate/AuthGate/AuthGate.csproj
   ```

6. Deploy the compatible application version and verify readiness.

If `Down()` drops or rewrites data, restore the backup or ship a forward repair;
do not use the generated rollback. Clear the connection string from the shell
after the operation.
