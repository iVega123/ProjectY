# aws-mid layered state

This is the default ephemeral AWS profile: a three-AZ EKS node group, Strimzi
Kafka on that existing capacity, CockroachDB Cloud `STANDARD` in one region,
multi-AZ RabbitMQ, and a Valkey replica. It promises an RTO measured in minutes,
an RPO of approximately five minutes, and survival of one Availability Zone.
The CockroachDB invoice and identity boundary belong to a second vendor; see
[`docs/cost-profiles.md`](../../../docs/cost-profiles.md) for the dated
**~$1,143/month** fixed estimate and its assumptions.

| Promise | Value |
|---|---|
| RTO | minutes |
| RPO | approximately five minutes |
| Survives | loss of one Availability Zone |

Apply in numeric order. Each directory is an independent Terraform root and
state file; it reads only the immediately preceding state. Supply the same
backend bucket to `init` and as `TF_VAR_state_bucket` to upper layers.

```powershell
$env:TF_VAR_state_bucket = '<state-bucket>'
$layers = '00-network', '10-data', '20-platform', '30-workloads'
foreach ($layer in $layers) {
  terraform -chdir="infra/envs/aws-mid/$layer" init `
    -backend-config="bucket=$env:TF_VAR_state_bucket" `
    -backend-config='region=us-east-1'
  terraform -chdir="infra/envs/aws-mid/$layer" apply
}
```

`terraform destroy` in `10-data` is intentionally refused by its
`prevent_destroy` sentinel. The controlled ephemeral teardown targets declared
data capabilities while retaining that zero-cost sentinel in state; the
acceptance runbook owns that explicit list.
