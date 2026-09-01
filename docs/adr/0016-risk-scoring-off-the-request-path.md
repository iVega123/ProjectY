# ADR 0016 — Risk scoring stays off the request path

- **Status:** Accepted
- **Date:** 2026-09-01

## Context

`risk-pricing` bundles three workloads that look alike and behave nothing alike:

| Workload | Trigger | Is anyone waiting? |
|---|---|---|
| OCR of the driver's licence | once, at onboarding | no — the screen says "under review" |
| Fraud score for a rider | must be ready when a rental is created | **this is the question** |
| Pricing model | on rental creation | no — today it is a deterministic tier table |

Two of the three are asynchronous by nature. Only the fraud score is in dispute,
and the real question it hides is: **what happens to a rental that arrives while
the score is unknown?**

## Decision

**The score is computed off the request path and read locally.**

`risk-pricing` computes a rider's score on `rider.verified`, on `rental.closed`
and on a periodic pass, and publishes `risk.scored`. `rental-core` keeps that
score in the rider projection it already maintains for ADR 0015. At rental
creation it reads its own row: no network call, no circuit breaker, no
fail-open dilemma.

This is not a new pattern — it is the event-carried state transfer of ADR 0015,
applied to one more field. The decision stays consistent with the rest of the
architecture instead of carving an exception into it.

It also replaces an unanswerable question with an answerable one. Not *"can we
score in 50 ms?"* but *"how stale may a score be?"* — which is declarable:
recomputed on every rental close plus a daily pass, so staleness is bounded by
one rental cycle. That bound is a panel, not a hope.

**`risk-pricing` therefore exposes no synchronous API at all.**

### Two cases that need a written policy, not an implicit one

- **A rider with no score yet.** The only genuinely blocking case. A new rider
  enters a conservative default tier until scored. Without this written down,
  every rider's first rental meets a `null`.
- **Real-time signals a projection cannot hold.** "Three rentals in ten minutes"
  needs request-time state — but that is a counter, not statistics, and it
  already has a home: **Redis at the gateway**, which is there for rate
  limiting. Cheap velocity checks at the edge, expensive statistical work
  precomputed, and the core reads the result of both without leaving its
  process.

## Alternatives considered

- **Blocking, fail-closed.** No score, no rental. Rejected: it makes a Python
  service a hard dependency of revenue. `risk-pricing` goes down and the
  business stops.
- **Blocking, fail-open behind a circuit breaker.** The obvious compromise, and
  the dangerous one: **fail-open on a security control is an attack surface.**
  An attacker does not need to break the scorer, only to make it slow — and
  overloading it becomes the documented bypass of the fraud check. ADR 0003
  already draws this line: rate limiting fails open, token verification fails
  closed. A fraud score belongs to the second family, which is precisely why it
  must not sit on the synchronous path at all.
- **Splitting `risk-pricing` into two services.** OCR and scoring are both
  Python, both statistical, neither on a hot path. They share a toolchain and
  have no reason to become two deploys. They do have different triggers, and
  that is documented rather than deployed.

## Consequences

- **`risk-pricing` can be down for an hour and nothing user-facing breaks.**
  Scores freeze at their last value. New row for the ADR 0003 degradation
  table:

  > `risk-pricing` unavailable → rentals continue with the last known score; the
  > score's staleness is on the dashboard; an alert fires when it passes the
  > declared window.

- **The circuit breaker disappears from the rental path.** Breakers stay where
  synchronous calls actually are — the upload into `media-guard`, the console's
  calls through the gateway.
- **Fraud moves from prevented to bounded.** A score can be one cycle old, so a
  rider whose behaviour turned bad within that window can still rent. That is
  the accepted cost, and the velocity counters at the edge are what keep the
  window from being free.
- **Ownership is unchanged.** `risk-pricing` owns `risk_scores`; `rental-core`
  holds a projection, not the source.

## Follow-up

- [ADR 0003 — Observability and fault tolerance](0003-observability-and-fault-tolerance.md) — the degradation table this adds a row to
- [ADR 0015 — Event contracts and carried state](0015-event-contracts-and-carried-state.md)
