# AWS capability implementations

These modules deliberately name capabilities, not products. AWS product choices
are implementation details and may change without changing a consumer:

| Capability module | Current implementation |
|---|---|
| `network` | VPC, private/public subnets and egress |
| `transactional_store` | RDS PostgreSQL-compatible fallback |
| `event_bus` | Amazon MSK provisioned |
| `command_bus` | Amazon MQ for RabbitMQ |
| `cache` | ElastiCache for Valkey |
| `time_series_store` | Amazon Keyspaces |
| `object_store` | Amazon S3 |
| `kubernetes_cluster` | Amazon EKS |

The cost profiles select only the modules needed for their recovery promise.
The managed transactional profiles use CockroachDB Cloud through its official
provider, because ADR 0004 requires the local and managed engines to be the same.
The RDS implementation remains the contract-compatible, AWS-only alternative.
