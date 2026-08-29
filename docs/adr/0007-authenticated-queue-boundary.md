# ADR 0007: Authenticate domain-writing queue messages

## Status

Accepted on 2026-08-29.

## Context

AuthGate publishes rider registration data and CNH image chunks for
RiderManager. The original messages were plain JSON, and RiderManager trusted
the payload `UserId` as the domain owner. Anyone able to reach RabbitMQ and
publish to those queues could create a rider or replace another rider's
document without passing through the HTTP authentication boundary.

The root and modernization Compose files also published RabbitMQ directly (or
through Toxiproxy) to the host, while the original four services shared one
broker credential.

## Decision

- AuthGate wraps rider messages in versioned envelopes containing the message
  type, signed subject, message ID, issue time, and base64 payload.
- AuthGate signs the canonical envelope fields with HMAC-SHA256. RiderManager
  verifies the signature in constant time before deserializing the payload and
  requires the payload `UserId` to exactly match the signed subject.
- Invalid, unsigned, mistyped, or identity-mismatched messages are negatively
  acknowledged without requeue and never reach a domain manager.
- AuthGate and RiderManager receive the signing key from
  `Messaging__SigningKey`; no key is tracked in source control.
- RabbitMQ is restricted to the internal Compose network. Each service receives
  an independent generated credential; the original rider and rental message
  flows use isolated vhosts and are restricted to their declared queues.
- Local RabbitMQ definitions contain password hashes rather than plaintext
  passwords and remain ignored by Git. They are imported when a blank broker
  node starts.

## Consequences

- Direct JSON injection and payload identity substitution no longer form an
  unauthenticated write path into RiderManager.
- Rotating `RIDER_EVENTS_SIGNING_KEY` requires a coordinated AuthGate and
  RiderManager restart. In-flight messages signed by the old key must be
  drained or deliberately discarded during the cutover.
- Reusing an already accepted, correctly signed message is not prevented here.
  Replay protection, durable queues, delivery guarantees, and DLQ redesign
  remain in Epic 8.
- Existing RabbitMQ volumes predate the imported users and vhost. Local
  operators must recreate the broker volume when adopting this decision.
