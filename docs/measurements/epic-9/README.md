# Measured rental load — 2026-09-05

Five constant VUs for 30 seconds, one seeded rider, default per-subject rate limiter.
Host: AMD Ryzen 9 9950X3D2 (16 cores/32 threads), Windows with Linux Docker 29.7.2;
Docker allocation: 32 logical CPUs and 66,091,401,216 bytes RAM (~61.55 GiB).
These are limited-subject acceptance measurements, not maximum system capacity.

| Mode | Created | HTTP 429 | Unexpected responses | Successful p95 / p99 (ms) | k6 exit |
|---|---:|---:|---:|---:|---:|
| baseline | 179 | 1,220 | 0 / 1,399 (0%) | 37.78 / 87.16 | 0 |
| slow-db (MongoDB +500 ms) | 88 | 0 | 4 / 92 (4.35%) | 1,528.78 / 2,533.04 | 99 |
| db-down (MongoDB timeout) | 0 | 957 | 179 / 1,136 (15.76%) | N/A: no successful creation | 99 |
| rabbit-down (RabbitMQ timeout) | 179 | 1,231 | 0 / 1,410 (0%) | 26.95 / 33.16 | 0 |

The database outage's observed HTTP 503 latency was median 3.52 ms,
p95 1,056.41 ms, p99 2,063.89 ms, maximum 2,096.70 ms. Initial requests and
half-open probes still incur dependency timeouts; a fast median does not mean
every refusal is instantaneous. The aggregate unexpected-response metric is
not a status-code breakdown, so it does not prove every failed request was 503.

The slow database experiment fails the original no-errors criterion:
[bug #160](https://github.com/iVega123/ProjectY/issues/160).
The same unchanged thresholds correctly reject both database fault runs.
RabbitMQ is not on the current synchronous rental-write path; its passing result
does not prove Kafka delivery, accumulation or recovery.

Reproduce from repository root:
```powershell
powershell -File scripts/Run-LoadTest.ps1
powershell -File scripts/Run-LoadTest.ps1 -Mode slow-db
powershell -File scripts/Run-LoadTest.ps1 -Mode db-down
powershell -File scripts/Run-LoadTest.ps1 -Mode rabbit-down
```

Each command builds an isolated project, seeds fixtures and removes its own
containers and volumes afterwards. Add -KeepStack to inspect Grafana on port
13000; the normal Tilt dashboard is on port 3000. See
[runner details](../../load-testing.md).

Raw k6 summaries and environment metadata are adjacent. The metadata commit is
the application checkout at measurement time, not a claim that the runner had
no working-tree edits: baseline/slow-db were measured while task #72 was being
implemented; db-down/rabbit-down include the subsequently committed refusal
metric and warmup recovery change. Final runner revision before publication
of these results: 8518121. Application code was unchanged across these runs.
Dashboard provisioning (six panels) and actual Prometheus series for creation
latency, rate limiting, breaker state and dependency refusals were verified.
The ephemeral-stack baseline also passed GitHub Actions in PR #159.

