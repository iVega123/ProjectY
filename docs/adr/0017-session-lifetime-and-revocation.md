# ADR 0017 — Session lifetime, revocation, and where refresh tokens live

- **Status:** Accepted
- **Date:** 2026-09-01

## Context

ADR 0008 puts token verification in one place: the gateway. That leaves two
questions it does not answer. How long is a token good for — and therefore how
long does a revocation take to bite? And where does the long-lived half of the
session live, given that it is the thing an attacker would most like to steal
and the thing a user would most notice losing?

## Decision

**Short access token, verified locally. Long refresh token, stored durably.**

- **Access token: 5 minutes, EdDSA.** The gateway verifies the signature against
  the JWKS it caches from `identity`. It does **not** consult Redis per request.
- **Refresh token: 7 days, stored in CockroachDB**, owned by `identity`.
- **Redis holds only the ephemeral:** rate-limit counters, idempotency keys, the
  revocation denylist.

That last line generalises into the invariant this record exists to establish:

> **Redis is never the source of truth — only protection and speed.**

It is checkable by reading the code, and it survives new people joining the
project, which is more than can be said for a convention held in someone's head.

### Revocation takes up to five minutes, and that is written down

Because the gateway verifies locally, a revoked identity keeps working until its
access token expires. This is the price of keeping Redis out of the hot path,
and it is a declared property rather than an oversight.

**Where five minutes is too long**, the escape hatch is a denylist consulted
*only on high-value operations* — creating a rental does one Redis read on a
low-QPS write path; reads do nothing. Immediate revocation where it matters,
without putting a cache back on the critical path of every request.

## Alternatives considered

- **Refresh tokens in Redis with a TTL.** The conventional choice, and what an
  earlier draft specified. Rejected on the strength of one question: *does
  losing Redis log everyone out?* With refresh tokens in Redis it does, and the
  degradation table then has to conflate "rate limiting degraded" with "every
  session on the platform destroyed" in a single row. Volume is not the
  obstacle — a refresh every five minutes per active session is roughly 33
  writes per second at ten thousand active users, which is noise for
  CockroachDB. The cost is one database write on the refresh path, and it buys
  a degradation table that says something useful.
- **Checking Redis on every request.** Immediate revocation, at the price of
  putting a cache in the hot path of the security boundary and making it a
  single point of failure for all traffic. The denylist on high-value
  operations buys most of the benefit for a fraction of the cost.
- **Long-lived access tokens with no refresh.** Fewer moving parts, and
  revocation measured in hours. Not a trade worth making for a system that
  moves money.

## Consequences

- **Losing Redis no longer ends anyone's session.** The degradation row becomes
  honest and narrow: rate limiting fails open, idempotency degrades, high-value
  revocation checks fail — and that last one fails *closed*, consistent with
  ADR 0003.
- **`identity` writes to its database on every refresh.** Small, and it is the
  service that should own that write.
- **Revocation has two speeds, and both are documented:** immediate for
  high-value operations, up to five minutes for everything else.
- **The refresh token table does not exist yet.** It belongs to `identity`'s
  schema, which is the open half of audit finding "Contradição 03" — the target
  schema has no identity tables at all. This record decides *where* the tokens
  live; the migration that creates them travels with the identity service.

## Follow-up

- [ADR 0006 — Secret loading and JWT boundaries](0006-secret-loading-and-jwt-boundaries.md)
- [ADR 0008 — A single trust boundary](0008-single-trust-boundary.md)
