# aws-low: disposable single-node data plane

This profile runs the data plane from `deploy/base`: one CockroachDB container,
one Apache Kafka container, and one copy of every other stateful capability on a
single `m7g.large` EKS node. `deploy/overlays/aws-low` adds the daily logical dump
job. The dump is the recovery boundary; the node and its volumes are disposable.

| Promise | Value |
|---|---|
| RTO | hours |
| RPO | 24 hours |
| Survives | nothing; a node or Availability Zone loss requires restore |

The root creates one EKS control plane, one on-demand node, one NAT gateway, and
the versioned S3 dump bucket. See `docs/cost-profiles.md` for the dated estimate,
assumptions, source links, and omitted usage-based charges.

```powershell
terraform -chdir=infra/envs/aws-low init
terraform -chdir=infra/envs/aws-low plan
terraform -chdir=infra/envs/aws-low apply
kubectl apply -k deploy/overlays/aws-low
```
