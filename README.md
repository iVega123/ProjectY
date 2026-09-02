# ProjectY

> A case study in turning an audited distributed system into evidence-based architecture.

ProjectY started as four .NET microservices for motorcycle rentals: identity,
fleet, rider management, and rental operations. A manual review of the code,
containers, and delivery pipeline found **42 issues: 5 critical security flaws,
11 high-risk findings, 14 architectural gaps, and 12 delivery or code-quality
problems**. This repository preserves that real baseline and documents the
decisions used to redesign it.

**Start with the evidence:** [read the architecture and security audit](docs/AUDITORIA-ARQUITETURA-SEGURANCA.md),
then the [architecture decision records](docs/adr/README.md) that answer it,
or browse the [modernization program](https://github.com/iVega123/ProjectY/issues/2).
The accompanying [article series](https://github.com/iVega123/ProjectY/issues/13)
turns the same work into public technical narratives.

## The five decisions that shape the redesign

| Decision | What was rejected | Why | Decision trail |
|---|---|---|---|
| Put rental correctness in the database and use an outbox/inbox around events | Application-only pre-checks, in-memory locks, and claims of transport-level "exactly once" | Those approaches do not protect concurrent writers or the gap between committing data and publishing an event | [ADR 0001](docs/adr/0001-polyglot-technology-choices.md), [ADR 0003](docs/adr/0003-observability-and-fault-tolerance.md) |
| Establish one edge trust boundary | Four editable copies of token and role validation inside domain services | The copies had already diverged and produced an authorization bypass | [ADR 0008](docs/adr/0008-single-trust-boundary.md) |
| Optimize the local feedback loop before adding cloud infrastructure | Premature Kubernetes, manual setup steps, and sleep-based startup ordering | A demonstrable system must start predictably and make source changes cheap | [ADR 0002](docs/adr/0002-development-loop-and-containers.md) |
| Treat failure tolerance as an observable behavior | Generic retry policies and dashboards that cannot be traced back to a request | Failure claims are credible only when they can be injected, observed, and reproduced | [ADR 0003](docs/adr/0003-observability-and-fault-tolerance.md) |
| Keep deployment variants in overlays and choose dependencies by protocol | Long-lived environment branches and vendor-specific contracts in workloads | Branches drift; protocol boundaries preserve a credible self-hosted path and an ephemeral cloud path | [ADR 0004](docs/adr/0004-cloud-portability-by-protocol.md), [ADR 0005](docs/adr/0005-repository-and-publication-strategy.md) |

Each row links to the record that argues it, including what was rejected and
at what cost. The full set is indexed in [`docs/adr/`](docs/adr/README.md),
beginning with the audit as ADR 0000.

## What the audit changed

The service boundaries looked reasonable on paper, but the review found that
the system had no effective perimeter. Anyone could register an administrator;
one service accepted any valid token on an admin path; secrets and a shared JWT
key were committed; rental ownership was not enforced; and queue payloads were
trusted as authenticated input.

The important outcome was not the list of findings. It was the correction
order: close the open entry points, rotate and externalize secrets, establish a
single trust boundary, make messaging durable and idempotent, and then remove
the duplicated security and messaging code. The full evidence, impact, and
source locations are in the
[audit](docs/AUDITORIA-ARQUITETURA-SEGURANCA.md).

## Repository state

| Area | Current state |
|---|---|
| Audited baseline | Four ASP.NET Core services: `AuthGate`, `MotoHub`, `RiderManager`, and `RentalOperations` |
| Data and messaging | PostgreSQL, MongoDB, RabbitMQ, and MinIO in the original local stack |
| Active observability | Application OTLP exporters, OpenTelemetry Collector, Prometheus, Tempo, Loki, and Grafana |
| Retired observability | The unauthenticated Elasticsearch, Logstash, and Kibana stack |
| Modernization scaffold | A container topology under `deploy/` for the planned transactional core and fault injection |
| Decision records | Eight records under [`docs/adr/`](docs/adr/README.md): 0000-0005 are the design trail, 0006-0007 are remediation decisions |

The modernization compose file is a design scaffold: it references services
that have not landed yet. The root compose file runs the audited services behind
the Rust gateway together with the LGTM observability stack.

## Branches

`main` carries every deployment variant as an overlay, so a fix lands once.
Branches named `article/NN-*` are **frozen citations** cut when an article is
published: they are never maintained and never accept pull requests. Target
`main` for any change. The reasoning is in
[ADR 0005](docs/adr/0005-repository-and-publication-strategy.md).

## Run locally

See [Running the audited baseline locally](docs/getting-started.md). This code
contains known security flaws and development credentials; use it only in an
isolated local environment.

Use the [telemetry correlation runbook](docs/observability-correlation.md) to
verify metric-to-trace, trace-to-log, log-to-trace, and service-graph links in
the running LGTM stack.

Database-backed tests use the shared
[PostgreSQL Testcontainers pattern](docs/testing/testcontainers-postgres.md).
State-changing API retries follow the shared
[`Idempotency-Key` contract](docs/api/idempotency.md).

### Modernization development loop

The root [`Tiltfile`](Tiltfile) organizes `docker-compose.yml` into
infrastructure, observability, setup, and service groups. It requires Docker
Compose 2.20 or newer, Tilt, and PowerShell (`powershell` on Windows or `pwsh`
on macOS/Linux). Start the audited services behind the Rust gateway, with LGTM
ready before application startup, using one command:

```bash
tilt up
```

On the first run, Tilt invokes `scripts/New-LocalSecrets.ps1` to generate the
ignored `.env` and `.rabbitmq-definitions.json` files with random local-only
credentials. Existing files are never overwritten, so later starts preserve
the same local data credentials.

The two files are one credential set. If exactly one is missing, Tilt stops
instead of silently rotating the other and desynchronizing persistent volumes.
To recover intentionally, stop the stack, remove volumes initialized with the
old credentials, and run
`powershell -ExecutionPolicy Bypass -File scripts/New-LocalSecrets.ps1 -Force`.

The Tilt UI is at <http://localhost:10350> and the gateway listens at
<http://localhost:8090>. Use `tilt down` to stop the stack. The one-shot EF Core
migration containers appear as setup resources; infrastructure, the audited
services and the temporary ELK stack are grouped separately.

Gateway source is synced into its Rust development image and rebuilt in place;
the Compose container restarts after a successful incremental build. The
release image built by CI uses the Dockerfile's final distroless, non-root stage
instead of the toolchain-bearing development stage.

There is not yet an honest cold-start time to publish. The first successful
start must still be timed on a clean Docker cache with the hardware and network
conditions recorded before a duration is presented as reproducible.

## Verify published images

After a change reaches `main`, CI publishes each changed service to GHCR with the
commit SHA and `latest` tags. Verification should use the immutable digest printed
by the workflow or returned by `docker buildx imagetools inspect`:

```bash
IMAGE=ghcr.io/ivega123/projecty/auth-gate
DIGEST=sha256:<published-digest>

cosign verify "$IMAGE@$DIGEST" \
  --certificate-identity="https://github.com/iVega123/ProjectY/.github/workflows/ci.yml@refs/heads/main" \
  --certificate-oidc-issuer="https://token.actions.githubusercontent.com"

gh attestation verify "oci://$IMAGE@$DIGEST" --repo iVega123/ProjectY
```

The same commands apply to `moto-hub`, `rental-operations`, and `rider-manager`
under `ghcr.io/ivega123/projecty/`. The keyless signature binds the image to this
repository's `main` workflow identity; the second command retrieves and verifies
its GitHub-hosted SLSA build provenance. See the
[container image security gate](docs/security/container-image-scanning.md) for
the scans that run before publication.

## Follow the work

- [Epic 1: repository repositioning](https://github.com/iVega123/ProjectY/issues/2)
- [All modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
- [Architecture decision records](docs/adr/README.md)
- [Six-part article series](https://github.com/iVega123/ProjectY/issues/13)
