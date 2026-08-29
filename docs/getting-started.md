# Running the audited baseline locally

This guide starts the original four-service system that was reviewed in the
[architecture and security audit](AUDITORIA-ARQUITETURA-SEGURANCA.md). It does
not start the modernization topology under `deploy/`; that file is currently a
design scaffold and references services that have not been implemented yet.

## Safety warning

The baseline still contains known security findings and exposes infrastructure
services on host ports. Run it only on an isolated development machine. Do not
expose it to the internet or deploy it to a shared or production environment.

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
| pgAdmin | `5050` |
| MongoDB | `27017` |
| RabbitMQ | `5672`, `15672` |
| MinIO | `9000`, `9001` |

| Application service | Required host ports |
|---|---|
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
is ignored by Git and includes independent JWT signing keys for all four
services:

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

Then start the stack:

```bash
docker compose up --build
```

The first build downloads the service and infrastructure images, so its duration
depends on the network connection and Docker cache.

## Application endpoints

| Service | Local URL |
|---|---|
| Auth Gate | <http://localhost:8080/swagger> |
| Rider Manager | <http://localhost:8000/swagger> |
| MotoHub | <http://localhost:8100/swagger> |
| Rental Operations | <http://localhost:8200/swagger> |

Only the HTTP endpoints above are documented as usable. The compose file also
publishes ports that older documentation described as HTTPS, but it configures
no certificates or HTTPS listener; the audit records this as finding A4.

## Bootstrap the first administrator

Administrator creation is deliberately unavailable over HTTP. Set `BootstrapAdmin__Email` and
`BootstrapAdmin__Password` in the process environment, then run AuthGate once in bootstrap mode:

```bash
dotnet run --project AuthGate/AuthGate -- --bootstrap-admin
```

The command creates the `Admin` role when needed, creates or promotes the configured account, and
exits. Remove both environment variables from the shell after the command completes.

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
- There are no reliable health gates; startup currently depends on fixed waits.
- The modernization topology in `deploy/compose.yaml` is not runnable until its
  referenced services land.

Use the [audit correction order](AUDITORIA-ARQUITETURA-SEGURANCA.md)
and the [modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
to follow the path from this baseline to the target system.
