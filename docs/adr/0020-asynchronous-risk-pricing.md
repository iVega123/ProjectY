# ADR 0020: Python owns asynchronous OCR, risk and demand pricing

Status: Accepted. Issue: #75. Date: 2026-09-06.

## Decision and workload

FastAPI exposes health probes only. Kafka consumers run Tesseract on sanitized
private PNG objects after document.stored, then emit document.verified. The
11-digit claimed CNH must match a complete OCR token. This validates text
consistency, not document authenticity. No document bytes or CNH are logged.

The worker scores rider.verified and rental lifecycle facts, and revisits all
known riders daily. A transparent logistic baseline uses verification and
smoothed late-return history; it is not a trained or calibrated fraud model.
Every five minutes, active rentals determine a capped demand multiplier.
pricing-policy.json is the single source of base tiers, copied into the .NET
artifact for conservative startup. Monetary calculations use integer cents.

RentalOperations consumes durable snapshots in the background. Its request
path reads local memory, with no HTTP or Kafka dependency on risk-pricing.
Unknown riders pay the base tier; scores at most 30 receive a 5% discount.
Settlement uses the price locked at creation. Delayed inference retains the
last known values, with score-age OTLP gauges in both worker and projection.

## Durability and boundaries

RiderManager saves document metadata and a PostgreSQL outbox row in one EF
transaction. Its relay deletes only acknowledged Kafka envelopes. Rental
lifecycle facts use the rental-core outbox from ADR 0019. Raw Protobuf follows
ADR 0015; Avro in the original issue is superseded by that contract decision.

The single worker owns a persistent SQLite WAL database containing inbox,
carried rider/rental state and outbox. Input deduplication, state changes and
output enqueue are one transaction. Kafka offsets commit afterward. Output
retries use stable event IDs; source timestamps reject older lifecycle and
verification updates. Each rental replica has its own consumer group and
rehydrates the snapshots persisted in `projection_snapshots` before consuming.
Older deliveries do not replace newer in-memory values.

SQLite avoids another database for this single-writer prototype. It does not
support horizontally scaled workers sharing a local volume: scale-out requires
partition-owned stores or a transactional shared database before increasing
replicas. Back up the risk-state volume. Kafka retains seven days; inbox IDs
retain ninety. Older historical replay must rebuild into a fresh store and
isolated output topics, never replay in place against current projections.

## Alternatives and costs

Synchronous inference would couple rental latency and availability to OCR and
model execution, violating ADR 0016. Implementing OCR in .NET avoids a runtime
but loses direct access to Python's document/model ecosystem. Python costs an
additional dependency lock, native OCR package, durable worker and operational
monitoring. The service is justified by asynchronous document and scoring work,
not by serving ordinary CRUD. A trained model requires separate evaluation,
versioning and fairness review; this baseline makes no predictive claims.

## Validation and degradation

The Python suite runs actual Tesseract on a generated PNG and tests mismatches,
digit boundaries, demand caps, restart deduplication and out-of-order facts.
The .NET suite checks document outbox content and conservative/local score use.
`docker compose -f docker-compose.yml -f docker-compose.polyglot.yml up -d --build risk-pricing`
starts the worker; execute `python smoke.py` in its container to verify live
Kafka outputs. Stop risk-pricing while creating rentals to exercise isolation.
Health readiness fails when polling/delivery is delayed. Failed input is
retained at its offset for repair; malformed documents can block that partition
and require operator intervention. Cassandra and Rabbit remain independent.
