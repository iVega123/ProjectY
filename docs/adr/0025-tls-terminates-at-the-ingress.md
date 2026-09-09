# ADR 0025 — TLS terminates at the ingress, and inside the cluster is not mTLS

- **Status:** Accepted
- **Date:** 2026-09-09
- **Related findings:** A4

## Context

The audit's finding A4 has two halves, and the second is the worse one.

The first half: there is no TLS. No certificate is configured anywhere,
`ASPNETCORE_URLS` declares HTTP, and calls between services are plain `http://`.
Tokens, login credentials and CNH images travel in clear.

The second half: **the documentation said otherwise.** The README advertised
HTTPS on `8181`, `8001`, `8101` and `8201`; `docker-compose.yml` published one
of those ports with nothing listening on it; and every .NET service called
`UseHttpsRedirection()`, which without a known HTTPS port logs a warning and
lets the request through. A stated guarantee that does not hold is more
dangerous than an absent one: it is the difference between someone deciding to
tunnel the traffic and someone believing they do not have to.

The first half needs a cluster, and there is no cluster yet — that is
[Epic 10](https://github.com/iVega123/ProjectY/issues/11). The second half needs
one commit, and waiting for the ingress to make it was never justified.

This record exists because the issue asks for the in-cluster transport decision
to be **written down** before the cluster is built: "either answer is
defensible, an unstated one is not". Deciding it late means deciding it under
the pressure of a half-built cluster.

## Decision

**TLS terminates at the ingress. Inside the cluster, traffic is plain HTTP,
confined by network policy — not mTLS.**

Concretely, when Epic 10 lands:

- A local certificate authority for `kind`, and ACM in front of the load
  balancer on AWS. The gateway and the console are reachable over TLS; nothing
  else is reachable from outside at all.
- `ForwardedHeaders` configured on the services, so the application sees the
  original scheme instead of guessing it. That is what replaces
  `UseHttpsRedirection()`, which was removed rather than left as decoration.
- Default-deny `NetworkPolicy`, with each service allowed to reach only the
  dependencies it names.

## Why not mTLS between services

The honest reason is not that mTLS is wrong. It is that **this system already
authenticates every internal hop, and does it above the transport layer.**

ADR 0008 gives every service-to-service call a signed identity envelope: an
HMAC over method, path, subject, roles, audience and issue time, with a 30
second maximum age. A service that receives a request without a valid envelope
refuses it. mTLS would answer "is this peer who it says it is"; the envelope
already answers a stronger question — "did the gateway authorise *this
request*, for *this subject*, *now*".

**What the envelope does not do is protect the body.** The canonical string has
no digest of the payload and no nonce, so an attacker who can read *and inject*
cluster traffic can capture an envelope and reuse it, **on the same method and
path, within the 30 second window, with a different body**. Concretely: a
captured `PUT /update-image` can be replayed with a different CNH image and it
authenticates as the victim. Replay to another route, another verb or another
audience still fails — the canonical string binds those — and after 30 seconds
the envelope is dead. But same-route body substitution is a real integrity gap,
and it is the one place where the argument above is weaker than it sounds.

What mTLS would add on top is therefore two things, not one: confidentiality on
the wire inside the cluster, and integrity of the body between hops. Against an
attacker who can already read and inject pod-to-pod traffic, a service mesh is
one control; a default-deny network policy plus a single ingress is another. The
mesh also brings a sidecar per pod, certificate rotation to operate, and a
second identity system whose relationship to the envelope would have to be
explained — in a system that runs seven languages, each new cross-cutting
concern is paid seven times (the argument of ADR 0014 against GraphQL, applied
to the transport).

**The cost accepted:** an attacker with packet capture inside the cluster reads
the envelope and the payload, and — for 30 seconds, on the same route — can
substitute the payload. The CNH image in a `PUT /update-image` body is both
readable and replaceable under that attacker. That is the trade, stated.

**What would close it without a mesh:** a digest of the body in the canonical
string, as a `v2` envelope. That is a change in four implementations — the Rust
gateway that signs, and the C#, Kotlin and Go verifiers — with a rollout in
which both versions are accepted, so it belongs in its own change and not in a
documentation one. It is cheaper than a mesh and closes the integrity half
without the confidentiality half. Tracked as
[#191](https://github.com/iVega123/ProjectY/issues/191).

**The trigger to revisit:** a second tenant in the same cluster, a compliance
requirement that names encryption in transit between workloads, or the first
service that is not ours running beside these.

## Alternatives considered

- **mTLS via a service mesh (Istio, Linkerd).** Rejected above. Linkerd is the
  cheaper of the two and would be the choice if the trigger fires.
- **TLS terminated at each service.** Every service would need a certificate and
  a rotation story, in seven languages, for traffic that never leaves the
  cluster. It moves the cost to the place with the most copies of it.
- **Leaving `UseHttpsRedirection()` in place "for when TLS arrives".** This is
  what produced the finding. Code that does nothing but reads as if it does
  something is worse than absent code, because it answers the question "is this
  handled?" with a false yes.

## Consequences

- Until Epic 10, the stack is HTTP end to end, and every document says so
  plainly. That is the state this ADR makes legible, not one it creates.
- `UseHttpsRedirection()` is gone from `rental-core`. Reintroducing it without
  `ForwardedHeaders` behind a proxy would produce redirect loops, which is a
  louder failure than the silent one it had.
- The identity envelope carries the weight of internal authentication. If it
  were ever weakened, this decision would have to be reopened with it — the two
  are load-bearing together. It does not carry integrity of the body, and this
  record says so where the decision rests on it, rather than in a footnote.

## Follow-up

- [Epic 10 — Local Kubernetes and signed admission](https://github.com/iVega123/ProjectY/issues/11)
- [A4 — Terminate TLS at the ingress](https://github.com/iVega123/ProjectY/issues/100)
- [#191 — Bind the identity envelope to the request body](https://github.com/iVega123/ProjectY/issues/191)
