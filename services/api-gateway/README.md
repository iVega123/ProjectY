# API gateway

The Rust/Axum gateway is the strangler entry point in front of ProjectY's four
audited ASP.NET Core services. It owns routing and process lifecycle in task
[#57](https://github.com/iVega123/ProjectY/issues/57); authentication, rate
limiting, resilience and OpenTelemetry arrive in the following Epic 6 tasks.

## Routes

| Public path | Current upstream |
|---|---|
| `/api/auth/**` | AuthGate |
| `/api/riders/**`, `/update-image` | RiderManager |
| `/api/motorcycles/**` | MotoHub |
| `/api/rental/**` | RentalOperations |

The gateway owns `/health/live` and `/health/ready`. Unknown paths return `404`
instead of being guessed or forwarded to a default service.

## Configuration

All runtime configuration comes from environment variables.

| Variable | Required | Purpose |
|---|---|---|
| `GATEWAY_BIND` | No (`0.0.0.0:8090`) | Listener address |
| `GATEWAY_HEALTH_URL` | No (`http://127.0.0.1:8090/health/ready`) | URL used by the binary probe |
| `GATEWAY_HEALTHCHECK_TIMEOUT_MS` | No (`2000`) | Probe timeout |
| `GATEWAY_UPSTREAM_AUTH_GATE` | Yes | AuthGate base URL |
| `GATEWAY_UPSTREAM_RIDER_MANAGER` | Yes | RiderManager base URL |
| `GATEWAY_UPSTREAM_MOTO_HUB` | Yes | MotoHub base URL |
| `GATEWAY_UPSTREAM_RENTAL_OPERATIONS` | Yes | RentalOperations base URL |

Run `api-gateway --healthcheck` to execute the same self-probe used by Compose.
The command needs only the two healthcheck variables, not upstream settings.
