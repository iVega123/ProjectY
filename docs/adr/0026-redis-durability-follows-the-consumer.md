# ADR 0026 — Redis durability follows the consumer

- **Status:** Accepted
- **Date:** 2026-09-13
- **Deciders:** project maintainer
- **Related:** [#193](https://github.com/iVega123/ProjectY/issues/193), ADR 0009, ADR 0017

## Context

One Redis instance served four consumers with `--appendonly yes --appendfsync
always`, which means an fsync before replying to every write. The setting was
chosen for one of them: [ADR 0009](0009-exactly-once-effect.md#http-idempotency)
wanted an idempotency claim on disk before it was acknowledged. Every other
consumer paid for it too. The one that paid most is the rate limiter, which runs
a token-bucket script with two writes on **every** request through the gateway.

`scripts/Measure-RateLimiterRedis.ps1` measures that script as the gateway sends
it: `EVALSHA`, the configured capacity and refill, 10,000 principals, 50 clients.
Raw results are in
[`docs/measurements/rate-limiter-redis.json`](../measurements/rate-limiter-redis.json).

| Persistence | `SET` baseline | Token bucket | Token bucket p50 | vs `always` |
|---|---|---|---|---|
| `appendfsync always` | 11,744 rps | 11,274 rps | 4.9 ms | 1× |
| `appendfsync everysec` | 119,760 rps | 71,685 rps | 0.63 ms | 6.4× |
| no persistence | 97,087 rps | 84,388 rps | 0.50 ms | 7.5× |

Absolute numbers belong to the machine; the ratios are the finding. Lua is
single-threaded and every gateway replica shares the instance, so this is the
platform's rate-limiting ceiling, and adding gateway replicas does not raise it.

Above the ceiling the limiter does not refuse. Its 100 ms timeout expires and it
fails open, so a load test run past that point measures the platform without a
rate limiter, and the graph looks excellent.

The consumers do not need the same durability:

| Consumer | What losing the last writes does |
|---|---|
| Rate-limit buckets | Nothing that matters. A bucket refills by itself, and the limiter fails open anyway. |
| Revocation denylist | A revoked token can pass rental creation again until it expires, which is five minutes at most (ADR 0017). |
| Idempotency claims | A retried mutation can run a second time: a second rental. |
| Live-tracking state (`telemetry`) | A rental activated in that window loses its tracking state. Its `rental.started` offset is already committed, so nothing replays it. |

## Decision

**The rate limiter gets a Redis of its own, with no persistence. The shared Redis
moves to `appendfsync everysec`.**

- **`rate-limit-redis`** runs with `--save '' --appendonly no` and has no volume.
  - It is capped with `--maxmemory 96mb --maxmemory-policy volatile-ttl`. Every bucket carries a TTL, so under memory pressure a bucket is evicted rather than a check refused, and eviction only refills that bucket early.
  - Only the gateway reaches it (NetworkPolicy `rate-limit-redis-owner`).
  - The gateway reads `GATEWAY_RATE_LIMIT_REDIS_URL`, and falls back to `GATEWAY_REDIS_URL` when that is unset.
- **`redis`** runs with `--appendonly yes --appendfsync everysec`. It holds idempotency claims, the revocation denylist and live-tracking state.
- **ElastiCache has neither setting.** It has no AOF: its durability is replication and snapshots, and nothing fsyncs per write. The fallback keeps any deployment that does not set the new variable on the shared instance, as before.

## Alternatives considered

- **`everysec` for the whole instance, the limiter included.** One line, and the limiter reaches ~72k checks/s.
  - It lost because the consumer with no durability requirement would keep sharing one single-threaded process with the writes that do have one.
  - A burst of rate-limit checks would queue idempotency claims and revocation reads behind it, and the reverse.
- **A separate logical database on the same instance.** Persistence is set per instance, so this changes nothing.
- **A Redis of its own for the limiter, and `always` kept on the shared one.** No loss window anywhere.
  - It lost because idempotency, denylist and tracking writes would keep paying an fsync each, about 4.9 ms at p50 under 50 clients.
  - ADR 0017 already says Redis is protection and speed, never the source of truth.
- **Token buckets in gateway memory.** No Redis on the request path at all. It lost because per-replica buckets multiply every limit by the replica count, which is exactly what a shared atomic bucket exists to prevent.

## What was explicitly rejected

**Zero-loss HTTP idempotency.** ADR 0009 put a claim on disk before
acknowledging it. That guarantee is given up.
- **What can happen:** a Redis *crash* can lose up to one second of claims. A client that retries one of those requests with the same `Idempotency-Key` can then create a second rental.
- **What still holds:** the inbox and the database constraints in ADR 0009 still stop a duplicate *event* or invoice. They do not stop a second HTTP mutation whose key was forgotten.

The price was accepted because it takes two things at once:
- a Redis process crash, not a restart, since a clean shutdown flushes the AOF;
- a retry of exactly that request, arriving inside the lost second.

**Durable token buckets** were rejected outright. Nobody can use a durable bucket
for anything.

## Consequences

- **The limiter's ceiling on the benchmark machine rises from ~11k to ~84k checks per second.** Its p50 under 50 clients falls from 4.9 ms to 0.5 ms, far from the 100 ms timeout where it silently fails open.
- **The shared Redis writes about six times faster.** The idempotency and tracking paths get that too.
- **Operating cost rises.** There is one more workload (20 in the Kubernetes manifests), one more local container, one more Toxiproxy proxy and one more drill (`rate-limit-redis-down`). The `redis-down` drill no longer says anything about rate limiting.
- **The ceiling is a planning input, not a setting.**
  - `GATEWAY_RATE_LIMIT_*` accepts up to 1,000,000 per minute *per principal*. The limiter serves on the order of 84k checks per second for *all* principals together, on the machine above.
  - Limits raised past that (#71), or load driven past it, measure fail-open behaviour. `gateway_ratelimit_degraded_total` shows when that is happening.
- **Re-measuring is one command:** `scripts/Measure-RateLimiterRedis.ps1`.

## Follow-up

- [#193](https://github.com/iVega123/ProjectY/issues/193) — the measurement this record answers.
- [#71](https://github.com/iVega123/ProjectY/issues/71) — rate-limit values; raise them against the measured ceiling.
- [ADR 0009](0009-exactly-once-effect.md#http-idempotency) — its HTTP idempotency section now states the one-second window.
- [ADR 0017](0017-session-lifetime-and-revocation.md) — Redis is protection and speed, never the source of truth.
