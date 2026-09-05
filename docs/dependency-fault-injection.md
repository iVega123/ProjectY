# Dependency fault injection

Tilt loads `docker-compose.yml` plus `docker-compose.chaos.yml`. The latter is
**development-only**: production retains direct endpoints and uses AWS FIS for
controlled infrastructure experiments. Do not include the chaos overlay in a
production deployment. The unauthenticated control API binds to loopback only.

The active .NET topology routes PostgreSQL, Redis, RabbitMQ, MongoDB and MinIO
through `toxiproxy` on their native ports. Keeping the native AMQP and object-store
ports also routes the existing TCP readiness checks through the same listeners.
Migrations wait for both the proxy and their database. Application services and
the gateway wait for proxy health before startup. Infrastructure self-checks and
administrative tools intentionally address their own backends directly.

```powershell
# Tilt enables the overlay automatically.
tilt up
powershell -File scripts/Invoke-Chaos.ps1 list
powershell -File scripts/Invoke-Chaos.ps1 add postgres slow-db -Value 500
powershell -File scripts/Invoke-Chaos.ps1 remove postgres slow-db
powershell -File scripts/Invoke-Chaos.ps1 add redis redis-down -Type timeout -Value 0
powershell -File scripts/Invoke-Chaos.ps1 reset
```

On Linux/macOS use `bash scripts/chaos.sh` with the same arguments (PowerShell 7
required). `add` replaces a toxic of the same name, `remove` is repeatable, and
`reset` removes every toxic and enables all proxies. None restarts a service.
Removing a fault allows clients to reconnect; application recovery is verified
separately by the degradation drills, rather than inferred from an open port.

To run Compose directly:

```sh
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d
```

The separate `deploy/chaos/toxiproxy.json` belongs to the future self-hosted
topology. Kafka, Cassandra and the new application services are not part of the
active root stack; their end-to-end drills remain dependent on #10 and #130.
Task #67 covers the running topology; #68 and #71 must remain open until their
remaining service-dependent acceptance criteria are demonstrated.
