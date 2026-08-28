# Running the audited baseline locally

This guide starts the original four-service system that was reviewed in the
[architecture and security audit](AUDITORIA-ARQUITETURA-SEGURANCA.md). It does
not start the modernization topology under `deploy/`; that file is currently a
design scaffold and references services that have not been implemented yet.

## Safety warning

The baseline contains known critical vulnerabilities, committed development
credentials, and infrastructure services exposed on host ports. Run it only on
an isolated development machine. Do not expose it to the internet or deploy it
to a shared or production environment.

## Prerequisites

- Git
- Docker Engine or Docker Desktop
- Docker Compose v2 (`docker compose`)
- Free local ports for the services listed below

## Start the stack

```bash
git clone https://github.com/iVega123/ProjectY.git
cd ProjectY
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

## Stop the stack

Press `Ctrl+C` in the attached Compose session, then run:

```bash
docker compose down
```

Named volumes are retained so local database contents survive the restart.

## Known limitations

- The root `docker-compose.yml` is the audited legacy baseline, not a secure
  deployment definition.
- Supporting databases, queues, object storage, and observability tools publish
  host ports with development settings.
- There are no reliable health gates; startup currently depends on fixed waits.
- The modernization topology in `deploy/compose.yaml` is not runnable until its
  referenced services land.

Use the [audit correction order](AUDITORIA-ARQUITETURA-SEGURANCA.md)
and the [modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
to follow the path from this baseline to the target system.
