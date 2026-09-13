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

| Operation | A revoked access token is refused | Asserted by |
|---|---|---|
| High-value: `POST /api/rental/create` | Immediately, once the revocation is written | gateway `refuses_a_revoked_token_on_rental_creation`; identity `TestLogoutDeniesTheAccessTokensTheSessionHandedOut` |
| Everything else | When the token expires, at most 5 minutes after it was issued | gateway `a_revoked_token_still_reads_until_it_expires`; identity `TestTokenCarriesExactlyWhatTheGatewayRequires` (lifetime 300 s) |

### Who writes the denylist, and why Redis can lose it

The gateway reads `projecty:revoked:jti:{jti}`. `identity` writes it, since
[#59](https://github.com/iVega123/ProjectY/issues/59). The key format is pinned
on both sides: gateway `the_denylist_key_is_the_one_identity_writes`, identity
`TestTheKeyIsTheOneTheGatewayReads`.

- **The session knows its access tokens.** Every `refresh_tokens` row records
  the `jti` and expiry of the access token issued with it
  (`007_access_token_revocation.sql`). The `jti` is reserved before the row is
  written, so it lands in the same statement that opens or rotates the family.
- **Three things revoke, and each denies what it revoked:**
  - **Logout** revokes the family.
  - **A replayed refresh token** revokes the family, and with it the thief's
    access token.
  - **`DELETE /api/auth/users/{id}/sessions`** revokes every session of the
    user. The user or an `Admin` may call it; anyone else gets 404.

  In all three cases the `UPDATE … RETURNING` hands back the family's access
  tokens, and they are written to Redis before the response.
- **CockroachDB first, Redis second.** The revocation is committed to the
  database, then copied to Redis. If the copy fails, the request still
  succeeds. Logging out does not wait on a cache, and answering an error would
  leave the user unsure whether they logged out.
- **The copy is rebuilt, not trusted.** Every 5 seconds `identity` rewrites every
  revoked access token that has not expired from CockroachDB into Redis. A copy
  lost to an outage or a restart is back within one pass.
- **Each key expires 60 seconds after its token.** The gateway accepts a token up
  to its 30 second clock-skew leeway past `exp`, so a key that vanished at `exp`
  would let a revoked token through for those seconds. The expiry is absolute,
  so a rewrite never extends it.

**What that leaves, stated:**

- **While Redis is down**, the gateway refuses every high-value operation
  (fail closed), so no revoked token gets through.
- **Between Redis coming back empty and the next pass**, at most 5 seconds, a
  token revoked during the outage could create a rental.
- **Tokens issued before migration 007** carry no recorded `jti` and cannot be
  denied. They expire within 5 minutes of the deploy.

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
- **A session id claim in the token, denied per session instead of per `jti`.**
  One key per logout instead of one per live access token. It would change the
  token format and the gateway's check for a saving of a few keys: a session
  has at most two or three live access tokens at a time.
- **Failing logout when Redis is down.** It would make "logged out" and "denied
  on high-value operations" happen together, at the cost of stranding users in
  a half-state during an outage in which the gateway is already refusing every
  high-value operation. The durable record and the resync give the same
  guarantee without the stranded user.

## Consequences

- **Losing Redis no longer ends anyone's session.** The degradation row becomes
  honest and narrow: rate limiting fails open, idempotency degrades, high-value
  revocation checks fail — and that last one fails *closed*, consistent with
  ADR 0003. Login, refresh, logout and revoke-all keep working, and
  `TestWithRedisDownNobodyIsLoggedOutAndLogoutStillEnds` asserts it.
- **`identity` writes to its database on every refresh.** Small, and it is the
  service that should own that write.
- **`identity` talks to the shared Redis**, write-only, through
  `IDENTITY_REDIS_URL`. It starts without it.
- **Revocation has two speeds, and both are documented:** immediate for
  high-value operations, up to five minutes for everything else.
- **The refresh token table ships with `identity`'s schema**
  (`005_identity.sql`), which closed the open half of audit finding
  "Contradição 03".

## Follow-up

- [ADR 0006 — Secret loading and JWT boundaries](0006-secret-loading-and-jwt-boundaries.md)
- [ADR 0008 — A single trust boundary](0008-single-trust-boundary.md)
