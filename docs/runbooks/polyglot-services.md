# Original epic 10: running and verifying the four services

The original scope is #73–#77. Identity/billing/additional BFF (#136–#138) and
the strangler migration (#130) remain separate. The running topology is the
root Compose stack, extended by `docker-compose.polyglot.yml`.

## Start

Generate local secrets with `scripts/New-LocalSecrets.ps1` on first use. Keep
existing credentials when reusing volumes. Run `tilt up -- --full`, or:

```powershell
docker compose -f docker-compose.yml -f docker-compose.chaos.yml -f docker-compose.polyglot.yml up -d --build
```

Console: http://localhost:3001. Tracking: localhost:4000. The BFF accepts an
access token from the gateway's configured JWKS identity provider and stores it
in a HttpOnly cookie. Legacy AuthGate login is not compatible with that gateway
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
* .NET: `dotnet test services/RentalOperations/RentalOperations.sln` and
  `dotnet test services/RiderManager/RiderManager.sln` use isolated Testcontainers.
* Console: `npm ci`, `npm test`, `npm run build` in services/console.

The telemetry `test/soak.mjs` sends bursts through a real socket for 30 seconds
by default. Its observed acceptance must remain at most one/second; Redis
reserves that rate across all rentals/replicas of the same rider. Cassandra's
primary key is ((rider_id, day), recorded_at), with server timestamps and 90-day
TTL. Query the actual rider/day after the probe to verify stored rows and TTL.

## Failure expectations

On rollout, the rental Kafka relay reconciles active legacy Mongo documents in
batches of 100. A missing `PendingEvents` field identifies the old schema; an
empty persisted array already belongs to the outbox schema and is not replayed.
The relay atomically adds a stable `rental.started` envelope only while the
document remains active and uninitialized. Its occurrence time is the original
Mongo ObjectId creation time, so a delayed backfill cannot reopen a closed rental
in consumers. Legacy records lack an immutable motorcycle ID: their start/close
events use the stable `legacy-rental:<id>` key, independent of licence plate
renames. New rentals continue to use their real motorcycle ID. Settlement updates
preserve concurrent outbox writes and acknowledgements. No SQL migration or
manual database rewrite is needed; tracking becomes available after Kafka catches up.

Concurrent settlements use the Mongo update's matched count: if another request
has already completed the rental, the losing request returns HTTP 409 and does
not release the motorcycle claim or return its unpersisted calculation. Reloading
the rental or retrying settlement after completion returns the stored dates and
costs; only the winning update enqueues `rental.closed`.

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
