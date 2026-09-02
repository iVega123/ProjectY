# API gateway

The Rust/Axum gateway is the strangler entry point in front of ProjectY's four
audited ASP.NET Core services. It owns routing and process lifecycle in task
[#57](https://github.com/iVega123/ProjectY/issues/57); authentication, rate
limiting, resilience and OpenTelemetry arrive in the following Epic 6 tasks.

## Routes

| Public path | Current upstream |
|---|---|
| `/api/auth/**` | AuthGate |
| `/api/riders/**`, `/update-image` | RiderManager |
| `/api/motorcycles/**` | MotoHub |
| `/api/rental/**` | RentalOperations |

The gateway owns `/health/live` and `/health/ready`. Unknown paths return `404`
instead of being guessed or forwarded to a default service.

## Configuration

All runtime configuration comes from environment variables.

| Variable | Required | Purpose |
|---|---|---|
| `GATEWAY_BIND` | No (`0.0.0.0:8090`) | Listener address |
| `GATEWAY_HEALTH_URL` | No (`http://127.0.0.1:8090/health/ready`) | URL used by the binary probe |
| `GATEWAY_HEALTHCHECK_TIMEOUT_MS` | No (`2000`) | Probe timeout |
| `GATEWAY_UPSTREAM_AUTH_GATE` | Yes | AuthGate base URL |
| `GATEWAY_UPSTREAM_RIDER_MANAGER` | Yes | RiderManager base URL |
| `GATEWAY_UPSTREAM_MOTO_HUB` | Yes | MotoHub base URL |
| `GATEWAY_UPSTREAM_RENTAL_OPERATIONS` | Yes | RentalOperations base URL |
| `GATEWAY_JWKS_URL` | Yes | Absolute JWKS URL; redirects are refused |
| `GATEWAY_JWT_ISSUER` | Yes | Exact trusted access-token issuer |
| `GATEWAY_JWT_AUDIENCE_AUTH_GATE` | Yes | Exact audience accepted on AuthGate routes |
| `GATEWAY_JWT_AUDIENCE_RIDER_MANAGER` | Yes | Exact audience accepted on RiderManager routes |
| `GATEWAY_JWT_AUDIENCE_MOTO_HUB` | Yes | Exact audience accepted on MotoHub routes |
| `GATEWAY_JWT_AUDIENCE_RENTAL_OPERATIONS` | Yes | Exact audience accepted on RentalOperations routes |
| `GATEWAY_JWKS_CACHE_TTL_SECS` | No (`300`) | Maximum age of cached public keys |
| `GATEWAY_JWKS_UNKNOWN_KID_REFRESH_SECS` | No (`5`) | Minimum interval between unknown-`kid` refreshes |
| `GATEWAY_JWKS_TIMEOUT_MS` | No (`2000`) | JWKS request timeout |
| `GATEWAY_JWT_CLOCK_SKEW_SECS` | No (`30`) | Accepted JWT clock skew |
| `GATEWAY_JWT_MAX_LIFETIME_SECS` | No (`300`) | Maximum `exp - iat` access-token lifetime |
| `GATEWAY_IDENTITY_SIGNING_KEY` | Yes | At least 32 bytes; HMAC key for the internal identity envelope |
| `GATEWAY_IDENTITY_SIGNING_KEY_ID` | Yes | Rotation identifier forwarded with the signed envelope |

`POST /api/auth/login` and `POST /api/auth/register/rider` are public. Every
other proxied route requires an EdDSA access token with `kid`, `sub`, `iat`,
`exp`, exact `iss`, and the route owner's exact `aud`. MotoHub routes and the
legacy admin endpoints in RiderManager and RentalOperations also require the
`Admin` role.

The gateway rejects every client-supplied `x-identity-*` header and never sends
the caller's `Authorization` or `Cookie` headers upstream. After verification it
adds `x-identity-subject`, sorted `x-identity-roles`, `x-identity-issued-at`,
`x-identity-key-id`, and `x-identity-signature`. The signature is base64url
HMAC-SHA256 over this newline-delimited canonical value:

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

The current .NET AuthGate still issues legacy HMAC tokens and does not expose a
JWKS. Protected gateway routes therefore fail closed until the Go identity
issuer from issue #136 lands; the two public AuthGate routes remain available
during that migration.

Run `api-gateway --healthcheck` to execute the same self-probe used by Compose.
The command needs only the two healthcheck variables, not upstream settings.
