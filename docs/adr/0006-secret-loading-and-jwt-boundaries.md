# ADR 0006: Secret loading and JWT trust boundaries

- Status: Accepted
- Date: 2026-08-29
- Decision owners: ProjectY maintainers
- Related finding: C3

## Context

The four legacy .NET services stored database, broker, object-storage, API-key,
and JWT credentials in tracked `appsettings.json` files. They also shared one
JWT signing key and did not validate issuer or audience. A token minted for one
service was therefore accepted by every service.

Deleting a secret from the current tree does not remove it from Git history.
Every previously tracked value is considered compromised and must be replaced
in each environment before that environment runs this revision.

## Decision

Tracked configuration contains only non-secret values. Secrets are supplied to
the standard .NET configuration provider through environment variables, using
double underscores for nested keys. Local Compose reads them from an ignored
`.env`; CI and hosted environments must inject the same variable names from
their secret manager.

The required variables and their exact names are listed in `.env.example`.
`scripts/New-LocalSecrets.ps1` creates a fresh local set using a cryptographic
random-number generator. Compose uses required-value interpolation, so startup
fails instead of silently falling back to a known password.

AuthGate is the sole token issuer. It holds a separate signing key for each
logical audience:

| Audience | AuthGate signing setting | Validator setting |
|---|---|---|
| AuthGate | `Jwt__SigningKeys__AuthGate` | `Jwt__SigningKeys__AuthGate` |
| MotoHub | `Jwt__SigningKeys__MotoHub` | `Jwt__SigningKey` in MotoHub |
| RiderManager | `Jwt__SigningKeys__RiderManager` | `Jwt__SigningKey` in RiderManager |
| RentalOperations | `Jwt__SigningKeys__RentalOperations` | `Jwt__SigningKey` in RentalOperations |

Clients request the intended logical audience during login. AuthGate emits
both `iss` and `aud`; every API validates signature, issuer, and its own exact
audience. An API receives only its validator key, while AuthGate necessarily
receives all signing keys.

## Consequences

- A leaked API validator key no longer makes tokens valid in sibling APIs.
- Tokens cannot be replayed from one ProjectY service to another.
- Missing secrets stop startup/configuration instead of selecting unsafe
  defaults.
- Rotating one service key invalidates that service's existing tokens without
  forcing sibling services to rotate.
- Because signing remains symmetric, compromise of AuthGate still exposes all
  token-signing keys. Moving to asymmetric signing with public validator keys
  is a future hardening option.
- Git-history cleanup may reduce accidental discovery but is not a substitute
  for credential rotation and is not performed by this task.

## Operational procedure

1. Generate or provision new values; never reuse any value from Git history.
2. Store them in the environment's secret manager (or ignored local `.env`).
3. Replace credentials in PostgreSQL, MongoDB, RabbitMQ, MinIO, Grafana, and
   every service/API-key consumer as one coordinated deployment.
4. Revoke the prior credentials and restart all four services.
5. Record the environment and UTC completion time in the rotation ledger.

See [the credential rotation ledger](../security/credential-rotation.md).
