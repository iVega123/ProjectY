# ADR 0004 — Choose the protocol, not the vendor

- **Status:** Accepted
- **Date:** 2026-08-29

## Context

The architecture has to satisfy three demands at once: run on a laptop for free,
stand up on AWS with `terraform apply`, and stay portable to another cloud
later. Treated as three designs, they age at different rates and two of them rot.

## Decision

**The managed AWS services this architecture uses *are* the open engines it
already ran.** MSK is Kafka. ElastiCache is Valkey. Aurora speaks Postgres.
Keyspaces speaks CQL. Amazon MQ is RabbitMQ. EKS is Kubernetes.

One choice buys all three properties:

| Capability | Protocol | Local | AWS | Elsewhere |
|---|---|---|---|---|
| Transactional core | Postgres | container | Aurora Serverless v2 | Cloud SQL, Azure DB, Neon |
| Events | Kafka | apache/kafka | MSK | Confluent, Event Hubs |
| Commands | AMQP 0-9-1 | rabbitmq | Amazon MQ | any RabbitMQ |
| Coordination | RESP | valkey | ElastiCache | Memorystore, Azure Cache |
| Time series | CQL | cassandra | Keyspaces | Astra |
| Object storage | S3 API | LocalStack | S3 | GCS, R2, MinIO |
| Orchestration | Kubernetes API | kind | EKS | GKE, AKS |
| Telemetry | OTLP | collector → LGTM | ADOT → AMP | anything accepting OTLP |

The local replica is faithful because the container is not an imitation of the
managed service — it is the same engine. `terraform apply` works because nothing
in the code changes between substrates; the connection string does. And a future
migration is a Terraform module, not a rewrite.

**Cloud specifics are confined to two seams:** Terraform modules that expose
*capabilities* rather than resources, and the configuration and secret loading
path. Everything else is ignorant of where it runs.

## Alternatives considered

- **Aurora DSQL** — the closest philosophical relative of a distributed SQL
  store. Rejected: it has no foreign keys, and the schema uses one. Referential
  integrity would move into the application, which is exactly where it tends to
  fail.
- **DocumentDB** — the obvious path from MongoDB. Rejected: partial and
  perpetually lagging compatibility. The better answer is removing MongoDB
  entirely.
- **A full LocalStack environment.** The services this architecture leans on
  most — RDS, MSK, ElastiCache, EKS — sit behind the paid tier. A repository
  that only starts with a paid licence defeats its own purpose, so the local
  environment is deliberately hybrid: LocalStack for S3, Secrets Manager and KMS;
  real containers for the data engines; kind for EKS.

## What was explicitly rejected

- **DynamoDB, SQS/SNS, Cognito.** Cheaper and better integrated, and each is a
  one-way door: no protocol equivalent elsewhere, and the semantics do not
  translate. Refusing them is the price of the thesis.
- **A `CloudProvider` abstraction in application code.** The classic trap: a
  daily tax across six languages in exchange for a migration that may never
  happen. Portability comes from the choice of protocol and from the two seams,
  not from an interface.

## Consequences

- **Portable by design, not exercised.** Network, IAM, admission policy and
  observability remain cloud-specific, and `modules/gcp/` is only real when
  someone writes and applies it. The honest claim impresses more than false
  parity.
- **Managed is not identical.** Keyspaces speaks CQL but does not expose
  compaction strategy, so the local `TimeWindowCompactionStrategy` has no
  counterpart. The replica is faithful in protocol and in application behaviour;
  it is not faithful in operations.
- The workloads layer must never reference a cloud resource identifier. The day
  an ARN leaks into it, portability has died silently.

## Follow-up

- [Epic 11 — AWS, Terraform and cost profiles](https://github.com/iVega123/ProjectY/issues/12)
- [envs/local against LocalStack and kind](https://github.com/iVega123/ProjectY/issues/85)
