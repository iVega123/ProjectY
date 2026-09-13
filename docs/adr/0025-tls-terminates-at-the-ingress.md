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
HMAC over method, path, subject, roles, audience, issue time and, since `v2`,
the body, with a 30 second maximum age. A service that receives a request
without a valid envelope refuses it. mTLS would answer "is this peer who it says
it is"; the envelope already answers a stronger question — "did the gateway
authorise *this request*, for *this subject*, *now*".

**The body, and how the gap in it was closed.** When this record was first
written, the canonical string had no digest of the payload, and it said so here.

- **The gap.** An attacker who can read *and inject* cluster traffic could
  capture an envelope and reuse it **on the same method and path, within the 30
  second window, with a different body**. A captured `PUT /update-image` could be
  replayed with a different CNH image, and it authenticated as the victim.
- **The fix.** [#191](https://github.com/iVega123/ProjectY/issues/191) added a
  `v2` canonical string whose last line is the SHA-256 of the body. The gateway
  now buffers and signs every authenticated body, and identity, billing and
  rental-core refuse a `v2` envelope whose body does not match. identity tests
  the exact case above through the whole route: a substituted CNH image is
  refused and nothing is stored.
- **Before and after.** Replay to another route, another verb or another
  audience failed before, and fails now. Same-route body substitution fails too.

**What remains accepted, now that the body is bound:**

- **Confidentiality on the wire inside the cluster.** An attacker with packet
  capture reads the envelope and the payload, the CNH image included. The
  envelope authenticates and binds; it does not encrypt.
- **Replay of the identical request.** There is still no nonce, so the same
  method, path and body can be sent again within 30 seconds. The replay repeats
  what the victim already asked for; it cannot change it. `PUT` and `DELETE` are
  idempotent, and a rental created with an `Idempotency-Key` returns the first
  result. The header is optional, so a create sent without one can be repeated.
- **The rollout downgrade, until `v1` is removed.** The gateway sends both
  signatures, and a verifier still accepts `v1` when `v2` is absent, so neither
  side can be deployed first and lock the other out. The same rule lets an
  attacker strip `x-identity-signature-v2` from a captured envelope and fall back
  to the signature that does not cover the body. Removing `v1` acceptance from
  the three verifiers closes it; that is
  [#274](https://github.com/iVega123/ProjectY/issues/274).

What mTLS would add on top is therefore confidentiality, and with it the end of
identical-request replay, since an attacker could no longer inject into the
session. Against an attacker who can already read and inject pod-to-pod traffic,
a service mesh is one control; a default-deny network policy plus a single
ingress is another. The mesh also brings a sidecar per pod, certificate rotation
to operate, and a second identity system whose relationship to the envelope
would have to be explained. In a system that runs seven languages, each new
cross-cutting concern is paid seven times (the argument of ADR 0014 against
GraphQL, applied to the transport).

**The cost accepted:** an attacker with packet capture inside the cluster reads
the envelope and the payload, and can repeat the identical request for 30
seconds. Once `v1` acceptance is removed, that attacker can no longer substitute
the payload. That is the trade, stated.

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
  are load-bearing together. Since #191 it carries integrity of the body as
  well. It does not carry confidentiality, and the `v1` fallback keeps the body
  unbound until the verifiers drop it. This record says both where the decision
  rests on them, rather than in a footnote.

## What implemented it

Epic 10 landed, and with it this decision, in #100:

- cert-manager issues a self-signed root, the `projecty-local-ca` authority from
  it, and the `projecty-tls` serving certificate from that authority.
- **The deployment layer names no certificate.** A `tls` block on the Ingress
  needs `hosts` to do anything — ingress-nginx maps SNI to a Secret by hostname
  and ignores an entry without them, which would have reproduced the A4 defect
  itself: a stated guarantee that does not hold. And the hostnames are not
  shareable, because the same base renders for AWS. So each environment names
  its certificate in its own platform layer: `--default-ssl-certificate` on the
  controller here, an ACM annotation in front of the load balancer there.
- `force-ssl-redirect` at the ingress is what replaced
  `UseHttpsRedirection()` — the redirect now lives at the only hop that knows
  the original scheme.
- `UseForwardedHeaders` in `rental-core`, with a forward limit of two, since the
  ingress and the gateway are both proxies in front of it. The known-proxy list
  is empty, so the trust in that header rests on the default-deny
  `NetworkPolicy` rather than on the middleware — noted where it is configured.
- `scripts/Test-IngressTls.ps1` verifies the chain against the cluster's own
  authority and requires a client without it to be refused.

What did not change is the paragraph above it: between services the traffic is
still plain HTTP, and the body-integrity gap is still open as #191.

## Follow-up

- [Epic 10 — Local Kubernetes and signed admission](https://github.com/iVega123/ProjectY/issues/11)
- [A4 — Terminate TLS at the ingress](https://github.com/iVega123/ProjectY/issues/100)
- [#191 — Bind the identity envelope to the request body](https://github.com/iVega123/ProjectY/issues/191)
