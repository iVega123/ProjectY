# ProjectY — the target architecture, and the decisions behind it

- **Status:** proposal. What is already implemented is marked as such; the rest is plan.
- **Date:** 2026-09-01
- **Scope:** consolidates the "two systems, one repository" analysis and the decisions recorded in ADRs 0014 to 0017.

This is an index with context. Every decision of weight lives in its own ADR —
what is here is why each one went the way it did, in a paragraph, with the link.

---

## 1. The problem that started it

The repository held two disconnected realities: a .NET application that works
(four services, against Postgres and MongoDB) and a modern platform that was
declared but empty — fourteen infrastructure containers coming up correctly, and
six application services that did not exist. The strategy is **evolution in
floors**, not a rewrite: eliminate the contradictions in the documents first,
connect the two halves next, and only then touch the data model.

---

## 2. Target topology — 8 services, 7 languages

Each language enters through a workload that forces it ([ADR 0001](adr/0001-polyglot-technology-choices.md)).

| Service | Language | Responsibility | Store |
|---|---|---|---|
| **api-gateway** | Rust (Axum) | Trust boundary: verifies JWT, rate limits, idempotency, breaker, bulkhead | Redis (ephemeral only) |
| **console** | TypeScript | BFF + SSR — **composes the screens** | none |
| **identity** | Go | users, riders, JWKS, refresh tokens | CockroachDB |
| **rental-core** | .NET 10 | motorcycles + rentals + outbox + rider projection | CockroachDB |
| **billing** | Kotlin | invoices, inbox, consumes `rental.closed` | CockroachDB |
| **media-guard** | Rust | validates and stores driver's licences | MinIO / S3 |
| **risk-pricing** | Python | OCR and fraud scoring — **no synchronous API** | CockroachDB |
| **telemetry** | Elixir | live tracking over WebSocket | Cassandra |

Seven languages across eight services is a lot, and the cost is real: seven
supply chains to maintain. What keeps it from being a zoo is that each one has
an ADR justifying it. If one has to go, the candidate is `media-guard` — it
doubles Rust, and image parsing fits inside the gateway or as a `risk-pricing`
task.

---

## 3. The drawing

```mermaid
graph TD
    classDef client fill:#e8eaf6,stroke:#3949ab,stroke-width:2px;
    classDef gateway fill:#e0f2f1,stroke:#00695c,stroke-width:2px;
    classDef service fill:#e3f2fd,stroke:#1565c0,stroke-width:2px;
    classDef data fill:#fff3e0,stroke:#e65100,stroke-width:2px;
    classDef msg fill:#f1f8e9,stroke:#33691e,stroke-width:2px;
    classDef infra fill:#fafafa,stroke:#616161,stroke-width:2px;

    Client["Browser / app"]:::client

    subgraph edge["Trust boundary — exactly one"]
        Gateway["api-gateway · Rust + Axum<br/>verifies signature: fail-closed<br/>rate limit: fail-open<br/>idempotency · breaker · bulkhead"]:::gateway
    end

    Client -->|REST + token| Gateway

    subgraph svc["Domain services"]
        Console["console · TypeScript<br/>BFF + SSR<br/>composes the screens"]:::service
        Identity["identity · Go<br/>users · riders · JWKS<br/>refresh tokens"]:::service
        RentalCore["rental-core · .NET 10<br/>motorcycles · rentals · outbox<br/>rider projection"]:::service
        Billing["billing · Kotlin<br/>invoices · inbox"]:::service
        MediaGuard["media-guard · Rust<br/>validates licences"]:::service
        RiskPricing["risk-pricing · Python<br/>OCR · scoring<br/>no synchronous API"]:::service
        Telemetry["telemetry · Elixir<br/>WebSocket · positions"]:::service
    end

    Gateway -->|/console| Console
    Console -->|back through the gateway,<br/>with the user's token| Gateway
    Gateway -->|/api · signed header| Identity
    Gateway -->|/api · signed header| RentalCore
    Gateway -->|/api · signed header| Billing
    Gateway -->|upload| MediaGuard
    Gateway -->|WS| Telemetry
    Identity -.->|JWKS, public key| Gateway

    subgraph bus["Two buses, different semantics"]
        RabbitMQ["RabbitMQ · cmd.*<br/>one owner · ack · DLQ<br/>gone once handled"]:::msg
        Kafka["Kafka · events<br/>retained · replayable<br/>key = immutable id, per topic"]:::msg
        SchemaReg["Apicurio Schema Registry<br/>Protobuf · FULL compatibility<br/>OUTSIDE the message path"]:::infra
    end

    Gateway -.->|cmd.media.validate| RabbitMQ
    RabbitMQ -.->|consumes + acks| MediaGuard

    RentalCore -.->|rental.created · rental.closed<br/>key: motorcycle_id| Kafka
    Identity -.->|rider.registered · rider.verified<br/>key: rider_id| Kafka
    MediaGuard -.->|document.stored · key: rider_id| Kafka
    RiskPricing -.->|document.verified · risk.scored<br/>key: rider_id| Kafka
    Billing -.->|invoice.issued · key: rental_id| Kafka

    Kafka -.->|rental.closed| Billing
    Kafka -.->|rider.verified + risk.scored<br/>feeds the projection| RentalCore
    Kafka -.->|document.stored| RiskPricing
    Kafka -.->|document.verified| Identity
    Kafka -.->|rental.created| Telemetry

    RentalCore -. resolves schema id, cached .-> SchemaReg
    Billing -. resolves schema id, cached .-> SchemaReg

    subgraph stores["Data — one owner per datum"]
        CRDB["CockroachDB<br/>identity · rental-core<br/>billing · risk-pricing<br/>refresh tokens"]:::data
        Cassandra["Cassandra<br/>positions · CQL"]:::data
        Redis["Redis — never the source of truth<br/>rate limit · idempotency<br/>revocation denylist"]:::data
        MinIO["MinIO / S3<br/>documents"]:::data
    end

    Identity --> CRDB
    RentalCore --> CRDB
    Billing --> CRDB
    RiskPricing --> CRDB
    Telemetry --> Cassandra
    MediaGuard --> MinIO
    Gateway --> Redis

    subgraph obs["Observability"]
        OTel["OTel Collector<br/>OTLP · W3C Trace Context"]:::infra
        Grafana["Grafana<br/>SLO · error budget"]:::infra
        Toxi["Toxiproxy<br/>fault injection"]:::infra
    end

    Gateway -.->|OTLP| OTel
    RentalCore -.->|OTLP| OTel
    Billing -.->|OTLP| OTel
    OTel --> Grafana
    Toxi -.->|intercepts| CRDB
    Toxi -.->|intercepts| Kafka
    Toxi -.->|intercepts| Redis
```

Three things this drawing fixes relative to the earlier draft, worth saying out
loud because each was drawn wrong in a way that matters:

- **The schema registry does not sit between the producer and Kafka.** The
  producer resolves a schema id once, caches it, serialises locally and
  publishes directly. The registry is neither a publishing single point of
  failure nor a latency contributor.
- **`rental-core` consumes as well as produces.** Without a rider projection it
  would have no way to put the rider's name into an event about a datum it does
  not own.
- **The verification loop closes.** `document.stored` → `risk-pricing` →
  `document.verified` → `identity` → `rider.verified` → `rental-core`'s
  projection. Renting requires a verified licence, and no synchronous call
  crosses that path.

---

## 4. The decisions, and where they live

### 4.1 Portability — CockroachDB on both sides · ✅ implemented

The transactional core is CockroachDB locally **and** CockroachDB Cloud on AWS.
Aurora was considered and rejected: the container would be PostgreSQL and the
managed service would be Aurora — two different engines agreeing on a protocol,
which is a weaker claim than every other row of
[ADR 0004](adr/0004-cloud-portability-by-protocol.md)'s table makes.

The DDL is split into per-engine bootstrap (`000_bootstrap.<engine>.sql`) and a
portable schema (`001_schema.sql`), and CI applies the same file to CockroachDB
and to PostgreSQL, asserting the invariant on both. Since both sides are the
same engine, nothing would break if the dialect slipped — which is exactly why
the check has to be deliberate.

### 4.2 Consistency — merge into `rental-core`

`moto-hub` and `rental-operations` become one service, so that the rental and
its outbox row are written in the **same transaction**. That is what makes
[ADR 0009](adr/0009-exactly-once-effect.md) true rather than drawn, and it
requires taking `rentals` out of MongoDB.

**MongoDB's fate:** it leaves the stack when Piso 2 completes. Until then it
stays in the compose file serving the current `rental-operations`, which is its
only consumer.

### 4.3 Read aggregation — in the BFF, without GraphQL

[ADR 0014](adr/0014-read-aggregation-at-the-bff.md). The gateway and the
composition layer change at opposite rates and fail in opposite directions; a
schema that stitches Rental to Rider at the edge couples a weekly-changing
artefact to the security boundary. GraphQL was rejected as a substitute for the
BFF rather than a complement to it — and the trigger to revisit is written down:
a third independent consumer.

**Batch endpoints are a contract requirement**, not an optimisation: without
`GET /riders?ids=…` the N+1 only moves.

### 4.4 Event contracts

[ADR 0015](adr/0015-event-contracts-and-carried-state.md). Events carry state,
bounded by a narrow rule: *does the field describe the fact, or serve a
consumer?* A carried field is a **dated fact, not a cache** — if the rider is
renamed, the invoice does not change.

Partition keys are **always an immutable id, and always per topic**. Never the
plate: it has already been rewritten in this system
(`CanonicalizeLegacyMotorcyclePlates`), and a mutable business key would reshard
the topic at the moment of the correction. For the same reason `rentals` now
references `motorcycles (id)`.

Compatibility is **FULL** — an old consumer survives the producer's upgrade, and
a new consumer can replay the history. **Protobuf** is the encoding, under four
mandatory conventions: every field `optional`, field numbers never reused, money
as `int64` minor units, and enums with an `_UNSPECIFIED = 0` member.

### 4.5 Fraud scoring — off the request path

[ADR 0016](adr/0016-risk-scoring-off-the-request-path.md). The score is
precomputed, published, and read by `rental-core` from its local projection. A
circuit breaker failing open in front of a fraud control is an attack surface:
an attacker would only have to make the service slow to bypass the check.
`risk-pricing` exposes no synchronous API at all.

Real-time signals a projection cannot hold — velocity, repetition — are counters
in the gateway's Redis, which is already there.

### 4.6 Session and revocation

[ADR 0017](adr/0017-session-lifetime-and-revocation.md). A 5-minute access token
verified locally against JWKS; a 7-day refresh token in **CockroachDB**.
Revocation takes up to 5 minutes for ordinary operations and is immediate for
high-value ones, through a denylist consulted only there.

The invariant this establishes: **Redis is never the source of truth — only
protection and speed.** Losing Redis degrades rate limiting and idempotency, and
logs nobody out.

---

## 5. State of the contradictions

| # | Contradiction | State |
|---|---|---|
| 01 | The target DDL did not run on the engine ADR 0004 declared | ✅ **closed** — portable schema, proven in CI against both engines |
| 02 | The double-booking guarantee lives in MongoDB, not in the target schema | ⬜ **open** — depends on the Piso 2 merge |
| 03 | Identity has no place in the schema or among the services | ⬜ **open** — `identity` decided in ADRs 0012 and 0013; tables not written |

No box is ticked because a decision was taken. Only because code runs.

---

## 6. Roadmap

| Floor | Scope | Size | Gain |
|---|---|---|---|
| **0** | Portable schema, per-engine bootstrap, CI on both | S — **done** | Portability becomes verifiable |
| **1** | Move the .NET services into `services/`, wire them to the new stack, export OTLP | M | The dashboards stop querying an empty series |
| **2** | Merge into `rental-core`, migrate `rentals` off Mongo, inbox in `billing` | L | Real ACID consistency; exactly-once effect demonstrated |
| **3** | The remaining polyglot services, with contract testing | L | The target architecture, complete |

No estimates in weeks, deliberately: they would depend on availability, and an
invented number costs more than it helps. The order is what matters — each floor
leaves the repository demonstrable at the end of it.

---

## 7. Next steps

1. **Piso 1.** It is what turns the platform from a stage set into an instrument.
2. **Identity tables**, `refresh_tokens` among them — closes Contradição 03 in the schema.
3. **The Protobuf contract for `rental.closed`**, with the fields that describe the fact, and the CI job that blocks a compatibility break.
4. **Batch endpoints** in `identity` and `rental-core`, locked by contract tests.
5. **The missing-score policy**: which tier a rider who has not been scored yet falls into.

---

*Derived from the "two systems, one repository" analysis, ADRs 0001–0017 and
[`AUDITORIA-ARQUITETURA-SEGURANCA.md`](AUDITORIA-ARQUITETURA-SEGURANCA.md).*
