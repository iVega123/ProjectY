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
| Gateway identity-envelope key | On first deployment after issue #62 | Operator action required | Provision one fresh key to the gateway and the three legacy domain services. Rotate the gateway and validators as one deployment; envelopes expire after 30 seconds. |
| Hosted database, broker, object-storage, and Grafana credentials | On first deployment after 2026-08-29 | Environment-owner action required | Rotate in the provider/secret manager, deploy consumers, revoke prior values, and append a dated row below. The retired inter-service API keys must be deleted from the secret manager. |

The repository cannot prove that an external credential was revoked. A release
must not mark the environment rotation complete until an owner records it here
or in the environment's auditable secret-management system.

## C5 broker cutover

| Scope | UTC date | State | Evidence / required action |
|---|---|---|---|
| Broker host exposure | 2026-08-29 | Removed from Compose | AMQP, management, and the Toxiproxy RabbitMQ listener have no host port mapping. |
| Per-service RabbitMQ users and vhosts | On first run after 2026-08-29 | Operator action required | Regenerate `.env` and `.rabbitmq-definitions.json`, then recreate the RabbitMQ volume so boot-time definitions are imported. |
| Rider event signing key | On first deployment after 2026-08-29 | Operator action required | Provision a fresh `RIDER_EVENTS_SIGNING_KEY` to AuthGate and RiderManager together. Drain or deliberately discard messages signed with the previous key before revocation. |

Do not reuse the former shared broker password for any generated service
account. Hosted environments should provision equivalent isolated credentials
and vhosts through their broker control plane rather than copying the local
definitions file.

## Environment completions

Append one row per environment without including secret material.

| Environment | Completed at (UTC) | Operator/change reference | Credentials revoked |
|---|---|---|---|
| _Example: staging_ | _YYYY-MM-DDThh:mm:ssZ_ | _deployment/change ID_ | _JWT, gateway identity, DB, RabbitMQ, MinIO, Grafana_ |
