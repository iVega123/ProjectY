# Dependency fault injection

Tilt's Compose path loads `docker-compose.yml` plus `docker-compose.chaos.yml`,
and with `--full` also `docker-compose.polyglot.yml` plus
`docker-compose.chaos-polyglot.yml`. The chaos files are **development-only**:
production retains direct endpoints and uses AWS FIS for controlled
infrastructure experiments. Neither the Kubernetes path nor any production
deployment loads them. The unauthenticated control API binds to loopback only.

Every application connection to a data store or broker crosses `toxiproxy`:

| Proxy | Listens | Clients |
|---|---|---|
| `cockroachdb` | 26257 | rental-core, identity, billing |
| `redis` | 6379 | api-gateway, rental-core, telemetry |
| `rabbitmq` | 5672 | rental-core |
| `minio` | 9000 | identity, risk-pricing |
| `kafka` | 9094 | rental-core, identity, billing, risk-pricing, telemetry |
| `cassandra` | 9042 | telemetry |

Kafka needs one more step than the others. A broker tells each client which
address to reconnect to, so a proxy in front of the ordinary listener would see
the bootstrap call and nothing after it. The chaos overlay adds a `CHAOS`
listener on 9094 that advertises `toxiproxy:9094`; applications bootstrap there,
while topic creation and the schema registry keep the direct listener.

Keeping native ports also routes the existing TCP readiness checks through the
same listeners. Application services wait for proxy health before startup.
Infrastructure self-checks and administrative tools intentionally address their
own backends directly. The schema registry is an HTTP dependency read at
startup and cached; it is not proxied.

```powershell
# Tilt enables the overlays automatically on the Compose path.
tilt up -- --orchestrator=compose --full
powershell -File scripts/Invoke-Chaos.ps1 list
powershell -File scripts/Invoke-Chaos.ps1 add cockroachdb slow-db -Value 500
powershell -File scripts/Invoke-Chaos.ps1 remove cockroachdb slow-db
powershell -File scripts/Invoke-Chaos.ps1 add kafka kafka-down -Type timeout -Value 0
powershell -File scripts/Invoke-Chaos.ps1 reset
```

On Linux/macOS use `bash scripts/chaos.sh` with the same arguments (PowerShell 7
required). `add` replaces a toxic of the same name, `remove` is repeatable, and
`reset` removes every toxic and enables all proxies. None restarts a service.
Removing a fault allows clients to reconnect; application recovery is verified
separately by the degradation drills, rather than inferred from an open port.

To run Compose directly:

```sh
docker compose -f docker-compose.yml -f docker-compose.chaos.yml \
  -f docker-compose.polyglot.yml -f docker-compose.chaos-polyglot.yml up -d
```

`scripts/Test-Chaos.ps1` refuses a model in which any of the connections above
bypasses its proxy, then injects latency into real traffic and verifies that
removing it restores the baseline without a restart.
