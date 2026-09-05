# Tilt chaos drills

Tilt exposes the following drill/clear pairs on the Toxiproxy resource. Every
command prints the expected effect and where to inspect it. Clear removes only
that drill's named toxics; it does not restart a container or clear other drills.
`Invoke-Chaos.ps1 reset` is the explicit global recovery command.

| Drill | Injection | Expected / acceptance status | Observe |
|---|---|---|---|
| Slow database | 500 ms on the active MongoDB proxy | Measure increased p99. Measured 4.35% unexpected responses; acceptance failed, tracked in [#160](https://github.com/iVega123/ProjectY/issues/160). | Grafana rental SLO, Mongo spans |
| Database down | MongoDB downstream timeout | 503 with Retry-After; gateway breaker opens after repeated failures | Gateway metrics and degraded traces |
| Redis down | Redis downstream timeout | Limiter fails open; revocation and idempotency fail closed | Degradation counter and response status |
| Kafka down | Disabled until the rental Kafka path exists | Target: rental writes continue, outbox accumulates and drains | Blocked by #130 / event-service work |
| Bad network | MongoDB slicer + 60,000-byte connection limit | Measure reconnects/retries and errors; unchanged error rate is not yet demonstrated | Client spans and k6 |
| Service killed | Disabled until tracking/map exists | Target: map freezes, other services continue | Blocked by #10 |

The two unavailable pairs are intentionally disabled in Tilt and refuse CLI
execution with the prerequisite explanation. They are not demonstrations of
resilience. Task #68 stays open until all six original acceptance criteria are
observed. Do not substitute a stopped unrelated service for a live-map drill.

```powershell
powershell -File scripts/Invoke-ChaosDrill.ps1 db-down
powershell -File scripts/Invoke-ChaosDrill.ps1 db-down -Clear
powershell -File scripts/Invoke-ChaosDrill.ps1 redis-down
powershell -File scripts/Invoke-ChaosDrill.ps1 redis-down -Clear
```

Linux/macOS use `pwsh`. The catalog is `deploy/chaos/drills.json` and is shared by
the CLI and Tilt, so a disabled prerequisite cannot silently become an active
button. The API URL can be overridden for an isolated validation stack.
