# PostgreSQL integration tests with Testcontainers

The first database concurrency test lives in
`RentalOperations/RentalOperationsTests/Integration/PostgreSql`. It starts a real
PostgreSQL container and proves that two concurrent active-rental claims for the
same motorcycle cannot both commit. The loser is rejected by PostgreSQL with
`unique_violation` (`23505`), something an in-memory repository cannot reproduce.

`RentalOperations` still uses MongoDB in the audited baseline, and the planned
`services/rental-core` project has not landed yet. For that reason, this task keeps
the minimal future rental schema inside the test fixture instead of pretending it
is a production migration. Epic 5 should move this schema into a real migration and
reuse the same concurrent-write pattern against the production repository.

## Run locally

Docker must be running. From the repository root:

```bash
dotnet test RentalOperations/RentalOperations.sln --configuration Release
```

The xUnit collection fixture starts PostgreSQL once and shares it across every test
in that collection. Tables are truncated between tests, and Testcontainers removes
the container when the collection finishes. This gives suite-level reuse without
leaving a persistent database behind or requiring a fixed host port.

The root CI workflow already runs this solution whenever `RentalOperations/**`
changes, so the same container-backed test is mandatory in pull requests. New
database integration tests should join `PostgreSqlCollection`, reset only the data
they own, avoid sleeps, and coordinate concurrent work with an explicit start gate.
