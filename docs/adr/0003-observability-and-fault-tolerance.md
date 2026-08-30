# ADR 0003 — Failure behaviour is declared before it is implemented

- **Status:** Accepted
- **Date:** 2026-08-28

## Context

One user action crosses four runtimes and two brokers. Without end-to-end
tracing that is undebuggable at the first integration bug, and retrofitting
instrumentation once six runtimes exist is a project of its own.

The audited baseline had the opposite of this: ELK storing only logs, on an
unauthenticated port, with no traces and no metrics; no health checks anywhere;
inter-service HTTP clients with no timeout, no retry and no circuit breaker, so
the 100-second default applied; and a `BasicNack(requeue: true)` that returned
poison messages to the head of the queue in a hot loop.

## Decision

**Every service speaks OTLP to a collector, and the failure behaviour of every
dependency is written down before it is built.**

Observability:

- Traces to Tempo, metrics to Prometheus, logs to Loki, all via one collector —
  services never learn which backend exists, so swapping one is configuration.
- RED metrics derive from traces via the spanmetrics connector. Nobody has to
  remember to increment a counter when adding an endpoint, and six runtimes
  produce numbers with the same definition.
- Trace context is carried in RabbitMQ and Kafka headers, so the span chain
  survives the queue — the half most projects quietly break.
- An SLO with an error budget and multi-window burn rate. A dashboard informs;
  an error budget decides.

Fault tolerance, per layer:

- **Edge:** per-upstream timeout, circuit breaker, bulkhead that refuses rather
  than queues, retry with full jitter on idempotent requests only.
- **Domain:** outbox and inbox in the same transaction as the aggregate change,
  giving an exactly-once *effect* without any broker promising exactly-once
  delivery.
- **Messaging:** durable queues, persistent messages, publisher confirms, and a
  real dead-letter exchange with backoff via TTL in the broker rather than
  `sleep` in the consumer.
- **Process:** three probes, and graceful shutdown that drains in-flight
  requests and flushes telemetry before exiting.

**The declared degradation table is the contract.** Written before implementing,
it is true; written afterwards, it is optimistic fiction. Every degraded path
increments a distinct metric, so degradation is visible rather than silent.

| Dependency down | Stops | Continues |
|---|---|---|
| Redis | Rate limiting and the read cache | Everything, with a degradation counter rising |
| Risk and pricing | Demand-based pricing | Rentals close on the fixed daily rate |
| Live tracking | The live map | Last known position, served from Redis |
| Read projections | Pre-built documents | Reads fall back to the primary store, slower |
| Kafka | Event propagation | Transactional writes continue; the outbox drains on recovery |
| Cassandra | Trip history | Current position and rental creation |
| Primary database | Rental creation and closure | Reads from projections; everything else refuses fast with `Retry-After` |

Fallbacks are explicit code paths, not accidents of exception handling. A row
reading "unknown" is a defect, not an omission.

Two dependencies fail in deliberately opposite directions. Rate limiting **fails
open** — without Redis the request passes and a degradation counter rises,
because losing the limiter beats losing availability. Token verification **fails
closed** — without Redis there is no way to prove a token was not revoked, so it
is refused. Security does not degrade to "probably fine".

## Alternatives considered

- **Keeping ELK.** Covers one of three signals, and correlating across them is
  the entire value.
- **Hand-instrumented metrics per service.** Six runtimes, six definitions of
  "a request", and drift within a month.
- **Asserting resilience without drills.** What every portfolio does. Toxiproxy
  sits between every service and every dependency so a drill is a button, not a
  rebuild.

## What was explicitly rejected

Claiming exactly-once delivery. No broker provides it. The outbox gives
at-least-once delivery; the inbox turns that into an exactly-once effect on the
consumer. Saying it precisely is part of the point.

## Consequences

- Toxiproxy indirection is development-only; production uses AWS FIS.
- Consumer state (retry counts, upload reassembly) must live outside process
  memory, or a second replica is impossible — this is a real constraint the
  baseline violated.
- Every row of the degradation table is a test. A row that says "unknown" is a
  defect.

## Follow-up

- [Epic 7 — Observability and error budget](https://github.com/iVega123/ProjectY/issues/8)
- [Epic 8 — Demonstrable fault tolerance](https://github.com/iVega123/ProjectY/issues/9)
