# ADR 0001 — Technology enters by workload, not by résumé

- **Status:** Accepted
- **Date:** 2026-08-27

## Context

The redesign spans six languages and five data stores. A system with that many
technologies and no written justification does not read as competence — it reads
as résumé-driven development, and an experienced reviewer identifies it in
thirty seconds. The breadth is only defensible if each choice answers a workload
this domain actually has.

## Decision

**A technology enters because a workload in this domain is served better by it
than by the alternatives, and the repository says which workload, in writing,
next to the code.**

| Capability | Choice | The workload that justifies it |
|---|---|---|
| Edge | Rust (Axum) | 100% of traffic passes through it and it parses hostile input continuously; predictable latency without GC pauses, memory safety where the input is untrusted |
| Transactional core | .NET 10 | Money, contracts and invariants — the part that must be boring and correct, with strong typing, versioned migrations and the best test tooling in the set |
| Live tracking | Elixir (Phoenix) | Tens of thousands of long-lived WebSocket connections with per-process fault isolation; `Presence` solves distributed "who is online" for free |
| Document pipeline | Rust | Hostile input, CPU-bound work, binary formats — the worst possible place for buffer overflows |
| Risk and pricing | Python (FastAPI) | The only genuinely statistical work in the system: OCR, fraud scoring, demand models |
| Console and BFF | Node (Next.js) | Pure I/O fan-out and shaping for the screen; shared TypeScript types remove a class of contract bug |
| Commands | RabbitMQ | A task with one owner needing explicit ack, retry and dead-lettering — smart broker, dumb consumer |
| Events | Kafka | A fact with many readers needing retention and replay — dumb broker, smart consumer |
| Transactional store | Postgres-compatible | Serializable isolation, and a partial unique index that makes double-booking impossible in the database |
| Time series | Cassandra | Write-heavy, partition-scoped reads, data that ages out — the canonical case |
| Coordination | Redis | Rate limiting, token revocation, idempotency keys, distributed locks — shared low-latency state, not "the cache" |

The command-versus-event split is the decision most likely to be challenged, so
it is made visible in naming: commands are `cmd.*` queues, events are
past-tense topics.

## Alternatives considered

- **One language for everything.** Simpler to operate and to hire for. Rejected
  here because demonstrating the fit between workload and runtime is part of the
  point — but this is the choice most production teams should make.
- **One broker instead of two.** Defensible in a real company. Kept as two
  because the value is showing *why* they differ; that obliges the rule above to
  hold everywhere, or the argument collapses.
- **DynamoDB for projections.** Cheaper and more integrated. Rejected under ADR
  0004: no protocol equivalent elsewhere, and the access-key modelling does not
  translate.

## What was explicitly rejected

- **MongoDB.** It was in the original design and is the most replaceable piece
  in the set — the read projections fit in `JSONB` on the primary store. Removing
  it takes a whole database out of the architecture, and the projections stay
  disposable and rebuildable from Kafka.
- **A service mesh.** Circuit breaking, timeouts, mTLS and routing are already
  covered by the gateway and per-service libraries, with the code visible. The
  operational weight does not pay for itself here.

## Consequences

- Eleven technologies is a real operational cost. It is contained by profiles:
  the default stack is the core; everything else is opt-in.
- Services must degrade rather than fail when an optional one is down — the
  declared degradation table in ADR 0003 is the contract.
- Breadth invites a "broad and shallow" reading. The countermeasure is one
  deliberately deep column: distributed consistency, written up and proven by
  tests.

## Follow-up

- [Epic 5 — Transactional core and distributed consistency](https://github.com/iVega123/ProjectY/issues/6)
- [Epic 9 — Remaining polyglot services](https://github.com/iVega123/ProjectY/issues/10)
- [One ADR per service justifying its language](https://github.com/iVega123/ProjectY/issues/77)
