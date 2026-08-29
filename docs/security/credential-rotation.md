# Credential rotation ledger

All credentials that ever appeared in ProjectY Git history are compromised.
Removing them from the current tree is only containment; it does not revoke
copies in clones, caches, images, logs, or running infrastructure.

## C3 cutover

| Scope | UTC date | State | Evidence / required action |
|---|---|---|---|
| Tracked application configuration | 2026-08-29 | Removed | Secret fields were removed from all four `appsettings.json` files. |
| Root and modernization Compose defaults | 2026-08-29 | Removed | Known defaults were replaced by required environment interpolation. |
| Local development credentials | On first run after 2026-08-29 | Operator action required | Run `scripts/New-LocalSecrets.ps1`; its `.env` header records the precise UTC generation time. Recreate volumes initialized with old credentials. |
| Shared JWT signing key | On first deployment after 2026-08-29 | Operator action required | Provision four independently generated keys, deploy all issuers/validators together, then revoke the old key. |
| Hosted database, broker, object-storage, Grafana, and API-key credentials | On first deployment after 2026-08-29 | Environment-owner action required | Rotate in the provider/secret manager, deploy consumers, revoke prior values, and append a dated row below. |

The repository cannot prove that an external credential was revoked. A release
must not mark the environment rotation complete until an owner records it here
or in the environment's auditable secret-management system.

## Environment completions

Append one row per environment without including secret material.

| Environment | Completed at (UTC) | Operator/change reference | Credentials revoked |
|---|---|---|---|
| _Example: staging_ | _YYYY-MM-DDThh:mm:ssZ_ | _deployment/change ID_ | _JWT, DB, RabbitMQ, MinIO, Grafana, API keys_ |
