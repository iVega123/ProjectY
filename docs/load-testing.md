# Reproducing the rental load gate

Run from the repository root with Docker and PowerShell 5.1 (Windows) or 7
(Linux/macOS):

```powershell
powershell -File scripts/Run-LoadTest.ps1
# On Linux/macOS: pwsh -File scripts/Run-LoadTest.ps1
```

The runner generates ignored credentials and a dedicated `projecty-load` Compose
model. It uses separately named volumes/images, loopback-only ports, a release
gateway build, real .NET services, PostgreSQL, MongoDB, Redis, RabbitMQ and LGTM.
It seeds one rider and 10,000 motorcycles, warms up one creation, then sends
five concurrent users for 30 seconds with 100 ms think time.

A test-only in-network issuer supplies short-lived Ed25519 tokens. Requests
still traverse the real gateway's JWT validation, signed identity, revocation,
rate limiting and the real rental creation flow. This does not benchmark signup,
login or the future Go identity service. The issuer is never present in the
application or production Compose models.

The default limiter remains enabled (120-token burst, 120 tokens/minute), shared
by the seeded rider. Report created rentals and 429s separately: this is a bounded
smoke/performance gate under the configured policy, not a maximum-capacity claim.
Each accepted request gets a fresh motorcycle and idempotency key. Repeated 409s,
401s or fast 503s cannot masquerade as successful throughput.

The build fails when any of these conditions fails:

- successful-creation p95 < 800 ms and p99 < 2,000 ms;
- unexpected response rate < 1%, calculated over **all** rental requests;
- at least 100 successful creations;
- more than 99% of checks pass.

429 is registered with k6's expected-status callback and counted separately.
Latency contains only successful creations. The former filtered error threshold
could exclude the very failures it was meant to detect; the gate no longer
filters by `expected_response:true`.

Results and environment metadata are written to `load/results/`. The GitHub
`Rental load gate` workflow builds the ephemeral stack, runs the same command,
and uploads JSON artifacts even when thresholds fail. Ordinary runs remove only
the generated benchmark containers and volumes. `-KeepStack` retains them for
inspection at Grafana `http://localhost:13000/d/projecty-load-resilience`;
`-NoBuild` reuses already built benchmark images.

To compare faults against the same workload:

```powershell
powershell -File scripts/Run-LoadTest.ps1 -KeepStack
powershell -File scripts/Run-LoadTest.ps1 -Mode slow-db -KeepStack -NoBuild
powershell -File scripts/Run-LoadTest.ps1 -Mode db-down -KeepStack -NoBuild
powershell -File scripts/Run-LoadTest.ps1 -Mode rabbit-down -KeepStack -NoBuild
```

Each run resets only its isolated fixture data, warms up, then injects the toxic.
k6 clears its toxic in teardown. Threshold failures during a deliberate outage
remain nonzero exits and are evidence of the measured failure, not a waived gate.
A later run starts with an explicit proxy reset. Kafka resilience is not inferred
from the RabbitMQ scenario; its target acceptance criteria remain open.

The `ProjectY — load and resilience` dashboard overlays the k6 percentiles,
creation/rejection rates, error rate, service health, gateway breaker state and
durable DLQ depth. k6 remote-writes into the same Prometheus as the application.
