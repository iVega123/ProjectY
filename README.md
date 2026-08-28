# ProjectY

> A case study in turning an audited distributed system into evidence-based architecture.

ProjectY started as four .NET microservices for motorcycle rentals: identity,
fleet, rider management, and rental operations. A manual review of the code,
containers, and delivery pipeline found **42 issues: 5 critical security flaws,
11 high-risk findings, 14 architectural gaps, and 12 delivery or code-quality
problems**. This repository preserves that real baseline and documents the
decisions used to redesign it.

**Start with the evidence:** [read the architecture and security audit](docs/AUDITORIA-ARQUITETURA-SEGURANCA.md),
follow the [ADR consolidation work](https://github.com/iVega123/ProjectY/issues/15),
or browse the [modernization program](https://github.com/iVega123/ProjectY/issues/2).
The accompanying [article series](https://github.com/iVega123/ProjectY/issues/13)
turns the same work into public technical narratives.

## The five decisions that shape the redesign

| Decision | What was rejected | Why | Decision trail |
|---|---|---|---|
| Put rental correctness in the database and use an outbox/inbox around events | Application-only pre-checks, in-memory locks, and claims of transport-level "exactly once" | Those approaches do not protect concurrent writers or the gap between committing data and publishing an event | [Transactional core](https://github.com/iVega123/ProjectY/issues/6) |
| Establish one edge trust boundary | Four editable copies of token and role validation inside domain services | The copies had already diverged and produced an authorization bypass | [Rust edge gateway](https://github.com/iVega123/ProjectY/issues/7) |
| Optimize the local feedback loop before adding cloud infrastructure | Premature Kubernetes, manual setup steps, and sleep-based startup ordering | A demonstrable system must start predictably and make source changes cheap | [Local development loop](https://github.com/iVega123/ProjectY/issues/5) |
| Treat failure tolerance as an observable behavior | Generic retry policies and dashboards that cannot be traced back to a request | Failure claims are credible only when they can be injected, observed, and reproduced | [Observability](https://github.com/iVega123/ProjectY/issues/8) and [fault-tolerance drills](https://github.com/iVega123/ProjectY/issues/9) |
| Keep deployment variants in overlays and choose dependencies by protocol | Long-lived environment branches and vendor-specific contracts in workloads | Branches drift; protocol boundaries preserve a credible self-hosted path and an ephemeral cloud path | [Local Kubernetes](https://github.com/iVega123/ProjectY/issues/11) and [AWS cost profiles](https://github.com/iVega123/ProjectY/issues/12) |

These links currently point to the implementation work items. Stable,
numbered decision records are being assembled under
[`docs/adr/`](https://github.com/iVega123/ProjectY/issues/15), beginning with
the audit as ADR 0000.

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
| Original observability | Elasticsearch, Logstash, and Kibana |
| Modernization scaffold | A container topology under `deploy/` for the planned gateway, transactional core, fault injection, and LGTM observability stack |
| Decision records | Being normalized and numbered in [issue #15](https://github.com/iVega123/ProjectY/issues/15) |

The modernization compose file is a design scaffold: it references services
that have not landed yet. It is deliberately not presented as a working demo.
The root compose file runs the audited baseline only.

## Run locally

See [Running the audited baseline locally](docs/getting-started.md). This code
contains known security flaws and development credentials; use it only in an
isolated local environment.

## Follow the work

- [Epic 1: repository repositioning](https://github.com/iVega123/ProjectY/issues/2)
- [All modernization epics](https://github.com/iVega123/ProjectY/issues?q=is%3Aissue%20state%3Aopen%20label%3Aepic)
- [Architecture decision records](https://github.com/iVega123/ProjectY/issues/15)
- [Six-part article series](https://github.com/iVega123/ProjectY/issues/13)
