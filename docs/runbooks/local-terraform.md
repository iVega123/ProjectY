# Local Terraform fidelity boundary

The local profile is a development substitute, not a miniature AWS region. It
keeps client protocols and the Terraform capability contracts stable while
making the control-plane differences explicit.

## What is real and what is emulated

| Capability | Local implementation | Fidelity | Deliberate gap |
|---|---|---|---|
| Transactional store | CockroachDB single-node container | Real PostgreSQL-wire database engine | No managed backups, regional replicas or Cockroach Cloud control plane |
| Event bus | Apache Kafka single-broker KRaft container | Real Kafka broker and client protocol | No MSK brokers, multi-AZ quorum or IAM authentication |
| Command bus | RabbitMQ container | Real AMQP broker | No Amazon MQ maintenance, failover or network boundary |
| Cache | Valkey container | Real RESP engine with authentication | No ElastiCache replication, TLS termination or automatic failover |
| Time-series store | Cassandra container | Real CQL engine | No Keyspaces capacity, IAM integration or regional service behavior |
| Object storage | S3 through the AWS provider and AWS SDK against LocalStack | Emulated AWS API with real SigV4 requests | AWS durability, regional behavior and service quotas are not emulated |
| Secrets | Secrets Manager through the AWS provider and AWS SDK against LocalStack | Emulated AWS API with real SigV4 requests | Managed rotation and account policies are not emulated |
| Encryption | KMS through the AWS provider against LocalStack | Emulated AWS API surface | HSM custody, grants and account isolation are not emulated |
| Kubernetes | kind using the pinned ProjectY node image | Real Kubernetes API and kubelet behavior | EKS control plane, VPC CNI and EKS Pod Identity are not emulated |

EKS Pod Identity is intentionally tested as Terraform policy in the AWS profile.
Giving local pods fake IAM semantics would hide the exact boundary this profile
is meant to expose.

## Start and stop

Docker must be running and ports `4566`, `5672`, `6379`, `8080`, `8443`,
`9042`, `9092`, `15672`, `18080`, `26257`, and `29092` must be free.

```powershell
terraform -chdir=infra/envs/local init
terraform -chdir=infra/envs/local apply
terraform -chdir=infra/envs/local output connections
```

The apply creates named Docker volumes. Destroy removes the containers, volumes,
kind cluster and LocalStack resources:

```powershell
terraform -chdir=infra/envs/local destroy
```

Do not run this profile beside the legacy Compose or script-created kind stack;
they intentionally use some of the same developer-facing ports.

## Application connection model

Containers on the capability bridge use the portable `connections` output and
resolve capability container names directly. Pods in kind reach the published
ports through `host.docker.internal`; Terraform records those endpoints in the
`projecty-cloud-runtime` ConfigMap.

The runtime map supplies `AWS_ENDPOINT_URL_S3`,
`AWS_ENDPOINT_URL_SECRETS_MANAGER`, `AWS_ENDPOINT_URL_KMS`, `S3_ENDPOINT`, and
`S3_BUCKET`. Application code therefore continues to use the AWS SDK for object
storage and secret lookup. Only the endpoint changes between local and AWS
profiles. The two LocalStack signing values are disposable local credentials,
not AWS account credentials.

Docker Engine installations that do not provide `host.docker.internal` must add
the host-gateway alias to kind or substitute the host gateway address in the
ConfigMap. That networking shim is local plumbing and is not part of a portable
capability contract.
