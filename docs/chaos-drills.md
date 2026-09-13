# Chaos drills

Every drill is a pair of Tilt buttons on the `toxiproxy` resource — inject and
clear — and the same command on the CLI. Each prints what should happen and
where to look. Clear removes only that drill's toxics or restarts only that
drill's container; `Invoke-Chaos.ps1 reset` is the global recovery.

```powershell
tilt up -- --orchestrator=compose --full
powershell -File scripts/Invoke-ChaosDrill.ps1 kafka-down
powershell -File scripts/Invoke-ChaosDrill.ps1 kafka-down -Clear
```

Linux/macOS use `pwsh`. Drills run on the Compose path, where Toxiproxy is part of
the development overlay (ADR 0003); the Kubernetes path has no fault injection. The
catalog is [`deploy/chaos/drills.json`](../deploy/chaos/drills.json), shared by Tilt,
the CLI and `Test-Chaos.ps1`. A drill marked `full` is disabled in Tilt without
`--full`, and a container drill that finds no container fails instead of reporting
success.

## Inject X, expect Y, observe it at Z

Measured 2026-09-13 on the polyglot stack; raw results in
[docs/measurements/fault-tolerance](measurements/fault-tolerance/README.md). What each
failure stops and continues is the [degradation contract](degradation-contract.md).

| Drill | Inject | Expect | Observe | Measured |
|---|---|---|---|---|
| `slow-db` | +500 ms on every CockroachDB response | p99 rises, breaker stays closed, no 5xx | Grafana `projecty-load-resilience`: creation p99, breaker state | **Fails.** No creation fits the 2.5 s gateway deadline at ~5 round trips of 500 ms; the breaker opens. [#206](https://github.com/iVega123/ProjectY/issues/206) |
| `db-down` | CockroachDB timeout | 503 with `Retry-After`, bounded; the breaker opens and later refusals are immediate | `dependency_refusals_total{dependency="database"}`, `gateway_upstream_circuit_breaker_state`, spans tagged `projecty.degradation=database` | Holds. First refusals ~2 s (database deadline), then median 4.6 ms, p95 8.6 ms |
| `redis-down` | Redis timeout | Rate limiting degrades open; `Idempotency-Key` mutations and revocation checks refuse closed | `gateway_ratelimit_degraded_total`, gateway 503 traces | Holds. No 429s; every creation refused at ~252 ms |
| `kafka-down` | Kafka timeout | Rental creation continues; events wait in the outbox and drain after Clear | `projecty_rental_outbox_pending`, `dependency_degradations_total{dependency="kafka"}` | Holds. 179 created, 0 errors; 80 events pending at recovery drained in 3.0 s |
| `bad-network` | CockroachDB slicer + 60 kB connection limit | Reconnects and retries keep user-visible errors flat | k6 error rate, database client spans | Holds. 2 refusals in 1,318 requests; p99 110 ms |
| `service-killed` | Stop `telemetry` | The map freezes with its last marker; everything else continues | Console *Live map* shows *Frozen · reconnecting*; rental creation in Grafana | Holds. Rental created (200, 316 ms), listing 200 |
| `cassandra-down` | Cassandra timeout | Trip history stops; live positions are accepted and served from Redis | `traces_span_metrics_calls_total{span_name="cassandra.position",status_code="STATUS_CODE_ERROR"}` | Holds. Position broadcast; history error series 0 → 1 |
| `risk-pricing-stopped` | Stop `risk-pricing` | Rentals are still created and priced on the last projected score and table; the rescore a rental causes waits for the service | `risk.scored` against `rental.started` in Kafka; `projecty_risk_projection_oldest_score_age_seconds` | Holds. With the service up, a rental moved `risk.scored` 1 → 2. Stopped: rental created (200, 107 ms), `rental.started` published, `risk.scored` still 2 after 30 s; 3 once the service returned |
| `billing-stopped` | Stop `billing` | The rental list renders every row and says invoices are missing | Console rental list; gateway 5xx for `/api/invoices` | Holds. Listing 200 with all 3 rows, `missing: ["invoices", …]` |

The frozen-map half of `service-killed` is a browser assertion:
`services/console/test/run-browser.mjs` stops telemetry mid-session, and
`browser-smoke.mjs` waits for *Frozen · reconnecting* and requires the last position
to still be on screen.

## Reproduce the measurements

```powershell
# Load under each fault (the slow-db and db-down runs fail k6 thresholds by design).
powershell -File scripts/Run-LoadTest.ps1 -Polyglot -KeepStack
powershell -File scripts/Run-LoadTest.ps1 -Polyglot -NoBuild -KeepStack -Mode kafka-down

# Stopped services and Cassandra, against the retained stack.
node services/console/test/drill-evidence.mjs
```
