# ADR 0019: Phoenix owns live tracking

Status: Accepted. Issue: #74. Date: 2026-09-05.

## Workload and decision

Long-lived connections and independent rental lifecycles fit BEAM processes.
Phoenix Channels deliver positions; Presence tracks connected riders. Each
active rental gets a supervised GenServer, recreated from Redis after a process
or service restart. A signed five-minute ticket binds rental and rider; the
console obtains it only after a gateway-authorized rental read. The channel
rechecks expiry and active ownership for writes. Client timestamps are ignored.

Cassandra uses ((rider_id, day), recorded_at) with 90-day TTL. One accepted
position per second bounds a day partition to 86,400 rows per rider per active
rental. Redis keeps the last position and active rental state with the same TTL.
Instances must share Redis and connect BEAM distribution for cross-node Presence.

## Alternatives and costs

Node WebSockets would reuse the console runtime but require implementing
distributed Presence and per-rental isolation. Go goroutines are economical,
but Phoenix supplies the realtime protocol and supervision primitives together.
The cost is another runtime, broker client, clustering configuration and schema
init. Cassandra is justified by partitioned append-heavy history, not arbitrary
query flexibility; analytics must not scan it on a request path.

## Events and integration boundary

The active .NET service writes pending Protobuf envelopes inside the same Mongo
document as the rental. A background relay publishes rental.started/closed with
immutable motorcycle id as key. Kafka downtime accumulates envelopes and does
not fail rental creation. Acknowledgement pulls only the delivered envelope;
a crash can duplicate an event. Redis applies event timestamps atomically before
Kafka offset commit, preventing replay or a late start from reopening a closed
rental. Raw Protobuf contracts are checked in; registry governance stays #132.
This is a transitional adapter, not the SQL migration of #130.

## Degradation

Stopping telemetry freezes last positions; rentals and listings continue.
Cassandra failure drops history writes with a warning and keeps live delivery
and Redis state. Redis failure refuses tracking writes and readiness. Kafka
failure delays new channel activation; existing channels continue. Probes are
/health/live, /health/startup, /health/ready. Kafka headers carry W3C context into
OTLP consumer spans; position/history work also emits spans.
