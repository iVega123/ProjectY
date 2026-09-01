# ADR 0015 — Event contracts: keys, compatibility and carried state

- **Status:** Accepted
- **Date:** 2026-09-01

## Context

ADR 0001 splits commands from events: RabbitMQ carries a task with one owner,
Kafka carries a fact with many readers. That settles the transport. It does not
settle what is inside a message, what may change about it, or how a consumer in
a different language survives a producer's upgrade — and with seven languages
reading these topics, those are the questions that decide whether the split
holds up in year two.

## Decision

### Events carry state, and the rule for what goes in is narrow

`rental.closed` carries the rider's name, the plate and the final cost, so
`billing` can issue an invoice without a single synchronous call. The rule that
keeps the payload from growing without limit:

> **Does the field describe the fact, or serve a consumer?**

The rider's name describes who rented — it goes in. The rider's phone number
serves `billing`'s intent to send an email — it does not.

**A carried field is a dated fact, not a cache.** If the rider changes their
name after the invoice is issued, the invoice does not change: it records who
rented, not who that person is today. Staleness is not a defect to be
engineered away here; it is the correct semantics.

### A producer may only carry what it owns or projects

`rental-core` does not own the rider's name, so it cannot put it in an event
unless it already holds it. It therefore keeps a **rider projection** — the
narrow set of fields it needs to make a decision or describe a fact it emits:

| Field | Why it is there |
|---|---|
| `rider_id` | key |
| `verified`, `verified_at` | decides whether renting is allowed at all |
| `risk_score`, `scored_at` | decides the tier (ADR 0016) |
| `rider_name` | goes into `rental.closed`; describes the fact |

The projection is fed by `rider.verified` and `risk.scored`, and deduplicated by
the `inbox` table the schema already has. This makes `rental-core` a Kafka
*consumer* as well as a producer.

### The partition key is per topic, and it is always an immutable id

| Topic | Key |
|---|---|
| `motorcycle.created`, `motorcycle.retired`, `rental.created`, `rental.closed` | `motorcycle_id` |
| `rider.registered`, `rider.verified`, `risk.scored`, `document.stored`, `document.verified` | `rider_id` |
| `invoice.issued` | `rental_id` |

**Never the licence plate.** It is a business identifier and this system has
already rewritten a batch of them — the `CanonicalizeLegacyMotorcyclePlates`
migration in MotoHub. A key that can be corrected is a key that reshards the
topic and breaks ordering exactly at the moment of the correction. For the same
reason `rentals` now references `motorcycles (id)` rather than the plate.

### The registry is beside the message path, never inside it

The producer resolves a schema id from the registry once and caches it,
serialises locally, and publishes straight to Kafka. The consumer reads the
schema id out of the payload and resolves it the same way. **The registry is
never a hop between a producer and the broker** — it is neither a single point
of failure for publishing nor a latency contributor.

### Compatibility is FULL, and Avro is the encoding

**FULL**, both directions, because both directions are real here: an old
consumer must survive the producer's upgrade (forward), and a new consumer must
be able to replay the topic's history (backward). Since these topics are
retained and replayable by deliberate choice, BACKWARD alone would only
guarantee half of what the design already promises.

**Avro** for events: evolution by named field with defaults behaves uniformly
across seven languages, and it is the canonical pairing with a registry.

## Alternatives considered

- **Thin events plus a lookup call.** Simpler payloads, and the consumer always
  reads current data. Rejected: it reintroduces the synchronous dependency the
  event split exists to remove, and it makes an invoice depend on the rider
  service being up months after the rental closed.
- **A single global partition key.** Tempting for its simplicity, and wrong:
  `rider.verified` has no motorcycle in it.
- **BACKWARD-only compatibility.** It means every consumer upgrades before the
  producer. Across seven services on independent release cadences, that is the
  wrong direction to force.
- **Protobuf for events as well as RPC.** Rejected as a false economy: an RPC
  contract and an event contract have different lifetimes and different
  compatibility rules. If gRPC appears between services, Protobuf there is a
  separate decision, not this one.

## Consequences

- **There is no ordering between topics, only within a partition.** A consumer
  reading `rider.verified` and `risk.scored` sees no order between them — and
  must not need one. `rental-core` decides from the state of its projection at
  the moment it handles the command, never from the order events arrived in.
- **A projection that lags will refuse a rental.** A rider verified moments ago
  may not be verified in `rental-core` yet. This fails closed, with an explicit
  "awaiting processing" answer rather than a generic denial.
- **Duplicated state is now a real thing to reason about**, and the two rules
  above are what keep it bounded: only what the producer needs, and only as a
  fact at a point in time.
- **CI blocks incompatible schemas.** A job runs the registry's compatibility
  check before merge, in the same spirit as `schema-portability`: the guarantee
  is worth what its test is worth.

## Follow-up

- [ADR 0009 — Exactly-once effect](0009-exactly-once-effect.md) — the outbox and
  inbox this record builds on
- [ADR 0016 — Risk scoring off the request path](0016-risk-scoring-off-the-request-path.md)
