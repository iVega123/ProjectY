# ADR 0024 — One token, many audiences

- **Status:** Accepted
- **Date:** 2026-09-08
- **Supersedes:** the one-audience-per-token half of ADR 0006
- **Related findings:** C3

## Context

ADR 0006 gave each service its own audience, and `AuthGate`'s login took the
audience as a request parameter: one token, one audience, chosen at login.
ADR 0013 replaced the *signing* half of that record with EdDSA and a JWKS. The
shape of the token was left alone.

Under ADR 0008 the gateway is the only validator, and it checks the token's
`aud` against the audience configured for the upstream the request is going to.
A screen that shows a rental and its invoice touches two upstreams, so it needs
two tokens. The console holds one, and has no credential with which to ask for
a second.

Issue #137 hit this and resolved it the only way available at the time: point
`GATEWAY_JWT_AUDIENCE_BILLING` at `projecty.rental-core`, so a rental-core
token works at billing. The comment written beside the value says what it is —
"É um afrouxamento real" — and names this issue as where the boundary gets
redrawn.

## Decision

**`identity` mints access tokens whose `aud` is a list**, and the issuer decides
what goes in it, per subject and per role. Each service keeps validating its own
name; no service answers to another's.

The difference from the #137 compromise is not cosmetic. With a shared audience
name, the claim stops identifying anybody: two services answer to one string,
and nothing at the validator can tell them apart — a token minted for rental-core
is *indistinguishable* from one minted for billing, because there is no such
distinction left to make. With a list, every service's name still exists and is
still checked; what moved is **who decides which names a token carries**, from
per-service configuration to the issuer, which is the one component that holds
the caller's identity and roles at the moment of minting.

## Alternatives considered

- **A token per audience, and a token endpoint the console calls per upstream.**
  The purest reading of ADR 0006. The console then holds N tokens with N
  expiries and N refresh paths, and adding an upstream becomes a console change.
  It buys a property (below) that ADR 0008 already provides by another route.
- **Keep the #137 weakening.** Cheapest, and it makes `aud` a claim that no
  longer names anything. A validated claim that cannot distinguish its subjects
  is worse than an absent one, because it reads as a control.
- **Stop validating `aud` at all.** At least honest. Rejected: the claim is
  cheap to check and the day a service does get its own token shape, the check
  has to already be there.

## What was explicitly rejected

Pretending the cost is zero. **A token in a list is replayable at every service
in that list.** Under one-audience tokens, a bearer token captured at
`rental-core` could not be presented at `billing`. Under this record, it can.

What makes that acceptable is ADR 0008, and specifically the part of it that
already shipped: **the gateway strips the bearer token and forwards a
per-request signed identity envelope instead.** No upstream ever sees the token.
The scenario per-audience tokens defended against — a compromised service
replaying a token it was handed — describes a system where services receive
tokens, and this one does not. The separation was protecting against a threat
the trust boundary had already removed; what it cost was real, and the #137
comment is where that cost surfaced.

## Consequences

- `IDENTITY_AUDIENCES` is a deployment-level list. A new upstream that is not in
  it gets `401` from the gateway for every caller — it fails closed, which is
  the right direction, and it is a configuration step that has to be remembered.
- `GATEWAY_JWT_AUDIENCE_BILLING` goes back to `projecty.billing`, and
  `BILLING_IDENTITY_AUDIENCE` with it. The #137 compromise is gone rather than
  documented forever.
- Token size grows by a few bytes per audience. It is a bearer token on a header,
  five minutes long; this is not a budget worth managing.
- Narrowing the set per role becomes possible without a schema change: the list
  is built at mint time from the subject's roles. Nothing does that yet, and the
  place to do it is one function.

## Follow-up

- ADR 0006 — the record whose audience half this replaces.
- ADR 0008 — the trust boundary that makes the trade acceptable.
- ADR 0013 — the signing half, replaced earlier and shipped with the same
  service.
- #136 (the issuer), #137 (where the compromise was written down).
