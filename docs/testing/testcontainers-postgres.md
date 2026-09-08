# PostgreSQL integration tests with Testcontainers

The first database concurrency test lives in
`services/RentalOperations/RentalOperationsTests/Integration/PostgreSql`. It starts a real
PostgreSQL container and proves that two concurrent active-rental claims for the
same motorcycle cannot both commit. The loser is rejected by PostgreSQL with
`unique_violation` (`23505`), something an in-memory repository cannot reproduce.

That is what it does now. The fixture no longer carries a schema of its own:
`RentalCoreDatabase` applies `deploy/db/sql` -- the same files `cockroach-init`
applies -- so the test exercises the production DDL rather than a copy of it.
A test that builds its own schema proves the code agrees with itself; this one
proves the code agrees with what gets deployed.

## Run locally

Docker must be running. From the repository root:

```bash
dotnet test services/rental-core/RentalCore.sln --configuration Release
```

The xUnit collection fixture starts PostgreSQL once and shares it across every test
in that collection. Tables are truncated between tests, and Testcontainers removes
the container when the collection finishes. This gives suite-level reuse without
leaving a persistent database behind or requiring a fixed host port.

The root CI workflow already runs this solution whenever `services/RentalOperations/**`
changes, so the same container-backed test is mandatory in pull requests. New
database integration tests should join `PostgreSqlCollection`, reset only the data
they own, avoid sleeps, and coordinate concurrent work with an explicit start gate.
