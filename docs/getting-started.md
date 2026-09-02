# Running the audited baseline locally

This guide starts the original four-service system that was reviewed in the
[architecture and security audit](AUDITORIA-ARQUITETURA-SEGURANCA.md), with the
Rust gateway in front as the first strangler-migration component. The remaining
topology under `deploy/` is still a design scaffold for services that have not
been implemented yet.

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
| Elasticsearch | `9200`, `9300` |
| Logstash | `5000` |
| Kibana | `5601` |
| PostgreSQL | `5432` |
| Redis | `6379` |
| pgAdmin | `5050` |
| MongoDB | `27017` |
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
is ignored by Git and includes independent JWT signing keys and RabbitMQ
credentials for every service:

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
The PostgreSQL bootstrap creates independent databases for AuthGate,
RiderManager, and MotoHub so that each EF context owns its schema. The same
command creates an ignored `.rabbitmq-definitions.json` containing salted
password hashes, isolated vhosts, and service-specific queue permissions. The
rider and rental message flows use separate vhosts so access to the AMQP
default exchange cannot cross domain boundaries. Both ignored files are
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

PostgreSQL must become healthy before the one-shot AuthGate, MotoHub, and
RiderManager migration containers run. Each application starts only after its
migration container exits successfully. See
[PostgreSQL migration operations](database-migrations.md) for baseline adoption,
schema changes, and rollback.

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

The audited baseline Compose stack runs every application in `Production`, so
Swagger and the developer exception page are disabled. Local IDE launch
profiles use `Development`; their `appsettings.Development.json` files enable
Swagger explicitly. Both conditions are required: setting `Swagger:Enabled`
alone never exposes Swagger from a `Production` process. Set
`SWAGGER_ENABLED=false` in `.env` to disable Swagger in the self-hosted
development overlay without editing its Compose files.

Application traffic can now enter through the gateway. Existing service ports
remain published temporarily so the migration can proceed without modifying
the domain services in task #57; a later epic task removes direct trust and
centralizes identity verification at the edge. The gateway routes
`/api/auth/**`, `/api/riders/**`, `/api/motorcycles/**`, and `/api/rental/**` to
their current owners.

Only the HTTP endpoints above are documented as usable. The compose file also
publishes ports that older documentation described as HTTPS, but it configures
no certificates or HTTPS listener; the audit records this as finding A4.

The gateway already enforces the target EdDSA/JWKS trust boundary. The legacy
.NET AuthGate still emits HMAC tokens, so authenticated traffic continues to use
the directly published legacy ports until the Go identity service in issue #136
provides the issuer and JWKS. The gateway rejects those legacy tokens instead of
silently weakening the new boundary; login and rider registration remain public
through port `8090`.

Once the identity issuer is available, rental creation also requires Redis for
the immediate-revocation check defined by ADR 0017. The denylist key is
`projecty:revoked:jti:<jti>` and expires no later than the access token. Redis
failure blocks only that high-value operation; ordinary token verification
continues from the bounded JWKS cache.

## Bootstrap the first administrator

Administrator creation is deliberately unavailable over HTTP. Open a second
terminal in the repository root after the Compose stack is running. Set the
bootstrap credentials only in that shell, then start a one-off AuthGate
container in bootstrap mode:

```powershell
$env:BootstrapAdmin__Email = Read-Host "Administrator email"
$bootstrapPassword = Read-Host "Administrator password" -AsSecureString
$env:BootstrapAdmin__Password = [System.Net.NetworkCredential]::new('', $bootstrapPassword).Password

try {
    docker compose run --rm `
        -e BootstrapAdmin__Email `
        -e BootstrapAdmin__Password `
        auth-gate --bootstrap-admin
}
finally {
    Remove-Item Env:BootstrapAdmin__Email -ErrorAction SilentlyContinue
    Remove-Item Env:BootstrapAdmin__Password -ErrorAction SilentlyContinue
}
```

Compose passes the two shell variables only to the one-off process. That
container joins the same Compose network and reuses the AuthGate service
configuration, so the PostgreSQL hostname `postgres` resolves correctly. It
does not publish another copy of the AuthGate ports and is removed after the
command exits.

The command creates the `Admin` role when needed, creates or promotes the
configured account, and exits. The `finally` block removes both plaintext
values from the shell even if bootstrap fails.

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

The modernization Compose graph waits for real health instead of elapsed time.
Tempo and Loki become healthy first, followed by the OpenTelemetry Collector,
Prometheus, and Grafana. Application services wait for a healthy collector and
for each infrastructure dependency they use through Toxiproxy.

The observability portion can be exercised before the modernization service
builds land:

```bash
docker compose --env-file .env -f deploy/overlays/selfhost/compose.yaml up --build -d \
  tempo loki otel-collector prometheus grafana toxiproxy
docker compose --env-file .env -f deploy/overlays/selfhost/compose.yaml ps

# Dependents must retain their container IDs and return to all-green.
docker compose --env-file .env -f deploy/overlays/selfhost/compose.yaml ps -q \
  otel-collector prometheus grafana
docker compose --env-file .env -f deploy/overlays/selfhost/compose.yaml restart loki
docker compose --env-file .env -f deploy/overlays/selfhost/compose.yaml ps -q \
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

- The root `docker-compose.yml` is the audited legacy baseline, not a production
  deployment definition. It requires secrets from the local environment and
  contains no committed secret defaults.
- Supporting databases, queues, object storage, and observability tools publish
  host ports with development settings.
- The modernization topology in `deploy/overlays/selfhost/compose.yaml` is not
  runnable until its referenced services land.

Use the [audit correction order](AUDITORIA-ARQUITETURA-SEGURANCA.md)
and the [modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
to follow the path from this baseline to the target system.
