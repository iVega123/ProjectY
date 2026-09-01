# ADR 0004 — Choose the protocol, not the vendor

- **Status:** Accepted
- **Date:** 2026-08-29
- **Amended:** 2026-09-01 — the transactional core moves from Aurora PostgreSQL
  to CockroachDB Cloud, so that every row of the table below makes the same
  claim. See *Alternatives considered*.

## Context

The architecture has to satisfy three demands at once: run on a laptop for free,
stand up on AWS with `terraform apply`, and stay portable to another cloud
later. Treated as three designs, they age at different rates and two of them rot.

## Decision

**The managed services this architecture uses *are* the open engines it already
ran.** MSK is Kafka. ElastiCache is Valkey. Keyspaces speaks CQL. Amazon MQ is
RabbitMQ. EKS is Kubernetes. CockroachDB Cloud is CockroachDB.

One choice buys all three properties:

| Capability | Protocol | Local | AWS | Elsewhere |
|---|---|---|---|---|
| Transactional core | Postgres wire | cockroachdb | CockroachDB Cloud, deployed into AWS | same, on GCP or Azure; or self-hosted |
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

- **Aurora PostgreSQL Serverless v2** — the first-party AWS answer, and what the
  first version of this record specified. Rejected because it broke the thesis on
  the row where the thesis matters most: the container would be PostgreSQL and
  the managed service would be Aurora — two different engines that agree on a
  protocol. That is a weaker claim than every other row makes, and the weakest
  row is the one a reader tests first. Aurora stays the right answer for anyone
  who needs the store inside AWS's own billing and IAM boundary, and the schema
  is written so that swapping to it costs a connection string.
- **Aurora DSQL** — the closest philosophical relative of a distributed SQL
  store. Rejected: it has no foreign keys, and the schema uses one. Referential
  integrity would move into the application, which is exactly where it tends to
  fail.
- **DocumentDB** — the obvious path from MongoDB. Rejected: partial and
  perpetually lagging compatibility. The better answer is removing MongoDB
  entirely.
- **A full LocalStack environment.** The services this architecture leans on
  most — MSK, ElastiCache, EKS — sit behind the paid tier. A repository that only
  starts with a paid licence defeats its own purpose, so the local environment is
  deliberately hybrid: LocalStack for S3, Secrets Manager and KMS; real
  containers for the data engines; kind for EKS.

## What was explicitly rejected

- **DynamoDB, SQS/SNS, Cognito.** Cheaper and better integrated, and each is a
  one-way door: no protocol equivalent elsewhere, and the semantics do not
  translate. Refusing them is the price of the thesis.
- **A `CloudProvider` abstraction in application code.** The classic trap: a
  daily tax across six languages in exchange for a migration that may never
  happen. Portability comes from the choice of protocol and from the two seams,
  not from an interface.

## Consequences

- **One managed service is not AWS's, and that is the deliberate part.**
  CockroachDB Cloud runs on AWS infrastructure and reaches the VPC over
  PrivateLink, but it is a second vendor: its own account, its own billing, its
  own identity model. What it buys is the only version of this record's central
  sentence that survives contact with the transactional core — the container and
  the managed cluster are the same engine. An architecture that says "AWS" and
  means it on every row would take Aurora, and would have to say something
  weaker.
- **`terraform apply` still covers the store.** CockroachDB publishes an official
  Terraform provider, so the cluster is declared in the same run as the VPC and
  the EKS cluster. The transactional core does not become a hand-clicked
  exception to the infrastructure-as-code rule — which it would have been if the
  answer were "create it in the console".
- **The dialect is the escape hatch, and nothing defends it automatically.**
  Local and managed are now the same engine, so `STRING` and
  `CREATE USER IF NOT EXISTS` would work on both and never fail — and the last
  column of the table would quietly stop being true. The schema is therefore
  written in the subset PostgreSQL also accepts, with the engine-specific
  bootstrap (database and role creation) split into its own file, and CI applies
  it to both engines whenever the schema changes. That check does not protect a
  deployment;
  it protects an option that would otherwise rot unnoticed.
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
