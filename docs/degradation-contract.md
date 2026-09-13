# Degradation contract

What each dependency failure stops, what keeps working, how you can see it,
the test that fails if the fallback is removed, and the drill that shows it on a
running stack. [ADR 0003](adr/0003-observability-and-fault-tolerance.md) holds
the original table; three of its rows described a topology that was never built,
and this contract restates them as the system actually behaves (see the revision
note in the ADR). Measured drill results are in [chaos-drills.md](chaos-drills.md).

A refusal and a degradation are counted apart. `dependency_refusals_total`
counts requests turned away; `dependency_degradations_total` counts work that
continued without a dependency, labelled by `dependency` and `path`.

| Dependency down | Stops | Continues | Signal | Test | Drill |
|---|---|---|---|---|---|
| **Redis** | Rate limiting (fails **open**). Revocation-checked rental creation and every `Idempotency-Key` mutation (fail **closed**, 503). Live position updates. | Authenticated reads; JWT verification from cached JWKS; login and refresh, because refresh tokens live in CockroachDB (ADR 0017). | `gateway_ratelimit_degraded_total` | gateway `fails_open_and_exposes_the_degradation_counter`, `fails_closed_when_revocation_cannot_be_checked`; rental-core `IdempotencyFailClosedTests` | `redis-down` |
| **risk-pricing** | New risk scores and price tables. | Rentals are priced on the last published table (persisted in `projection_snapshots`, restored at start) or the packaged `pricing-policy.json`; a rider with no score pays the base rate. | `dependency_degradations_total{dependency="risk-pricing"}`, `projecty_risk_projection_oldest_score_age_seconds`, alert `ProjectYRiskProjectionStale` | rental-core `PricingFallbackTests` | `risk-pricing-stopped` |
| **telemetry** (live tracking) | Live map updates. | The console keeps the last marker and labels the map *Frozen · reconnecting*. The last accepted position is in Redis and is served again when the rider rejoins. Rentals and listing are unaffected. | Console map label; telemetry readiness probe | telemetry `the last known position survives the tracking process that accepted it`; console `degradation-smoke.mjs` | `service-killed` |
| **A read-composition provider** (motorcycles, invoices, rider) | That field on the rental screen. | The page renders every row and names what is missing (`without invoices`). A 404 is an answer, not a degradation. | The page label; the refused call in `traces_span_metrics_calls_total{service_name="api-gateway"}` | console `a provider that is down leaves the row without that field, not without the page` | `billing-stopped` |
| **Kafka** | Event propagation: invoices, tracking activation, rider projection updates, risk scoring. | Rental creation and closure commit. Their events wait in the outbox and drain on recovery, in order per motorcycle, each once. | `dependency_degradations_total{dependency="kafka"}`, `projecty_rental_outbox_pending`, `projecty_rental_outbox_oldest_age_seconds` | rental-core `OutboxDispatcherTests.KafkaDown_TheRentalCommits_TheEventWaits_AndDrainsOnRecovery` | `kafka-down` |
| **Cassandra** | Trip history. | Positions are accepted, broadcast and served from Redis; rental creation is unaffected. | `traces_span_metrics_calls_total{span_name="cassandra.position",status_code="STATUS_CODE_ERROR"}`, span attribute `projecty.degradation=cassandra` | telemetry `a position is accepted and served while trip history is unavailable` | `cassandra-down` |
| **CockroachDB** (primary store) | Everything that reads or writes it: rentals and motorcycles (reads included -- projections share the store), identity login and rider records, invoices. | Refusal is explicit and bounded: every database wait is capped at 2 s, so rental-core answers 503 with `Retry-After: 1` before the gateway's 2.5 s request deadline, and the gateway does not start a retry that cannot finish in what is left. After five such refusals the breaker opens and later requests are refused in milliseconds with `Retry-After` set to the remaining open time. Live tracking of active rentals (Redis) and cached-JWKS verification continue. A constraint violation stays 409. | `dependency_refusals_total{dependency="database"}`, `gateway_upstream_circuit_breaker_state`, span attribute `projecty.degradation=database` | rental-core `DatabaseDeadlinesTests`, `DependencyFailureTests`; gateway `one_deadline_bounds_the_whole_request_including_retries`, `a_slow_refusal_is_returned_instead_of_retried_past_the_deadline` | `db-down`, `slow-db`, `bad-network` |
| **RabbitMQ** | Motorcycle command and event publication. | Motorcycle writes commit; `OutboxMessages` drains after recovery with bounded retries and a DLQ. | `projecty_outbox_depth{service="rental-core"}` | rental-core `OutboxRelayTests.CommittedSequencedMessages_SurviveRelayRestartAndDrainAfterBrokerRecovery` | `Run-LoadTest.ps1 -Mode rabbit-down` |

## What changed from ADR 0003

- **Primary database.** "Reads from projections" assumed a separate read store.
  Rentals, motorcycles, the rider projection and the price snapshots are one
  CockroachDB, which is what lets a rental and its event commit together. When
  it is down there is nothing to read from, so the row promises a fast,
  explicit refusal instead.
- **Read projections.** No pre-built document store exists. The degradation
  that does exist is read composition in the console BFF (ADR 0014), and that
  is the row that is tested.
- **Redis.** "Everything continues" was superseded by ADR 0017: without Redis
  there is no proof a token was not revoked and no record that a mutation has
  not already run, so those refuse. Rate limiting still fails open.
- **Risk and pricing.** There is no fixed daily rate distinct from the tiered
  table; the fallback is the last known table and the base rate.

## Two rules that hold across rows

- A timeout is a refusal, never proof that a mutation did not commit. Retry with
  the same `Idempotency-Key`. Read-only preflight failures release the claim;
  failures during claim or mutation keep their recorded outcome.
- A missing rider projection is 409 `rider-projection-pending`, not 5xx: nothing
  is unhealthy, the rider's record has not arrived yet, and a 5xx would make
  the gateway retry and count it against the breaker.

## Reproduce

```sh
dotnet test services/rental-core/RentalCoreTests/RentalCoreTests.csproj \
  --filter "FullyQualifiedName~DatabaseDeadlinesTests|FullyQualifiedName~DependencyFailureTests|FullyQualifiedName~OutboxDispatcherTests|FullyQualifiedName~PricingFallbackTests|FullyQualifiedName~IdempotencyFailClosedTests|FullyQualifiedName~OutboxRelayTests"
cargo test --manifest-path services/api-gateway/Cargo.toml --locked
(cd services/telemetry && mix test test/degradation_test.exs)   # needs Redis at TEST_REDIS_URL
(cd services/console && npm test)
```
