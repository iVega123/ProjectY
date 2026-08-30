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
- The three inter-service API keys are removed; service-to-service calls carry
  gateway-issued identity like any other request.

This is what makes the gateway worth building. Until the copies are gone, the
duplication keeps regenerating the class of bug that produced C2.

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
