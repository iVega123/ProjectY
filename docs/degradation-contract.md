# Active degradation contract

The target contract in ADR 0003 spans services that do not yet exist. This table
separates current executable behavior from prerequisites; #71 remains open for
the unfinished target rows. Do not interpret a missing service as a passing drill.

| Dependency failure | Active behavior | Evidence / outstanding work |
|---|---|---|
| Redis rate limiter | The gateway admits traffic and increments `gateway_ratelimit_degraded_total`. | Existing gateway Redis failure tests. |
| Redis revocation / idempotency | High-value rental creation and protected idempotent mutations refuse closed. Ordinary JWT verification uses cached JWKS. | ADR 0017 supersedes the earlier blanket “everything continues” row. |
| MongoDB rental store | Rental endpoints return 503 with `Retry-After: 1`; driver connection, selection, socket and pool waits are bounded to one second each. | Driver deadline test plus controller failure tests; gateway retains its independent 2.5 s request budget. |
| RiderManager / MotoHub dependency | Rental calls have a one-second HTTP deadline. Transport failures and upstream 5xx become 503, with a distinct refusal counter and a trace degradation tag. Business 4xx remain client errors. | Controller tests cover wrapped 503 versus 404 / business rejection. |
| RabbitMQ | AuthGate and MotoHub transactional outboxes retain unpublished events and retry after recovery. Consumers use durable bounded retries and DLQ. | #69 / #154, outbox integration tests. This is the active broker; it is not Kafka evidence. |
| Risk/pricing | Rental closure already uses the fixed daily-rate model. There is no dynamic-pricing service to fail over from. | Target fallback blocked by #10. |
| Live tracking | No live map or last-position service exists in the active topology. | Blocked by #10. |
| Read projections | Current reads use their primary stores. No projection fallback is implemented. | Blocked by #130 and target read services. |
| Kafka | No active rental Kafka producer exists. | Transactional rental Kafka/outbox drill blocked by #130 and the event-service work. |
| Cassandra | No active trip-history consumer exists. | Blocked by telemetry service in #10. |

`dependency.refusals` is exported by OTel with a bounded dependency label. Its
Prometheus name is `dependency_refusals_total`. A 503 trace carries
`projecty.degradation`; exception details and connection strings are not returned
to the caller. Individual driver waits are bounded; the gateway's request timeout
is the overall public deadline, including retries. A timeout is a refusal, never
proof that a mutation did not commit: retain the same idempotency key when retrying.

Reproduce the current failure contracts:

```sh
dotnet test RentalOperations/RentalOperationsTests/RentalOperationsTests.csproj --filter FullyQualifiedName~DependencyFailureTests
cargo test --manifest-path services/api-gateway/Cargo.toml --locked
```
