# Original epic 10: running and verifying the four services

The original scope is #73–#77. billing (#137) landed after it and runs in the
same stack; identity and the BFF read composition (#136, #138) remain separate.
The strangler migration (#130) is done. The running topology is the
root Compose stack, extended by `docker-compose.polyglot.yml`.

## Start

Generate local secrets with `scripts/New-LocalSecrets.ps1` on first use. Keep
existing credentials when reusing volumes. Run `tilt up -- --full`, or:

```powershell
docker compose -f docker-compose.yml -f docker-compose.chaos.yml -f docker-compose.polyglot.yml up -d --build
```

Console: http://localhost:3001. Tracking: localhost:4000. The BFF accepts an
access token from the gateway's configured JWKS identity provider and stores it
in a HttpOnly cookie. A token from any other issuer is not compatible with that gateway
contract; replacing identity remains #136. This is explicit in ADR 0021.

## Isolated acceptance environment

### Existing RabbitMQ installations

New installations use `cmd.rider.register`, `cmd.rider.store-document` and
`cmd.rental.update-licence`. Before upgrading an existing installation, pause
command-producing traffic and let the old application drain its database outboxes,
ready/unacknowledged messages and delayed retries. Resolve or archive poison
messages explicitly; do not delete broker volumes to perform this update.

Run `scripts/Update-CommandQueueNames.ps1` against the existing definitions file,
then import that file with RabbitMQ's `rabbitmqctl import_definitions` using its
mounted container path (or restart the broker to load its configured definitions).
The script adds the new names and ACLs while preserving credentials, old queues
and old permissions. Deploy all four .NET applications together, verify command
delivery, then resume writes. To roll back, use the previous applications and
drain or replay any new pending commands before reverting producers; no queue is
automatically removed. This is a queue naming update, independent of #130.

### Prepare the fixture

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Run-LoadTest.ps1 -PrepareOnly -Polyglot
node services/console/test/stack-smoke.mjs
```

The existing `projecty-load` test issuer and seeded fleet are used only in this
isolated project. Console is http://localhost:13001; tracking is localhost:14000.
The preparation command resets only the load fixture's rental/cache data; do
not point it at a production project. The test creates a real rental, obtains
a short tracking ticket, sends a position over Phoenix, and waits for the real
trace to include the Kafka telemetry and risk consumers. Evidence is written
to `docs/measurements/polyglot-api.json`. The ignored `.env.console-session.json`
contains short-lived test credentials and must never be committed.

Run `node services/console/test/run-browser.mjs` for the Playwright harness. It runs in
the official version matching package-lock.json on the fixture network, with
the repository mounted at `/workspace`, and captures `docs/images/console-*.png`.
The runner stops only `projecty-load` telemetry at its capture checkpoint and
restarts it afterward. The harness asserts that the last marker remains visible
and disconnected. The capture report records successful checks and browser errors.

## Targeted checks

Console, telemetry and risk-pricing use Alpine runtime images with package updates
applied at build time. The Python image compiles librdkafka 2.15.0 from the official
checksum-verified source because Alpine's packaged library is older than the client
requires; temporary compiler dependencies are removed after installation.

* Rust: `cargo test --manifest-path services/media-guard/Cargo.toml --locked`;
  `cargo clippy --manifest-path services/media-guard/Cargo.toml --locked --all-targets -- -D warnings`.
* Elixir: `mix format --check-formatted` and `mix test` in services/telemetry.
  The lifetime regression uses Redis at `TEST_REDIS_URL` (default localhost:6379);
  CI provides an isolated Redis service. It exercises a real rental process,
  accepted/rejected positions and stale/current timeout delivery.
* Python: build services/risk-pricing/Dockerfile from the repo root, then run
  its image with `--entrypoint pytest -q -p no:cacheprovider`. This invokes real
  Tesseract on a generated PNG as well as restart/idempotency tests.
  In the isolated running worker, `python smoke_document.py` additionally proves
  the real media-guard → MinIO → Kafka → OCR path and removes its own test objects.
* .NET: `dotnet test services/rental-core/RentalCore.sln` and
  `dotnet test services/rental-core/RentalCore.sln` use isolated Testcontainers.
* Console: `npm ci`, `npm test`, `npm run build` in services/console.
* Kotlin: `gradle ktlintCheck test` in services/billing, with a JDK 21 and
  Gradle 8.14.3 — there is no `gradlew` here; the version is pinned in the
  Dockerfile and in the workflow, and CI refuses a PR where the two disagree.
  The settlement tests are pure; `ExactlyOnceTest` starts a real CockroachDB
  through Testcontainers and applies `deploy/db/sql` to it. Where the Docker
  socket is not reachable from inside a container — Docker Desktop on Windows —
  start one by hand and point the test at it with
  `BILLING_TEST_COCKROACH=host:port`.

The telemetry `test/soak.mjs` sends bursts through a real socket for 30 seconds
by default. Its observed acceptance must remain at most one/second; Redis
reserves that rate across all rentals/replicas of the same rider. Cassandra's
primary key is ((rider_id, day), recorded_at), with server timestamps and 90-day
TTL. Query the actual rider/day after the probe to verify stored rows and TTL.

## Failure expectations

The rental Kafka relay drains the `outbox` table in batches of 100, oldest
first. A row is written in the same transaction as the rental, so an event
without a rental cannot exist and a rental without its event cannot either. The
row is marked published only after `ProduceAsync` returns: a crash between the
two republishes the same event, and its id -- derived from the rental and the
subject -- is what lets the consumer recognise it. The partition key is the
motorcycle id, which does not change when a licence plate is corrected.

Concurrent closings are decided by the update's row count under a status guard:
if another request has already closed the rental, the losing request returns
HTTP 409. Closing again returns the stored rental unchanged; only the winning
update enqueues `rental.closed`.

## identity

Issues the tokens and publishes the JWKS. Port `8095`, its own tables in the
shared CockroachDB (`users`, `user_roles`, `riders`, `refresh_tokens`,
`signing_keys`), and no dependency on Kafka.

If the gateway answers `401` to everything, check the JWKS first:

```bash
curl -s localhost:8095/.well-known/jwks.json
```

An empty or unreachable document means the gateway fails closed, which is the
posture ADR 0013 chose. A token whose `kid` is not in that document was signed
by a key that has already been retired.

### The rider domain

Since #136 the rider record lives here too, in its own table beside the
credential — [ADR 0023](../adr/0023-the-rider-record-lives-with-the-credential.md)
explains why `rider-core` was dropped and what the merge costs.

```
GET    /api/riders?ids=a,b,c     admin, capped at 100
GET    /api/riders/{userId}      the rider, or an admin
DELETE /api/riders/{userId}      admin
PUT    /update-image             the rider named by the envelope
```

All four require the gateway identity envelope of ADR 0008, verified here in Go
— the third implementation of that check, after C# and Kotlin. The canonical
string is pinned on both sides: `signs_rider_reads_for_the_identity_audience` in
the gateway, `TestAcceptsAnEnvelopeTheGatewaySigned` in identity, against an
envelope the gateway actually produced.

Someone else's record answers 404, not 403. The difference between those two
responses tells a scanner which identifiers exist.

### What identity publishes, and what it listens to

| Direction | Topic | When |
|---|---|---|
| out | `rider.registered` | registration |
| out | `rider.verified`, `rider.verified.v2` | registration, OCR verdict, deletion |
| out | `document.stored` | a CNH image is stored |
| in | `document.verified` | risk-pricing cross-checked the OCR'd number |

Every one of those is written to the shared `outbox` **inside the transaction
that changed the row**, and a relay sends them. Deleting a rider publishes
`verified = false` rather than only deleting the row: rental-core authorizes
from its local projection, so without a new fact it would keep answering
"verified" for a rider who no longer exists.

The OCR verdict can only take entitlement **away**. A document that does not
match revokes; one that matches returns what the licence type already said.
Letting the OCR grant would make a category-B licence rentable by sending a
legible photo.

If facts stop arriving, the outbox is where they are:

```sql
SELECT topic, count(*) FROM outbox
 WHERE aggregate_type = 'rider' AND published_at IS NULL
 GROUP BY topic;
```

A growing count means Kafka or the schema registry is unreachable — identity
serves login and reads normally while that lasts, and only stops telling.

Rotating the signing key is a command, not a database edit:

```bash
docker compose exec identity /app/identity rotate-keys
```

The previous key stays published for
`IDENTITY_KEY_ROTATION_OVERLAP_SECONDS` (900 by default), which is what lets a
token already in flight keep working. The service refuses to start if that
overlap is shorter than the access token's lifetime.

Bringing the legacy AuthGate users across:

```bash
docker compose exec -e AUTHGATE_DATABASE_URL=postgres://... identity \
  /app/identity import-authgate
```

It preserves each user's identifier — that value is the JWT `sub` and
`rentals.rider_id` — and copies the ASP.NET Identity password hash unchanged.
Those passwords keep working: the first successful login verifies the old format
and rewrites it as Argon2id. Re-running the import is safe; it skips what is
already there and reports what it could not map.

The invoice itself is read through the gateway, from billing:

```
GET /api/invoices/{rentalId}
GET /api/invoices?rentalIds=a,b,c
```

Both are read-only and both filter to the caller unless the identity envelope
carries the `Admin` role. Someone else's invoice answers 404, not 403: the
difference between those two responses tells a scanner which ids exist.

billing verifies the gateway identity envelope of ADR 0008 the same way the .NET
services do — it is a second implementation of that check, in Kotlin, and the
canonical string is pinned on both sides: a test in the gateway asserts what it
signs for an invoice route, and `GatewayIdentityTest` in billing asserts what it
accepts, against an envelope the gateway actually produced. Its audience must
equal `GATEWAY_JWT_AUDIENCE_BILLING`; today both are `projecty.rental-core`, and
`docker-compose.yml` says why.

Since #137 that endpoint is `POST /api/Rental/close`, and it computes no money.
It records the end date and the status; what is owed is decided by billing from
the event, which makes the amount eventually consistent by design. Closing
returns the closed rental, not the invoice — the invoice exists moments later,
once the relay publishes and the consumer settles. A rental closed with no
invoice after the relay has drained means billing is behind or refused the
event, and its log says which.

The billing consumer separates a broken message from a broken dependency,
because the two need opposite answers. Undecodable Protobuf and an event
missing what a settlement needs are logged and skipped, and the batch still
commits -- retrying them forever would stop the partition for every other
rider. Anything else (the database down, a pool timeout) rewinds the batch to
the last committed offsets and tries again, because dropping a good message
would lose an invoice. A worker thread that dies anyway halts the process on
purpose: a billing service that has stopped billing must not keep answering
`/health/live` with 200.

billing settles from what `rental.closed` carries and never calls back: the
agreed total, the plan days and the three dates all travel on the event, so an
invoice is issued with rental-core down. The invoice, the inbox row and the
`invoice.issued` outbox row share one transaction, so a crash cannot leave a
message marked handled without its invoice. A replay that arrives with a new
message id — after inbox retention swept the old row, say — passes the inbox and
is refused by `one_invoice_per_rental` instead.

Replacing or deleting a document may remove its object before a delayed
`document.stored` event arrives. Only S3 `NoSuchKey` is consumed as failed
verification; the inbox and output events commit normally and newer verification
facts win by timestamp. Access failures, missing buckets, timeouts and server
errors still retry. This keeps deleted private documents from blocking unrelated
riders without treating an unavailable storage service as an OCR result.

The telemetry rental process expires after 24 hours without an accepted position.
Each accepted position renews the timer; rejected writes do not. A timeout already
queued for a previous timer is ignored, so continuously tracked multi-day rentals
remain connected.

| Stop/unavailable dependency | Observable behavior |
| --- | --- |
| telemetry | Marker freezes, last-update age grows; creation/listing still work |
| risk-pricing | Last risk/price projections remain; unknown riders use conservative base; no synchronous inference call |
| Kafka | Outboxes accumulate and retry; new tracking activation/scoring is delayed |
| Cassandra | History write warns and times out; current position remains in Redis and reaches sockets |
| Redis | Position writes fail closed; tracking readiness fails |
| Tempo / Prometheus | Console reports absent trace/metric data, without fabricated values |

Monitor `projecty.risk.projection.oldest_score_age_seconds` and worker score age
alongside outbox age/depth. Restore failed input or malformed object references
before retrying blocked partitions. Back up risk-state; historical replay older
than the ninety-day inbox retention must use a fresh state store and isolated
outputs, as described in ADR 0020. No volume deletion is part of normal recovery.
