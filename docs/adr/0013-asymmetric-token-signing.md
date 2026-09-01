# ADR 0013 — Tokens are signed asymmetrically and validated from a JWKS

- **Status:** Accepted
- **Date:** 2026-09-01
- **Supersedes:** the signing half of ADR 0006
- **Related findings:** C3

## Context

ADR 0006 replaced the single shared JWT key from finding C3 with one symmetric
key per audience. That was a real improvement — a leaked key stopped working on
sibling services — and its consequences section already flags what remained:
compromising the issuer exposes every signing key.

It does not record the sharper consequence. Under HMAC the validator key **is**
the signing key. Every service holds a secret that can *mint* tokens for its own
audience, not merely verify them. The record says a leaked validator key does not
work on siblings, which is true; it works perfectly well on itself, and a service
compromised through any other path can forge its own callers.

Rotation has the matching problem: changing a key means distributing a new secret
to whichever services validate that audience.

## Decision

**EdDSA (Ed25519) signing, with public keys published as a JWKS.**

- `identity` holds the private keys and is the only component that can sign.
- `identity` serves `/.well-known/jwks.json`, and an OIDC discovery document
  alongside it — the discovery document is cheap and makes the service legible to
  standard tooling.
- Validators fetch the JWKS and select the key by `kid`. **No validator holds a
  secret capable of signing anything.**
- Rotation: publish the new key first, sign with it only after propagation,
  overlap for at least one token lifetime, then retire the old one. Rotation is a
  JWKS update, not a secret distribution.
- Under ADR 0008 the gateway is the only validator on the request path, so it is
  the primary JWKS consumer. It caches with a bounded TTL, refetches once on an
  unknown `kid`, rate-limits that refetch, and **fails closed** when the JWKS
  cannot be resolved — consistent with the posture ADR 0003 sets for token
  verification.

This ships **with** the Go rewrite in ADR 0012, not after it.

## Alternatives considered

- **RS256.** Equally standard and equally well supported. EdDSA chosen for
  smaller keys and signatures and faster verification; the gateway verifies on
  every request, so verification cost is the one that compounds.
- **Keep symmetric per-audience keys.** The status quo. Cheapest, and it leaves
  every validator holding a minting key.
- **A full OIDC provider — authorization code flow, consent, clients.** Out of
  scope. The useful subset here is asymmetric signing, a JWKS and discovery;
  the rest is surface without a consumer.

## What was explicitly rejected

Rewriting identity in Go while keeping hand-rolled symmetric HMAC. That is the
worst available combination: the full cost of a rewrite with none of the security
gain. The two changes are deliberately coupled — the rewrite is the moment the
signing scheme is cheapest to change, and doing one without the other wastes it.

## Consequences

- The gateway gains a network dependency on `identity` for key material. It is
  bounded by the cache, and the failure mode is deliberate: unresolvable JWKS
  means requests are refused, not waved through.
- Key rotation becomes an operation with a procedure, which is more moving parts
  than a static secret and the reason it can actually be performed.
- Tests must cover: a valid token, an unknown `kid`, a key retired mid-flight,
  and the JWKS endpoint unreachable.
- ADR 0006's secret-loading half stands unchanged. Only its signing table is
  superseded.

## Follow-up

- ADR 0012 — the Go identity service this ships with.
- The validator side lands in the gateway epic; the issuer side in the identity
  epic.
