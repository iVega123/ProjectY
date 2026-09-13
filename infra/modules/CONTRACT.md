# Portable capability module contract

Every data-plane module exposes a `connection` object with the same public
shape:

```hcl
output "connection" {
  value = {
    endpoint         = "protocol endpoint without credentials"
    port             = 443
    secret_reference = "name resolved by the environment secret provider"
  }
}
```

`endpoint` never embeds a username, password, token or cloud resource ID.
`secret_reference` is a logical secret name, not an ARN. It is `null` when the
protocol authenticates with workload identity. Additional capability metadata
may be exposed in a named object (`cluster`, `bucket`, `database`), but consumers
must not inspect provider resource objects.

The required capability names are:

| Directory | Capability | Portable protocol |
|---|---|---|
| `transactional_store` | relational transactions | PostgreSQL wire |
| `event_bus` | durable event stream | Kafka |
| `command_bus` | command queues | AMQP 0-9-1 |
| `cache` | coordination and rate limiting | RESP |
| `time_series_store` | time-series facts | CQL |
| `object_store` | documents and media | S3 API |
| `kubernetes_cluster` | orchestration | Kubernetes API |

A future `modules/gcp` or `modules/local` implementation satisfies this file,
not the internals of `modules/aws`. Swapping an implementation therefore changes
composition only; `30-workloads` continues to consume the same object shape.
