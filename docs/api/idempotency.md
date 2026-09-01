# Idempotency-Key semantics

All `POST`, `PUT`, `PATCH`, and `DELETE` endpoints in the four baseline APIs
accept the optional `Idempotency-Key` request header. Clients should generate a
new opaque key for each intended state change and reuse that key only when
retrying the same request after a timeout or connection failure.

The guarantee is scoped to one service and authenticated caller, and lasts 24 hours. Redis stores the
key, a SHA-256 request fingerprint, and the complete HTTP response. The
fingerprint covers the method, path, canonical query string, authenticated user
identifier, content type, and raw request body. Keys are hashed before they are
used in Redis keys.

| Situation | Result |
|---|---|
| Header omitted | The request executes normally without replay protection. |
| New key | Redis claims the key and the request executes once. |
| Same key and fingerprint after completion | The stored status, headers, and body are replayed with `Idempotency-Replayed: true`. |
| Same key with a different fingerprint | `422 Unprocessable Entity`; the new request does not execute. |
| Same key while the first request is running | `409 Conflict` with `Retry-After: 1`; the second request does not execute. |
| Invalid or multiple key values | `400 Bad Request`. |
| Redis unavailable before the claim is acquired | `503 Service Unavailable`; the request does not execute. |
| Endpoint execution throws after a possible side effect | `503 Service Unavailable`; the unknown outcome is retained and the same key never executes again. |
| Redis fails after the endpoint completes | `503 Service Unavailable` reports an unknown outcome; the pending claim is retained. |

Pending claims and successful responses remain protected for 24 hours. Using
the full retention TTL for in-flight work prevents a second replica from
claiming a long-running or ambiguously failed request. The duration and maximum
key length are configurable through the `Idempotency` configuration section.
The Compose stacks store Redis data in an append-only, persistent volume and
fsync every idempotency write before Redis acknowledges it.

Example against RentalOperations:

```http
POST /api/Rental/create HTTP/1.1
Authorization: Bearer <token>
Content-Type: application/json
Idempotency-Key: rental-request-001

{
  "motocycleLicencePlate": "ABC1D23",
  "startDate": "2026-09-01T00:00:00Z",
  "predictedEndDate": "2026-09-08T00:00:00Z"
}
```

The Swagger document for each service also exposes this header on every
state-changing operation and documents the `409` and `422` responses.
