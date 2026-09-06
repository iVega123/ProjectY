# ADR 0022: Runtime and datastore choices follow the workload

Status: Accepted. Issues: #10, #77. Date: 2026-09-06.

## Runtime allocation

| Boundary | Workload that earns the runtime | Alternative | Operational cost |
| --- | --- | --- | --- |
| Rust media-guard | Bounded CPU image decode, fresh-pixel encoding and thumbnails | In-process .NET image library | Native image codecs, memory limits, independent patch/build pipeline; two CPU slots protect the process |
| Elixir telemetry | Long-lived sockets, Presence and isolated rental processes | Node sockets or Go goroutines | BEAM supervision and distribution, Kafka client compatibility, shared rate/state cache |
| Python risk-pricing | Offline OCR and scoring, periodic demand pricing | .NET worker | Tesseract packages, Python lock, durable worker state, evaluation discipline before adopting a trained model |
| TypeScript console | Interactive map and trace/load inspection with shared BFF types | Static SPA plus separate BFF | Node server and client bundles, browser testing, framework updates and external map tiles |
| Existing .NET services | Existing transactional rental, rider and motorcycle behavior | Reimplementing CRUD during this epic | Transitional event outboxes/projections in current stores; migration remains #130 |

Detailed accepted runtime ADRs: [0018](0018-media-guard.md),
[0019](0019-live-tracking.md), [0020](0020-asynchronous-risk-pricing.md),
[0021](0021-operations-console.md). Adding a language for ordinary CRUD is not
a sufficient justification. The current four services correspond to four
distinct workloads, with independently testable failure boundaries.

## Datastore allocation

| Store | Access pattern and owner | Rejected shortcut | Cost and retention |
| --- | --- | --- | --- |
| PostgreSQL | Existing rider/motorcycle records and rider document outbox | Publish Kafka directly after saving metadata | Schema migrations, transactional relay and backups; acknowledged outbox rows are removed |
| MongoDB | Current rental document and atomic embedded lifecycle outbox; risk/pricing snapshots | Introduce SQL solely for this epic's events | Transitional model maintained until #130; delivered envelopes are pulled independently |
| Cassandra | Append-heavy rider history, queried by rider/day | One ever-growing rider partition | Daily partitions, 90-day TTL, shared one-position/second limit; up to 86,400 rows per rider/day; heap and compaction overhead |
| Redis | Active rental ownership, last position, event ordering, shared write rate | In-process cache across independent telemetry replicas | Shared availability dependency and 90-day TTL; failed Redis rejects position writes |
| MinIO/S3 | Private sanitized full image and thumbnail | Persisting expiring public URLs or raw uploads | Object lifecycle cleanup and fresh five-minute URLs; deletion must include both variants |
| SQLite WAL | Single Python worker inbox, carried facts and output outbox | Treat Kafka acknowledgement as an atomic state write | Persistent volume backup and a single writer; partition/shared-store redesign required before scale-out |
| Tempo / Prometheus / Loki | Traces, numeric series and structured operational logs | Client-side recordings presented as live evidence | Independent retention/resource budgets; observability outages show missing data without blocking rentals |

These are workload allocations, not a requirement to use every store for every
service. Cassandra history is allowed to degrade while Redis preserves live
delivery. Risk is fully asynchronous; its outage freezes projections rather
than adding request latency. The full profile costs more memory and build time
than the default core: Kafka, Cassandra, BEAM, Python and Node all add resident
processes. Measure the intended host with `docker stats --no-stream`; no
unmeasured production throughput or monetary saving is claimed here.

## RabbitMQ commands and Kafka facts

RabbitMQ keeps addressed work requests and their processing acknowledgements:
`cmd.rider.register`, `cmd.rider.store-document` and `cmd.rental.update-licence`.
Retry queues retain the command prefix; exhausted deliveries reach `cmd.rider.dead`
or `cmd.rental.licence-update.dead`. Kafka carries immutable facts:
document.stored, document.verified, rider.verified, rental.started,
rental.closed, risk.scored and pricing.updated. A command asks an owner to do
something; a fact reports a state transition and can fan out to independent
consumer groups. They have different delivery and replay contracts.

Database outboxes cross the commit/publish boundary. Consumers use IDs and
source timestamps because delivery can repeat or arrive out of order. Event
contracts follow Protobuf ADR 0015, keyed by immutable aggregate identity.
MongoDB remains the most replaceable active store: PostgreSQL JSONB could own
the rental documents and projections without another database process. Keeping
it here accepts a separate backup and operational surface until #130; this epic
does not claim MongoDB is required for the workload.
Registry governance is #132, and additional identity/billing/BFF services
#136–#138 are outside the original epic scope.

## Developer and supply-chain cost

`tilt up` retains the core; `tilt up -- --full` adds the polyglot overlay with
language-specific live updates. The four final images participate in the
existing OCI SBOM, vulnerability scan and main-only signing/provenance flow.
Language tests feed the Required CI gate. Dependency locks are committed;
native health probes must also work in development images. See the
[polyglot runbook](../runbooks/polyglot-services.md) for reproducible evidence.
