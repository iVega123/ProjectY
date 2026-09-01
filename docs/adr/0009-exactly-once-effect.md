# ADR 0009 — Exactly-once effect is a layered, bounded guarantee

- **Status:** Accepted
- **Date:** 2026-09-01
- **Deciders:** ProjectY maintainers

## Context

The phrase "exactly once" is dangerously compact. A request crosses an HTTP
server, a primary database, an outbox relay, RabbitMQ, and a consumer database.
None of those components can make one atomic commit across every boundary, and
RabbitMQ deliberately provides at-least-once delivery. Calling the whole path
"exactly once" would hide the failure modes that matter most.

This record refines the domain guarantee sketched in
[ADR 0003](0003-observability-and-fault-tolerance.md). It documents the
baseline .NET services implemented in this repository; it does not claim that
the future services named in `deploy/base/compose.yaml` already provide the
same behavior.

## Decision

ProjectY promises an **exactly-once effect only inside a named idempotency
boundary**. It means that retries or redeliveries carrying the same stable
identity produce at most one durable domain transition inside that boundary.
It does not mean exactly-once transport, one global transaction, or one
execution of application code.

The guarantee is assembled from independent layers. Each layer has its own
identity, durability boundary, expiry, and failure response.

| Layer | Stable identity | Authority | Guarantee |
|---|---|---|---|
| Rental claim | Motorcycle licence plate while status is `Active` | MongoDB partial unique index | At most one active rental per motorcycle |
| Producer write | EF Core database transaction | PostgreSQL | Aggregate mutation and outbox rows commit or roll back together |
| Relay | Outbox row and claim token | PostgreSQL | A committed row remains retryable; concurrent relays do not own the same row |
| Rider consumer | `(MessageId, ConsumerName)` | PostgreSQL inbox transaction | Inbox record and relational domain effect commit once |
| Rental consumer | `(MessageId, ConsumerName)` | MongoDB inbox document | One active handler lease; completed messages are suppressed |
| HTTP retry | Service, authenticated caller, and `Idempotency-Key` | Redis AOF | Same fingerprint replays; a different fingerprint is rejected for 24 hours |

<a id="database-serialized-rental-claim"></a>
## Database-serialized rental claim

RentalOperations relies on MongoDB's partial unique index over
`MotorcycleLicencePlate` for documents whose status is `Active`. The
application may perform an advisory read for a friendly error, but correctness
comes from the index. Two genuinely parallel inserts can both pass an earlier
read; MongoDB serializes the writes and accepts only one. The losing API request
returns `409 Conflict`.

The relational `rental_claims` Testcontainer is a portability proof of the same
invariant using a PostgreSQL partial unique index. It is not a claim that the
current RentalOperations service stores rentals in PostgreSQL.

<a id="transactional-outbox"></a>
## Transactional outbox

AuthGate and MotoHub add domain changes and `OutboxMessages` to the same EF Core
`DbContext` and commit them in one PostgreSQL transaction. A failed outbox
insert rolls back the domain mutation. A successful domain commit therefore
leaves a durable event row even when RabbitMQ is unavailable or the process
stops before the relay runs.

This boundary ends at PostgreSQL. Publishing and marking `PublishedAtUtc`
cannot be one transaction with RabbitMQ. Publisher confirms prove that the
broker accepted a message, but a connection loss around the confirm is
ambiguous and can cause a duplicate publish.

<a id="leased-outbox-relay"></a>
## Leased outbox relay and ordering

Each relay atomically claims one eligible aggregate head with `FOR UPDATE SKIP
LOCKED`, records an owner token and lease, and publishes outside the database
transaction. Another replica skips that row. Rows from the same aggregate are
released in `AggregateSequence` order; no ordering is promised between
different aggregates.

A publish failure clears the claim and schedules a bounded backoff. A process
crash leaves the claim until its lease expires. A confirm followed by a crash
before `PublishedAtUtc` is stored causes a later republish; consumer inboxes are
what make that duplicate harmless.

<a id="transactional-inbox"></a>
## PostgreSQL transactional inbox

RiderManager inserts `(MessageId, ConsumerName)` with `ON CONFLICT DO NOTHING`,
runs the handler, and commits the inbox row and EF Core domain changes in one
transaction. Concurrent deliveries race at the database; one commits and the
other reports that it did no work. This is the strongest consumer boundary in
the baseline and is the precise case meant by "exactly-once effect."

The guarantee covers effects written through that same PostgreSQL transaction.
Calls to object storage, HTTP APIs, email, or another database are outside it
and must be independently idempotent.

<a id="mongo-inbox-convergence"></a>
## MongoDB inbox convergence

RentalOperations atomically leases an inbox document and suppresses a completed
message, but its handler effect and inbox completion are not a general MongoDB
multi-document transaction. A crash can therefore happen after the effect and
before completion. Redelivery is safe only where the handler itself is
idempotent, such as setting every matching rental from an old plate to a new
plate. The test proves convergence for that operation; it does not generalize
the PostgreSQL transactional-inbox promise to arbitrary MongoDB effects.

<a id="http-idempotency"></a>
## HTTP idempotency

For `POST`, `PUT`, `PATCH`, and `DELETE`, clients may provide an
`Idempotency-Key`. Redis atomically claims the service/caller/key tuple for the
full 24-hour retention period. The fingerprint includes method, path, ordered
query values, caller, content type, and raw body. A completed response is
replayed; a different fingerprint returns `422`; concurrent ownership returns
`409`.

Once downstream execution starts, an exception is retained as an `unknown`
outcome rather than releasing the key. That chooses duplicate prevention over
automatic retry when the database may already have committed. Redis uses AOF
with `appendfsync always` in both Compose stacks so a claim is fsynced before it
is acknowledged. The memory-limited self-hosted overlay uses `noeviction`:
when Redis is full, protected writes fail closed instead of silently evicting
idempotency history before its TTL. This trades write availability for the
stated duplicate-prevention guarantee. Redis is still not atomic with a service
database: loss or corruption of the Redis volume can remove request history.

<a id="retention-boundaries"></a>
## Retention boundaries

Idempotency records live for 24 hours and inbox records are retained for seven
days by the configured cleanup mechanisms. After retention expires, an old key
or message identity may execute again. Producers must not redeliver messages
beyond the consumer retention horizon, and clients must not treat an expired
HTTP key as permanent evidence.

## Failure modes

### Outbox relay lag

The domain commit succeeds and the outbox row remains pending. The API does not
wait for RabbitMQ, so downstream views can be stale until the relay recovers.
Backlog age and attempt count, not API status, expose this degradation. Ordering
within one aggregate is preserved because later rows cannot pass its pending
head.

### Broker partition and ambiguous confirms

Before a confirm, the row remains pending and is retried. If RabbitMQ accepted
the message but the confirm was lost, retry can publish it twice. Durable queues
and persistent messages protect accepted messages across broker restart; inbox
deduplication protects domain effects from duplicate delivery.

### Clock skew

Outbox and Mongo inbox leases use application-node UTC timestamps. A fast owner
clock can hold a claim longer than intended; a fast contender can reclaim it
early. Claim tokens prevent a former owner from marking a row complete after it
loses ownership, but they cannot retract an external publish already made.
Production nodes therefore require synchronized clocks and lease durations
larger than expected skew and publish latency. Duplicate delivery remains an
expected outcome and must reach an inbox-protected handler.

### Partition during the unique-index race

The primary database is the authority. A writer that cannot reach it cannot
claim a rental and fails. If a commit succeeded but its acknowledgement was
lost, the client sees an ambiguous result; retrying cannot create a second
active rental because the unique index still arbitrates the write. During a
database topology event, this guarantee assumes MongoDB itself does not
acknowledge conflicting writes outside its configured consistency model.

### Process crash by phase

| Crash point | Observable result |
|---|---|
| Before database commit | Neither domain mutation nor outbox row exists |
| After commit, before publish | Domain state exists; pending outbox row publishes after recovery |
| After broker accept, before `PublishedAtUtc` | Message may be published again; inbox suppresses the duplicate effect |
| During PostgreSQL inbox handler | Inbox and relational effect roll back together |
| After Mongo effect, before inbox completion | Redelivery occurs; only an idempotent handler is safe |
| After HTTP effect, before response persistence | Redis retains `unknown`; the same key never executes again during retention |

## What is deliberately not promised

- **Not exactly-once delivery.** RabbitMQ may redeliver and the relay may
  republish.
- **Not one transaction across Redis, PostgreSQL, MongoDB, MinIO, and
  RabbitMQ.** Each guarantee ends at its named authority.
- **Not exactly-once execution.** Handlers and middleware can run more than
  once; the durable effect is what is deduplicated.
- **Not global event ordering.** Ordering is per aggregate only.
- **Not arbitrary MongoDB effect safety.** The current Mongo inbox requires an
  idempotent effect after a crash window.
- **Not permanent deduplication.** HTTP and inbox records expire.
- **Not protection for requests without `Idempotency-Key`.** Those requests
  intentionally bypass Redis.
- **Not recovery from loss of the authority itself.** Losing the primary
  database, inbox table, or Redis AOF volume loses the corresponding evidence.
- **Not immediate propagation.** A healthy primary write can coexist with a
  delayed outbox and stale downstream reads.

## Executable proof matrix

Every proof carries a `Guarantee` trait that points back to the paragraph above.
All database and Redis proofs use real Testcontainers; the transport is replaced
only where the test must deterministically stop or observe a publish.

| Paragraph | Executable proof | Fails when |
|---|---|---|
| [Database rental claim](#database-serialized-rental-claim) | [`ConcurrentCreateRequestsForSameMotorcycle_OneSucceedsAndOneReturnsConflict`](../../RentalOperations/RentalOperationsTests/Integration/MongoDb/ActiveRentalApiTests.cs) | The production Mongo partial unique index is removed |
| [Database rental claim](#database-serialized-rental-claim) | [`ConcurrentClaimsForSameMotorcycle_OneIsRejectedByDatabaseConstraint`](../../RentalOperations/RentalOperationsTests/Integration/PostgreSql/ActiveRentalConstraintTests.cs) | The relational partial unique index is removed |
| [Transactional outbox](#transactional-outbox) | [`DomainMutationAndOutboxInsert_RollBackTogetherWhenSaveFails`](../../MotoHub/MotoHubTests/Integration/PostgreSql/OutboxRelayTests.cs) | The outbox is no longer part of the aggregate save |
| [Transactional outbox](#transactional-outbox) | [`CommittedMessages_SurviveRelayRestartAndDrainInAggregateOrderAfterBrokerRecovery`](../../MotoHub/MotoHubTests/Integration/PostgreSql/OutboxRelayTests.cs) | The committed event row or retry behavior is removed |
| [Leased relay](#leased-outbox-relay) | [`ConcurrentRelays_ClaimOnlyOneHeadMessagePerAggregate`](../../MotoHub/MotoHubTests/Integration/PostgreSql/OutboxRelayTests.cs) | Atomic claims or aggregate-head ordering is removed |
| [PostgreSQL inbox](#transactional-inbox) | [`SameMessageProcessedConcurrently_ProducesOneDatabaseEffect`](../../RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | The inbox conflict gate or shared transaction is removed |
| [PostgreSQL inbox](#transactional-inbox) | [`ImageRedelivery_UsesInboxAndCallsIdempotentUploadOnce`](../../RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | Completed image messages are handled again |
| [Mongo inbox](#mongo-inbox-convergence) | [`SameMessageDeliveredTwice_ExecutesHandlerOnce`](../../RentalOperations/RentalOperationsTests/Integration/MongoDb/InboxProcessorTests.cs) | Completed Mongo inbox messages are claimable |
| [Mongo inbox](#mongo-inbox-convergence) | [`CrashAfterIdempotentEffect_RedeliveryConvergesAndCompletesInbox`](../../RentalOperations/RentalOperationsTests/Integration/MongoDb/InboxProcessorTests.cs) | A crash cannot be reclaimed or the handler is not idempotent |
| [HTTP idempotency](#http-idempotency) | [`ReplayingCreateWithSameKey_ReturnsOriginalResponseAndOneEffect`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Completed responses are not stored |
| [HTTP idempotency](#http-idempotency) | [`ReusingKeyWithDifferentBody_ReturnsUnprocessableEntity`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Body fingerprints are ignored |
| [HTTP idempotency](#http-idempotency) | [`ConcurrentRequestWithSameKey_ReturnsConflictUntilFirstCompletes`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Atomic Redis claiming is removed |
| [HTTP idempotency](#http-idempotency) | [`LongRunningRequest_RetainsClaimUntilItCompletes`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Pending claims expire on a short execution lease |
| [HTTP idempotency](#http-idempotency) | [`DownstreamFailure_RetainsUnknownOutcomeWithoutRepeatingEffect`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Ambiguous failures release their claim |
| [HTTP idempotency](#http-idempotency) | [`ReusingKeyWithReorderedQueryValues_ReturnsUnprocessableEntity`](../../RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Repeated query-value order is discarded |
| [Retention](#retention-boundaries) | [`RetentionSweep_DeletesOnlyExpiredInboxRows`](../../RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | PostgreSQL retention deletes current evidence |
| [Retention](#retention-boundaries) | [`Initializer_SchedulesRetentionWithTtlIndex`](../../RentalOperations/RentalOperationsTests/Integration/MongoDb/InboxProcessorTests.cs) | MongoDB inbox TTL is missing or misconfigured |

Run the proof suite from the repository root:

```powershell
dotnet test RentalOperations/RentalOperationsTests/RentalOperationsTests.csproj --filter "Category=Integration&Guarantee~ADR-0009"
dotnet test MotoHub/MotoHubTests/MotoHubTests.csproj --filter "Category=Integration&Guarantee~ADR-0009"
dotnet test RiderManager/RiderManagerTests/RiderManagerTests.csproj --filter "Category=Integration&Guarantee~ADR-0009"
```

## Alternatives considered

- **Distributed transactions across every dependency.** RabbitMQ, Redis,
  PostgreSQL, MongoDB, and object storage do not share a practical transaction
  coordinator here. The operational cost would still not remove ambiguous
  network outcomes.
- **Broker deduplication as the only defense.** It does not cover republish
  after an ambiguous confirm, consumer crashes, or domain-specific identities.
- **An application mutex around rental creation.** It protects one process,
  disappears on restart, and fails with two replicas. The database constraint
  is the authority all replicas share.
- **Infinite inbox and idempotency retention.** It converts correctness state
  into unbounded storage. Bounded retention is explicit, monitored, and part of
  the producer/client contract.

## What was explicitly rejected

The project does not use the phrase "exactly-once delivery." It does not hide
the MongoDB crash window behind the stronger PostgreSQL inbox guarantee, and it
does not claim that an HTTP idempotency record commits atomically with a domain
database. Precision is preferred over a broader but false guarantee.

## Consequences

- Every new consumer must name its message identity and effect boundary.
- External side effects require their own idempotency key or reconciliation
  process.
- Clock synchronization and retention horizons are correctness inputs, not
  tuning details.
- Relay backlog, expired leases, inbox conflicts, and unknown HTTP outcomes
  need operational visibility.
- The integration suite is slower because it starts real PostgreSQL, MongoDB,
  and Redis containers; that cost is accepted because mocks cannot prove these
  database races.

## Follow-up

- [Epic 5 — Transactional core and distributed consistency](https://github.com/iVega123/ProjectY/issues/6)
- [Task 56 — Write the guarantees and prove them](https://github.com/iVega123/ProjectY/issues/56)
