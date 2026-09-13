# ADR 0008 — One trust boundary, at the edge

- **Status:** Accepted
- **Date:** 2026-08-30
- **Related findings:** C2, M10, A7, A8, B2, B3

## Context

The audited system reimplemented authorization in every service. Four copies of
`AuthorizationFilter` and `AdminAuthorizationFilter` existed, maintained by
copy-paste, and they had **already diverged**: the RentalOperations copy fell
through to a signature-only check, so any valid rider token was accepted on an
admin path (finding C2).

That is the failure mode of duplicated security code. It is not that someone
wrote a bad filter — three of the four were correct. It is that a security fix
has to be applied four times, and forgetting one creates a hole nobody sees.

Two more consequences followed from the same shape. `[Authorize]` and the custom
filters were stacked on the same endpoints with conflicting semantics, so
service-to-service calls carrying only an API key were rejected while two
endpoints ended up with no attribute at all (B2). And identity handling drifted:
`role.Contains("Admin")` on a possibly-null value returns 500 rather than 403
(B3).

## Decision

**Identity is verified exactly once, at the edge. Domain services never parse a
token.**

- The gateway validates the JWT with `iss` and `aud` enforced, checks the
  revocation list, and forwards a signed identity to the upstream service.
- Inbound `x-identity-*` headers are stripped before being set, so a client
  cannot forge identity by sending the header itself.
- The original `Authorization` header and cookies are **not** forwarded. The
  upstream trusts the gateway, not the caller.
- Domain services apply **domain** authorization only — "is this rental mine",
  not "is this token valid".
- Where the edge also gates by role, it mirrors the service rather than
  inventing its own answer. A blanket `Admin` over an upstream is easy to write
  and reads as safe, but when the service already allows a rider the two
  disagree in silence, and the wrong one stays invisible until someone needs
  that route. Reading one motorcycle is a rider action in
  `MotorcyclesController`; listing the fleet is not; the gateway now says the
  same thing.
- The three inter-service API keys are removed; service-to-service calls carry
  gateway-issued identity like any other request.

This is what makes the gateway worth building. Until the copies are gone, the
duplication keeps regenerating the class of bug that produced C2.

### The envelope

The gateway signs an HMAC-SHA256 over a newline-separated canonical string. It
sends the signature base64url-encoded without padding, next to
`x-identity-key-id`, `x-identity-subject`, `x-identity-roles` and
`x-identity-issued-at`. A verifier rebuilds the string from the request it
received and refuses an envelope older than 30 seconds or more than 5 seconds
ahead of its clock.

Two versions exist. `v1` is sent as `x-identity-signature: v1=<signature>`:

```text
v1
key-id
subject
comma-separated-roles
issued-at
HTTP-METHOD
path-and-query
audience
```

`v2` is sent as `x-identity-signature-v2: v2=<signature>`. It is `v1` with the
version changed and one more line, the digest of the body. It was added in
[#191](https://github.com/iVega123/ProjectY/issues/191), because `v1` let a
captured envelope carry a different body on the same route inside its window:

```text
v2
key-id
subject
comma-separated-roles
issued-at
HTTP-METHOD
path-and-query
audience
sha256-of-body
```

- **The digest** is the SHA-256 of the body bytes, in lowercase hex. It covers
  the body after transfer coding is removed and before any content coding is.
- **No body and an empty body are the same thing.** Both use the digest of zero
  bytes, `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`. A
  `GET` does not sign an empty line, a dash or nothing.
- **The gateway never streams a body it signs.** The digest only exists after the
  last byte, and the headers leave before the first one. So the gateway reads
  every authenticated body, up to 32 MiB, signs it, and sends exactly those bytes
  with a `Content-Length`. A chunked request is signed over everything read.
- **Above 32 MiB the gateway answers 413 and forwards nothing.** Verifiers refuse
  a body above the same limit, because no legitimate envelope covers one.
- **Verifiers read the body to its end, with or without a `Content-Length`.**
  They then hand it back to the handler unread.
- **Rollout.** The gateway sends both signatures. A verifier that finds `v2`
  checks `v2` alone, and never falls back to `v1` when `v2` fails; that fallback
  would accept exactly the substituted body `v2` refused. Without `v2`, a
  verifier still accepts `v1`. That makes the rollout safe in both directions:
  - a verifier deployed before the gateway still accepts the old gateway;
  - a verifier not yet on `v2` reads the same five headers as before and ignores
    the new one, so the gateway can be deployed first.
- **The price of that is a downgrade.** Stripping `x-identity-signature-v2` from a
  captured envelope falls back to the signature that does not cover the body.
  It closes when the verifiers stop accepting `v1`, the follow-up of #191.
  [ADR 0025](0025-tls-terminates-at-the-ingress.md) states what remains.

Golden vectors pin the string on both sides. `signs_the_v2_envelopes_the_verifiers_pin`
in the gateway asserts the exact signatures for three requests, one each for
identity, billing and rental-core. The values were computed separately, with
`openssl`, from the strings above. Each verifier accepts the one for its
audience, refuses it with a substituted body, and accepts it with `v2` removed:
- identity: `TestAcceptsAV2EnvelopeTheGatewaySigned`
- billing: `GatewayIdentityTest`
- rental-core: `GatewayIdentityEnvelopeTests`

## Alternatives considered

- **A shared authorization library referenced by all four services.** Removes
  the copy-paste but not the four deployment units that can drift in version,
  and it leaves every service holding a signing key. It is the right answer when
  a gateway is not wanted; here the gateway exists for other reasons anyway.
- **A service mesh handling authentication.** Rejected in ADR 0001: the
  operational weight does not pay for itself at this size, and having the policy
  visible as code is a feature for this repository, not a cost.
- **Leaving the filters and fixing only the divergent one.** Closes C2 and
  leaves M10 — the mechanism that produced it — fully intact.

## What was explicitly rejected

Trusting an unsigned identity header. Forwarding `x-identity-subject` without
stripping the inbound value first would replace a duplicated-code vulnerability
with a header-spoofing one, which is strictly worse: it would look correct.

## Consequences

- The gateway becomes a single point of failure for authentication. It is
  mitigated by being stateless and horizontally scalable, and by the fail-closed
  posture on revocation in ADR 0003 — but it is a real trade accepted here.
- Domain services can no longer be called directly in a trusted way. In the
  local stack this is enforced by network policy rather than by the network
  topology alone.
- Every endpoint's protection has to be re-verified when the filters are
  deleted; the removal is only safe with tests asserting the previous behaviour.

## Follow-up

- [Epic 6 — Rust edge gateway](https://github.com/iVega123/ProjectY/issues/7)
- [Verify identity once at the edge](https://github.com/iVega123/ProjectY/issues/58)
- [Delete the four duplicated authorization filters](https://github.com/iVega123/ProjectY/issues/62)
