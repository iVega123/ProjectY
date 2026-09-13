# aws-high: review-only regional survival

**Never apply this profile.** It exists to make the multi-region topology and
its financial consequence reviewable. The deployment workflow intentionally
has no `aws-high` apply job or environment credentials.

The root declares two complete three-AZ EKS/MSK regions, asynchronous MSK
Replicator failover, and CockroachDB Cloud `ADVANCED` with three nodes in each
of three regions. CockroachDB remains synchronously multi-region; Kafka's
cross-region copy is asynchronous, so the `~0` RPO assumes replication lag is
inside the alert threshold when traffic is switched.

| Promise | Value |
|---|---|
| RTO | seconds, with an automated traffic switch and a rehearsed runbook |
| RPO | approximately zero; bounded by current Kafka replication lag |
| Survives | loss of one AWS region |

Only validation and mocked plans are supported:

```powershell
terraform -chdir=infra/envs/aws-high init -backend=false
terraform -chdir=infra/envs/aws-high validate
terraform -chdir=infra/envs/aws-high test
```

See `docs/cost-profiles.md` before reviewing any capacity change.
