# ADR 0014 — Read aggregation belongs to the BFF, not the edge

- **Status:** Accepted
- **Date:** 2026-09-01

## Context

A single console screen needs data owned by several services: the rental, the
motorcycle, the rider's name, the invoice. Something has to compose them, and
the obvious candidate is the component every request already passes through —
the gateway.

## Decision

**Composition happens in the BFF. The gateway stays a security boundary and
nothing else.**

The two concerns have opposite change rates and opposite failure postures, and
that is what decides it:

| | Gateway | Read aggregation |
|---|---|---|
| Changes | rarely — it is a security boundary | every time a screen changes |
| Fails | **closed** — no valid token, no entry | **soft** — render what arrived |
| Is tested by | fuzzing, penetration testing | screen tests |

A schema that stitches Rental to Rider is domain shape knowledge. Putting it in
the gateway couples a weekly-changing artefact to the thing that verifies
tokens: every field added to a screen becomes a deploy of the trust boundary,
and the deploy coupling of eight services converges on one point.

The gateway keeps the work that actually justifies Rust at the edge — signature
verification against JWKS, rate limiting, idempotency keys, routing, timeouts,
circuit breakers, bulkheads, trace context injection, body size limits. None of
it changes when a screen changes.

The BFF is allowed to know screen shapes because it is deployed with the screen.
ADR 0001 justifies Node for exactly this: *pure I/O fan-out and shaping for the
screen; shared TypeScript types remove a class of contract bug.*

The console still calls back through the gateway with the user's token, so the
single trust boundary of ADR 0008 survives.

## Alternatives considered

- **GraphQL at the gateway.** The God Gateway arriving through the back door:
  the edge would have to know every service's schema. And DataLoader does not
  batch on its own — it groups keys into *one* call, which requires a batch
  endpoint downstream. Without that it is N parallel calls, which is what
  already existed.
- **Apollo Federation with subgraphs.** It does solve the coupling — each
  service owns its part of the schema. Rejected for two costs that are not
  visible at first: GraphQL becomes a mandatory cross-cutting concern in seven
  languages, with federation directives and uneven library maturity in each;
  and the mature federated gateway in Rust is Apollo Router, which is
  *configured* rather than written — hollowing out the one row of ADR 0001 that
  says Rust is at the edge because it parses hostile input continuously.
- **Direct calls from the console to services.** Rejected: the console would
  become a second place that decides whether a caller is who it claims to be,
  which is the thing ADR 0008 exists to prevent.

## What was explicitly rejected

- **GraphQL anywhere in this architecture, for now.** A BFF and GraphQL are
  substitutes, not complements: what GraphQL buys is decoupling the client's
  data needs from the server's endpoints, and in a BFF the server is deployed
  *with* the client, so that decoupling is already free. Paying for schema,
  resolvers, query complexity analysis and an arbitrary-query attack surface at
  the edge, to buy a property the topology already grants, is not a trade.
  **The trigger to revisit:** a third independent consumer with divergent data
  needs, or a partner that consumes the API — someone whose deploys are not
  ours. A second client is answered by a second BFF, not by GraphQL.

## Consequences

- **Batch endpoints are a contract requirement, not an optimisation.**
  `GET /riders?ids=a,b,c` and its equivalents have to exist, or the N+1 simply
  moves. This belongs in the consumer-driven contracts, so a service cannot
  remove one without a test going red.
- **The DataLoader *pattern* survives; the technology does not.** Per-request
  batching and deduplication is a generic technique and works in a REST BFF.
- **One extra internal hop.** The console calls services through the gateway
  rather than beside it. That is the price of a single trust boundary, and it
  is measurable: it belongs in the latency budget, not in a footnote.
- **The escape hatch, if BFF fan-out becomes the bottleneck**, is a read model
  fed by the events of ADR 0015 — the same mechanics that already carry rider
  state to `billing`. Not now. The trigger is a p99 on the rental screen
  dominated by fan-out rather than by any single service.

## Follow-up

- [Epic 11 — AWS, Terraform and cost profiles](https://github.com/iVega123/ProjectY/issues/12)
