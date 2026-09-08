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
| Rental claim | `motorcycle_id` while status is `active` | Target-schema partial unique index | At most one active rental per motorcycle |
| Producer write | Database transaction | CockroachDB / PostgreSQL | Aggregate mutation and outbox rows commit or roll back together |
| Relay | Outbox row and claim token | CockroachDB / PostgreSQL | A committed row remains retryable; concurrent relays do not own the same row |
| Rider consumer | `(MessageId, ConsumerName)` | PostgreSQL inbox transaction | Inbox record and relational domain effect commit once |
| Rental consumer | `(message_id, consumer)` | Target-schema inbox row | One active handler lease; completed messages are suppressed |
| Settlement consumer | `(message_id, consumer)` and `rental_id` | Target-schema transaction | Inbox row, invoice, and outgoing event commit once, together |
| HTTP retry | Service, authenticated caller, and `Idempotency-Key` | Redis AOF | Same fingerprint replays; a different fingerprint is rejected for 24 hours |

<a id="database-serialized-rental-claim"></a>
## Database-serialized rental claim

rental-core relies on `one_active_rental_per_motorcycle`, the partial unique
index in `deploy/db/sql/001_schema.sql` over `motorcycle_id` where the status
is `active`. The application may perform an advisory read for a friendly error,
but correctness comes from the index. Two genuinely parallel inserts can both
pass an earlier read; the database serializes the writes and accepts only one.
The losing API request returns `409 Conflict`.

The write also locks the motorcycle row (`FOR UPDATE`). The index settles two
rentals racing each other, but not a rental racing a retirement: that is write
skew, and under READ COMMITTED both transactions would see a world in which they
may proceed. CockroachDB is serializable and would refuse on its own; PostgreSQL
would not, and the same code runs on both.

Until #135 this invariant lived in a MongoDB partial unique index, and a
relational `rental_claims` table stood in for it as a portability proof. Both
are gone: the proof and the production mechanism are now the same index.

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
selected by `AggregateSequence`, `OccurredAtUtc`, and `Id`, and only one head
can be owned at a time.

The relay preserves causal order only when the producer persists a strictly
monotonic `AggregateSequence` in the same write. AuthGate image chunks do this
inside one registration transaction. MotoHub licence-plate updates currently
write sequence `0` for every separate update, so their timestamp and identifier
are tie-breakers, not a causal clock. Ordering between those updates is
deliberately not promised, nor is ordering between different aggregates.

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

<a id="inbox-convergence"></a>
<a id="mongo-inbox-convergence"></a>
## Rental inbox convergence

rental-core claims an `inbox` row for a `(message_id, consumer)` pair and
suppresses a completed message, but the handler runs between the claim and the
completion rather than inside one transaction with them. A crash can therefore
happen after the effect and before completion. Redelivery is safe only where
the handler itself is idempotent, such as the rider projection's newest-wins
upsert. The test proves convergence for that operation; it does not generalize
the PostgreSQL transactional-inbox promise — where effect and inbox commit
together — to every inbox handler.

Until #135 this was a MongoDB inbox document, and the idempotent handler was
the licence-plate rewrite. Both are gone: a rental references `motorcycle_id`,
so there is no copied plate left to rewrite.

<a id="settlement-inbox"></a>
## Settlement inbox: the effect inside the transaction

billing consumes `rental.closed` and writes the inbox row, the invoice, and the
`invoice.issued` outbox row in one transaction. There is no claim lease, no
handler running between a claim and a completion, and therefore no crash window
of the kind the rental inbox above lives with. Crashing anywhere before the
commit leaves the message untreated and the redelivery settles it; crashing
after leaves both the invoice and its inbox row.

This is the same promise as the PostgreSQL transactional inbox, made in a
different runtime and against the target schema. It is worth stating separately
because it is the version of the promise where duplication costs money rather
than a recomputed projection, and because it crosses a process and language
boundary: billing shares no transaction, connection pool, or runtime with the
producer, so it can only hold if the outbox and inbox contract is real.

A second, independent guard sits under it. The inbox deduplicates *messages*;
`one_invoice_per_rental` deduplicates *rentals*. They fail differently — a
replay from offset zero after inbox retention has swept the row carries a new
message id and passes the first gate — and only the second one refuses it.

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
for already-sequenced rows is preserved because later rows cannot pass their
pending head; the relay does not invent a missing producer sequence.

### Broker partition and ambiguous confirms

Before a confirm, the row remains pending and is retried. If RabbitMQ accepted
the message but the confirm was lost, retry can publish it twice. Durable queues
and persistent messages protect accepted messages across broker restart; inbox
deduplication protects domain effects from duplicate delivery.

### Clock skew

Outbox and rental inbox leases use application-node UTC timestamps. A fast owner
clock can hold a claim longer than intended; a fast contender can reclaim it
early. Claim tokens prevent a former owner from marking a row complete after it
loses ownership, but they cannot retract an external publish already made.
Production nodes therefore require synchronized clocks and lease durations
larger than expected skew and publish latency. Duplicate delivery remains an
expected outcome and must reach an inbox-protected handler. Same-sequence
MotoHub updates can also be observed out of causal order when their timestamps
come from skewed writers.

### Partition during the unique-index race

The primary database is the authority. A writer that cannot reach it cannot
claim a rental and fails. If a commit succeeded but its acknowledgement was
lost, the client sees an ambiguous result; retrying cannot create a second
active rental because the unique index still arbitrates the write. During a
database topology event, this guarantee assumes the database itself does not
acknowledge conflicting writes outside its configured consistency model.

### Process crash by phase

| Crash point | Observable result |
|---|---|
| Before database commit | Neither domain mutation nor outbox row exists |
| After commit, before publish | Domain state exists; pending outbox row publishes after recovery |
| After broker accept, before `PublishedAtUtc` | Message may be published again; inbox suppresses the duplicate effect |
| During PostgreSQL inbox handler | Inbox and relational effect roll back together |
| After an inbox handler effect, before inbox completion | Redelivery occurs; only an idempotent handler is safe. Does not arise in billing, whose effect and inbox row share the commit |
| After HTTP effect, before response persistence | Redis retains `unknown`; the same key never executes again during retention |

## What is deliberately not promised

- **Not exactly-once delivery.** RabbitMQ may redeliver and the relay may
  republish.
- **Not one transaction across Redis, PostgreSQL, CockroachDB, MinIO, and
  RabbitMQ.** Each guarantee ends at its named authority.
- **Not exactly-once execution.** Handlers and middleware can run more than
  once; the durable effect is what is deduplicated.
- **Not global event ordering.** A producer sequence can order one aggregate;
  nothing orders different aggregates.
- **Not causal ordering for MotoHub licence updates.** Those events do not yet
  carry a durable monotonic aggregate sequence; relay tie-breakers are not a
  substitute for one.
- **Not arbitrary inbox effect safety.** The rental inbox claims a row rather
  than committing with its handler, so it requires an idempotent effect after a
  crash window. The RiderManager and billing inboxes do commit with their
  handlers and carry no such window; the property belongs to the consumer, not
  to the pattern's name.
- **Not permanent deduplication.** HTTP and inbox records expire.
- **Not protection for requests without `Idempotency-Key`.** Those requests
  intentionally bypass Redis.
- **Not recovery from loss of the authority itself.** Losing the primary
  database, inbox table, or Redis AOF volume loses the corresponding evidence.
- **Not immediate propagation.** A healthy primary write can coexist with a
  delayed outbox and stale downstream reads.

## Executable proof matrix

Every proof carries a `Guarantee` trait — a JUnit `@Tag` in billing — that points
back to the paragraph above.
All database and Redis proofs use real Testcontainers; the transport is replaced
only where the test must deterministically stop or observe a publish.

| Paragraph | Executable proof | Fails when |
|---|---|---|
| [Database rental claim](#database-serialized-rental-claim) | [`ConcurrentRentalsForSameMotorcycle_OneWinsAndTheDatabaseRefusesTheOther`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/RentalStoreTests.cs) | The partial unique index is removed |
| [Database rental claim](#database-serialized-rental-claim) | [`ReturnedMotorcycle_CanBeRentedAgain`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/RentalStoreTests.cs) | The index loses its `WHERE status = 'active'` predicate |
| [Transactional outbox](#transactional-outbox) | [`CreatingARental_WritesItsOutboxRowInTheSameTransaction`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/RentalStoreTests.cs) | The rental and its event stop sharing a transaction |
| [Transactional outbox](#transactional-outbox) | [`RefusedRental_LeavesNeitherTheRowNorTheEvent`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/RentalStoreTests.cs) | A refused rental still announces itself |
| [Transactional outbox](#transactional-outbox) | [`DomainMutationAndOutboxInsert_RollBackTogetherWhenSaveFails`](../../services/rental-core/RentalCoreTests/Motorcycles/Integration/PostgreSql/OutboxRelayTests.cs) | The outbox is no longer part of the aggregate save |
| [Transactional outbox](#transactional-outbox) | [`CommittedSequencedMessages_SurviveRelayRestartAndDrainAfterBrokerRecovery`](../../services/rental-core/RentalCoreTests/Motorcycles/Integration/PostgreSql/OutboxRelayTests.cs) | The committed event row or retry behavior is removed |
| [Leased relay](#leased-outbox-relay) | [`ConcurrentRelays_ClaimOnlyOneHeadMessagePerAggregate`](../../services/rental-core/RentalCoreTests/Motorcycles/Integration/PostgreSql/OutboxRelayTests.cs) | Atomic claims or aggregate-head ordering is removed |
| [PostgreSQL inbox](#transactional-inbox) | [`SameMessageProcessedConcurrently_ProducesOneDatabaseEffect`](../../services/RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | The inbox conflict gate or shared transaction is removed |
| [PostgreSQL inbox](#transactional-inbox) | [`ImageRedelivery_UsesInboxAndCallsIdempotentUploadOnce`](../../services/RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | Completed image messages are handled again |
| [Rental inbox](#inbox-convergence) | [`SameMessageDeliveredTwice_ExecutesHandlerOnce`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/InboxProcessorTests.cs) | Completed inbox rows are claimable |
| [Rental inbox](#inbox-convergence) | [`CrashAfterIdempotentEffect_RedeliveryConvergesAndCompletesInbox`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/InboxProcessorTests.cs) | A crash cannot be reclaimed or the handler is not idempotent |
| [Settlement inbox](#settlement-inbox) | [`a mesma mensagem duas vezes emite uma nota so`](../../services/billing/src/test/kotlin/projecty/billing/ExactlyOnceTest.kt) | The inbox row stops sharing the invoice transaction |
| [Settlement inbox](#settlement-inbox) | [`nota recusada pelo banco nao deixa a mensagem marcada como tratada`](../../services/billing/src/test/kotlin/projecty/billing/ExactlyOnceTest.kt) | A refused invoice still marks the message handled |
| [Settlement inbox](#settlement-inbox) | [`mensagem nova para aluguel ja faturado nao emite a segunda nota`](../../services/billing/src/test/kotlin/projecty/billing/ExactlyOnceTest.kt) | `one_invoice_per_rental` is removed |
| [HTTP idempotency](#http-idempotency) | [`ReplayingCreateWithSameKey_ReturnsOriginalResponseAndOneEffect`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Completed responses are not stored |
| [HTTP idempotency](#http-idempotency) | [`ReusingKeyWithDifferentBody_ReturnsUnprocessableEntity`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Body fingerprints are ignored |
| [HTTP idempotency](#http-idempotency) | [`ConcurrentRequestWithSameKey_ReturnsConflictUntilFirstCompletes`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Atomic Redis claiming is removed |
| [HTTP idempotency](#http-idempotency) | [`LongRunningRequest_RetainsClaimUntilItCompletes`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Pending claims expire on a short execution lease |
| [HTTP idempotency](#http-idempotency) | [`DownstreamFailure_RetainsUnknownOutcomeWithoutRepeatingEffect`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Ambiguous failures release their claim |
| [HTTP idempotency](#http-idempotency) | [`ReusingKeyWithReorderedQueryValues_ReturnsUnprocessableEntity`](../../services/RiderManager/RiderManagerTests/Integration/Redis/IdempotencyMiddlewareTests.cs) | Repeated query-value order is discarded |
| [Retention](#retention-boundaries) | [`RetentionSweep_DeletesOnlyExpiredInboxRows`](../../services/RiderManager/RiderManagerTests/Integration/PostgreSql/InboxProcessorTests.cs) | PostgreSQL retention deletes current evidence |
| [Retention](#retention-boundaries) | [`RetentionSweep_RemovesHandledEntriesPastTheirPeriod`](../../services/rental-core/RentalCoreTests/Rentals/Integration/Database/InboxProcessorTests.cs) | The rental inbox grows without bound |

Run the proof suite from the repository root:

```powershell
dotnet test services/rental-core/RentalCoreTests/RentalCoreTests.csproj --filter "Category=Integration&Guarantee~ADR-0009"
dotnet test services/RiderManager/RiderManagerTests/RiderManagerTests.csproj --filter "Category=Integration&Guarantee~ADR-0009"
```

## Alternatives considered

- **Distributed transactions across every dependency.** RabbitMQ, Redis,
  PostgreSQL, CockroachDB, and object storage do not share a practical
  transaction coordinator here. The operational cost would still not remove
  ambiguous network outcomes.
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
the rental inbox's crash window behind the stronger PostgreSQL
transactional-inbox guarantee, and it does not claim that an HTTP idempotency
record commits atomically with a domain database. Precision is preferred over a
broader but false guarantee.

## Consequences

- Every new consumer must name its message identity and effect boundary.
- External side effects require their own idempotency key or reconciliation
  process.
- Clock synchronization and retention horizons are correctness inputs, not
  tuning details.
- Relay backlog, expired leases, inbox conflicts, and unknown HTTP outcomes
  need operational visibility.
- The integration suite is slower because it starts real PostgreSQL and Redis
  containers; that cost is accepted because mocks cannot prove these database
  races.

## Follow-up

- [Epic 5 — Transactional core and distributed consistency](https://github.com/iVega123/ProjectY/issues/6)
- [Task 56 — Write the guarantees and prove them](https://github.com/iVega123/ProjectY/issues/56)
