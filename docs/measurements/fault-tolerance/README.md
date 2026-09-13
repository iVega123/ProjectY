# Measured fault tolerance — 2026-09-13

The drills of #68 run against the full polyglot topology (`Run-LoadTest.ps1 -Polyglot`),
with every dependency behind Toxiproxy (#67). Five constant VUs for 30 seconds, one
seeded rider, default per-subject rate limiter; the same unchanged k6 thresholds as
[epic 9's first measurement](../epic-9/README.md), which measured the retired MongoDB store.

Host: AMD Ryzen 9 9950X3D2, Windows with Linux Docker 29.7.2; 32 logical CPUs and
66,091,405,312 bytes (~61.55 GiB) allocated. These are acceptance measurements of one
limited subject, not a capacity claim.

## Load under each drill

| Mode | Created | HTTP 429 | Unexpected responses | Successful p95 / p99 (ms) | 503 refusal median / p95 / p99 (ms) | k6 exit |
|---|---:|---:|---:|---:|---:|---:|
| baseline | 179 | 1,205 | 0 / 1,384 (0%) | 42.1 / 50.1 | — | 0 |
| kafka-down | 179 | 1,206 | 0 / 1,385 (0%) | 41.1 / 43.5 | — | 0 |
| bad-network | 177 | 1,139 | 2 / 1,318 (0.15%) | 104.6 / 110.0 | 92.6 / 101.4 / 102.2 | 0 |
| db-down | 0 | 1,152 | 178 / 1,330 (13.4%) | — | 4.6 / 8.6 / 2,019.7 | 99 |
| slow-db (+500 ms) | 135 | 0 | 0 / 135 (0%) | 1,037.5 / 1,549.3 | — | 99 |
| redis-down | 0 | 0 | 425 / 425 (100%) | — | 252.2 / 252.7 / 252.9 | 99 |

Reading the table against the [degradation contract](../../degradation-contract.md):

- **kafka-down** — rental creation is unaffected. The outage left 80 rental events in the
  outbox when the toxic was removed; they drained in 3.0 s, and all 180 events of the run
  were published ([kafka-down-drain.json](kafka-down-drain.json)).
- **db-down** — the contract holds. The first refusals are rental-core's own 503 with
  `Retry-After: 1` at ~2 s (the database deadline); the breaker then opens and the rest are
  refused in single-digit milliseconds. The p99 is those first refusals.
- **redis-down** — the contract holds, and the thresholds fail by design: every rental
  creation carries an `Idempotency-Key`, which refuses closed without Redis after the
  250 ms Redis timeout. Rate limiting failed open (no 429).
- **bad-network** — errors stay near flat: 2 refusals in 1,318 requests, latency roughly
  doubled.
- **slow-db** — the expectation holds: slower, not refused. Creating a rental costs two
  database round trips (the preconditions read and one write statement), so +500 ms per
  response puts the median at 1,028 ms, inside the 2.5 s gateway deadline. No request
  was refused, so the breaker never opened; the p99 is a single creation at 1,549 ms. The
  k6 exit is the p95 < 800 ms threshold, which +500 ms per round trip cannot meet and
  which was not relaxed. Each creation now takes about a second, so the five VUs never
  reach the rate limiter (no 429). Re-measured on `f4c8c56`, after
  [#213](https://github.com/iVega123/ProjectY/pull/213); the first run, at five round
  trips, created nothing ([#206](https://github.com/iVega123/ProjectY/issues/206)).

## Drills without a load profile

`node services/console/test/drill-evidence.mjs`, against the same retained stack, runs
each drill through `Invoke-ChaosDrill.ps1` and clears it in a `finally`
([drills.json](drills.json)):

| Drill | Observed |
|---|---|
| service-killed (telemetry stopped) | Rental creation 200 in 316 ms; rental listing 200 |
| risk-pricing-stopped | Control with the service up: a rental moved `risk.scored` 1 → 2 (creation 118 ms). Stopped: creation 200 in 107 ms, `rental.started` published, `risk.scored` unchanged at 2 after 30 s; 3 after the service returned. The fallback counter is not used as evidence: the fixture rider is unscored until risk-pricing scores it, so it counts with the service up too |
| billing-stopped | Listing 200 with all 3 rows; `missing` includes `invoices`. `rider` also appears: the fixture's token comes from a test issuer, not identity, and the drill asserts only on invoices |
| cassandra-down | A live position was accepted and broadcast; `cassandra.position` error calls 0 → 1 |

The two database runs are the second measurement of this build. The first, before #71's
fixes, found that rental-core had no database deadlines and that gateway retries turned a
database outage into `409 Request already in progress` after 2.5 s, which never opened the
breaker; those numbers were discarded with the build that produced them.

Reproduce from the repository root:

```powershell
powershell -File scripts/Run-LoadTest.ps1 -Polyglot -KeepStack
foreach ($mode in 'slow-db','db-down','redis-down','bad-network','kafka-down') {
  powershell -File scripts/Run-LoadTest.ps1 -Polyglot -NoBuild -KeepStack -Mode $mode
}
```

The environment files record `commit` as the checkout the runs started from; the
application code measured is that commit plus the #67, #70 and #71 changes that these
files were published with. The exception is slow-db, measured on `f4c8c56` as merged,
with no working-tree changes.
