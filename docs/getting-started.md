# Running the audited baseline locally

This guide starts the original four-service system that was reviewed in the
[architecture and security audit](AUDITORIA-ARQUITETURA-SEGURANCA.md), with the
Rust gateway in front and the LGTM observability stack running as the first
strangler-migration components. `deploy/base/compose.yaml` imports the same
application model as the root entrypoint; the self-hosted overlay selects the
gateway's production image. There is no second, unimplemented application
topology.

**The strangler migration is finished.** None of the four audited ASP.NET
services is left: `MotoHub` and `RentalOperations` became `rental-core` (#135),
and `AuthGate` and `RiderManager` became the Go `identity` service (#136). Every
application store is now CockroachDB, whose schema lives in `deploy/db/sql` and
is applied by `cockroach-init`. PostgreSQL and pgAdmin left with the last
services that used them; the CockroachDB console on
[localhost:26080](http://localhost:26080) replaces pgAdmin.

`tilt up` enables live updates for the .NET services, gateway and media service.
`tilt up -- --full` adds the existing telemetry, risk-pricing and console services.
The rental application’s OTel resource and Compose/DNS name are both
`rental-core`.

## Safety warning

The baseline still contains known security findings and exposes several
infrastructure services on host ports. Run it only on an isolated development
machine. Do not expose it to the internet or deploy it to a shared or
production environment. RabbitMQ is an exception: it is reachable only from
the Compose network.

## Prerequisites

- Git
- Docker Engine or Docker Desktop
- Docker Compose v2 (`docker compose`)
- Every host port listed in the next section must be available

## Required host ports

The root Compose file publishes application, data, messaging, storage, and
observability ports. A conflict on any one of them prevents the stack from
starting.

| Infrastructure service | Required host ports |
|---|---|
| OpenTelemetry Collector | `4317`, `4318`, `8889` |
| Prometheus | `9090` |
| Tempo | `3200` |
| Loki | `3100` |
| Grafana | `3000` |
| PostgreSQL | `5432` |
| Redis | `6379` |
| pgAdmin | `5050` |
| CockroachDB | `26257`, `26080` |
| MinIO | `9000`, `9001` |

| Application service | Required host ports |
|---|---|
| API Gateway | `8090` |
| Auth Gate | `8080`, `8181` |
| Rider Manager | `8000`, `8001` |
| MotoHub | `8100`, `8101` |
| Rental Operations | `8200`, `8201` |

The secondary application ports (`8181`, `8001`, `8101`, and `8201`)
are still published and therefore must be free, even though the current
containers do not configure usable HTTPS listeners on them. If a port is
already occupied, stop the conflicting local service or change the matching
host-side mapping in `docker-compose.yml` before starting the stack.

## Start the stack

Clone the repository, then create fresh local credentials. The generated `.env`
is ignored by Git and includes the gateway identity-envelope key, the key that
seals identity's signing seeds, the bootstrap administrator password, and
RabbitMQ credentials for every service that still uses the broker:

```bash
git clone https://github.com/iVega123/ProjectY.git
cd ProjectY
```

```powershell
powershell -ExecutionPolicy Bypass -File scripts/New-LocalSecrets.ps1
```

On PowerShell 7, `pwsh -File scripts/New-LocalSecrets.ps1` is equivalent. If a
local `.env` already exists, the script refuses to overwrite it. Use `-Force`
only for an intentional full rotation, and recreate persistent volumes that
were initialized with the previous database, broker, or storage credentials.
The same command creates an ignored `.rabbitmq-definitions.json` containing
salted password hashes, isolated vhosts, and service-specific queue permissions.
The rental message flow has its own vhost so access to the AMQP default exchange
cannot cross domain boundaries. Both ignored files are
required before the first Compose startup.

RabbitMQ does not publish its AMQP or management ports to the host. To inspect
it locally, run management commands inside the container or attach a temporary
tool to the Compose network instead of adding a permanent host port mapping.
The generated definitions declare the rider and licence-update queues as
durable. When upgrading a stack that created those queues as non-durable,
restart RabbitMQ before the application services so the old queues disappear;
RabbitMQ cannot change queue durability in place.

Then start the stack:

```bash
docker compose up --build
```

Tilt uses the same Compose model and adds live update for the Rust gateway:

```bash
tilt up
```

`cockroach-init` applies `deploy/db/sql` before any application starts, and
each service is health-gated on the dependencies it actually needs. There are no
migration containers left: the schema is a set of files the CI proves portable,
not a set of EF migrations per service.

The first build downloads the service and infrastructure images, so its duration
depends on the network connection and Docker cache.

## Application endpoints

| Service | Local URL |
|---|---|
| API Gateway | <http://localhost:8090> |
| Auth Gate | <http://localhost:8080> |
| Rider Manager | <http://localhost:8000> |
| MotoHub | <http://localhost:8100> |
| Rental Operations | <http://localhost:8200> |

Grafana is available at <http://localhost:3000>. Sign in with the generated
`GRAFANA_USER` and `GRAFANA_PASSWORD` values from `.env`; both ProjectY
dashboards and the Prometheus, Tempo, and Loki datasources are provisioned at
startup.

The four ASP.NET Core services instrument inbound requests and outbound
`HttpClient` calls with the OpenTelemetry SDK. The Rust gateway creates a
server span for each proxied request and propagates its W3C trace context to the
upstream. Database commands emitted through EF Core and Npgsql are child spans
of the request that triggered them. RabbitMQ trace
context is captured in each transactional outbox row, continued by a producer
span when the relay publishes, and restored by consumers; bounded retries keep
the same W3C `traceparent` and `tracestate` headers. The same carrier contract
applies to Kafka services as they are introduced; no application Kafka producer
or consumer exists in the current tree.

Both runtimes keep structured JSON console output and also export logs and
traces over OTLP to the Collector. The rental SLO dashboard selects the active
`rental-core` service and its `POST /api/Rental/create` span.

The audited baseline Compose stack runs every application in `Production`, so
Swagger and the developer exception page are disabled. Local IDE launch
profiles use `Development`; their `appsettings.Development.json` files enable
Swagger explicitly. Both conditions are required: setting `Swagger:Enabled`
alone never exposes Swagger from a `Production` process. Set
`SWAGGER_ENABLED=false` in `.env` to disable Swagger in the self-hosted
development overlay without editing its Compose files.

Application traffic enters through the gateway. Existing service ports remain
published temporarily for operational migration, but the domain APIs reject
direct calls without a fresh, gateway-signed identity envelope. The gateway routes
`/api/auth/**`, `/api/riders/**`, `/api/motorcycles/**`, and `/api/rental/**` to
their current owners.

Only the HTTP endpoints above are documented as usable. The compose file also
publishes ports that older documentation described as HTTPS, but it configures
no certificates or HTTPS listener; the audit records this as finding A4.

The gateway already enforces the target EdDSA/JWKS trust boundary and strips
credentials before forwarding a short-lived signed identity envelope. Domain
services do not parse JWTs; they verify that envelope and apply only role and
resource-ownership rules. Calls between domain services propagate the verified
identity through the same signed envelope, replacing the former API keys.

Tokens come from the Go `identity` service, which also owns the rider domain. It signs with Ed25519, publishes
its public keys at `/.well-known/jwks.json`, and the gateway selects the key by
`kid` from a bounded cache. It runs in the polyglot overlay, next to the
CockroachDB it reads its keys from, and listens on `8095`.

```bash
curl -s localhost:8095/api/auth/login -H 'content-type: application/json' \
  -d '{"email":"admin@projecty.local","password":"<IDENTITY_ADMIN_PASSWORD from .env>"}'
```

The access token that comes back is accepted by the gateway on every proxied
route. The refresh token is opaque, single-use, and stored hashed in
CockroachDB — reusing one after it has been exchanged revokes the whole session,
because there is no way to tell the victim from the thief.

The gateway routes `/api/auth/**`, `/api/riders/**` and `/update-image` there.
Credential and rider live in one process because the token's `sub` **is** the
rider identifier — keeping them apart forced one side to hold a copy of the
other, which is [ADR 0023](adr/0023-the-rider-record-lives-with-the-credential.md).

Once the identity issuer is available, rental creation also requires Redis for
the immediate-revocation check defined by ADR 0017. The denylist key is
`projecty:revoked:jti:<jti>` and expires no later than the access token. Redis
failure blocks only that high-value operation; ordinary token verification
continues from the bounded JWKS cache.

The gateway rate limiter is already active for public and protected routes. It
uses an atomic Redis token bucket shared by every gateway replica, with a
stricter bucket for `/api/auth/**`. Unlike the high-value denylist, it fails
open: stopping Redis allows ordinary traffic, omits the remaining-token header,
and increments `gateway_ratelimit_degraded_total` on the gateway `/metrics`
endpoint and the platform Grafana dashboard.

The gateway also isolates each legacy upstream independently. Requests have a
low per-attempt timeout, acquire a bulkhead permit without queueing, and open a
circuit after repeated transport errors, timeouts, or `5xx` responses. Safe
methods and requests carrying `Idempotency-Key` retry with exponential full
jitter. Saturated or open dependencies are shed with `503` and `Retry-After`;
an open breaker is visible in `/health/ready`, `/metrics`, and Grafana without
making the whole gateway unready.

## The first administrator

There is no HTTP route that creates one, and there is no one-off container
either. `identity` reads `IDENTITY_ADMIN_EMAIL` and `IDENTITY_ADMIN_PASSWORD` on
start and creates that account if it does not exist — idempotent, because a
stack that restarts must neither fail nor overwrite the password of someone who
has already signed in. `scripts/New-LocalSecrets.ps1` generates the password
into `.env`; the account is `admin@projecty.local`.

## Verify health probes

Each application exposes three separate endpoints:

| Endpoint | Question | Dependency failure |
|---|---|---|
| `/health/live` | Is the process responding? | Stays healthy |
| `/health/ready` | Can required infrastructure be reached? | Becomes unhealthy |
| `/health/startup` | Has application startup completed? | Unchanged after startup |

Compose uses `/health/ready` for container health. The probe command runs through
the application assembly itself, so the chiseled images do not need a shell,
`curl`, or `wget`.

Manual readiness drill:

```bash
docker compose up --build -d
curl --fail http://localhost:8000/health/live
curl --fail http://localhost:8000/health/ready
curl --fail http://localhost:8000/health/startup

docker compose stop rabbitmq
curl --fail http://localhost:8000/health/live
curl --fail http://localhost:8000/health/ready # expected to fail with HTTP 503

docker compose start rabbitmq
```

The process remains live while RabbitMQ is unavailable, but readiness removes it
from rotation. Inter-service HTTP upstreams are deliberately excluded from
readiness: a circuit breaker opening for one upstream must not disable unrelated
routes served by the same process.

## Verify startup gates

The root Compose and Tilt graphs wait for real health instead of elapsed time.
Tempo and Loki become healthy first, followed by the OpenTelemetry Collector,
Prometheus, and Grafana. Application services wait for a healthy collector and
for each infrastructure dependency they use.

The observability portion can be exercised independently:

```bash
docker compose --env-file .env up --build -d \
  tempo loki otel-collector prometheus grafana
docker compose --env-file .env ps

# Dependents must retain their container IDs and return to all-green.
docker compose --env-file .env ps -q \
  otel-collector prometheus grafana
docker compose --env-file .env restart loki
docker compose --env-file .env ps -q \
  otel-collector prometheus grafana
```

The IDs before and after the Loki restart must match. Compose health conditions
gate initial startup only; they do not cascade a dependency restart into healthy
dependents. No fixed delay is used in the self-hosted Compose overlay.

## Stop the stack

Press `Ctrl+C` in the attached Compose session, then run:

```bash
docker compose down
```

Named volumes are retained so local database contents survive the restart.

## Known limitations

- The root `docker-compose.yml` combines the audited services with the gateway
  and LGTM stack; it is not a production deployment definition. It requires
  secrets from the local environment and contains no committed secret defaults.
- Supporting databases, queues, object storage, and observability tools publish
  host ports with development settings.
- The modernization topology in `deploy/overlays/selfhost/compose.yaml` is not
  runnable until its referenced services land.

Use the [audit correction order](AUDITORIA-ARQUITETURA-SEGURANCA.md)
and the [modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
to follow the path from this baseline to the target system.
