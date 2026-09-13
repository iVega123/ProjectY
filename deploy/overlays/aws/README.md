# AWS Mid overlay

This overlay is the `aws-mid` workload composition. It installs a three-broker
Kafka cluster through the Strimzi operator, replaces the base Kafka container
with a stable bootstrap-service alias, reads runtime credentials from AWS
Secrets Manager, and selects the production replica shape. Canonical service
contracts and dependency topology remain under `deploy/base/`.

`infra/envs/aws-mid/30-workloads` owns the Strimzi Helm release before applying
this Kustomize overlay. The deliberately cheaper single-container composition
lives in `deploy/overlays/aws-low`; managed MSK is declared only in the
review-only `infra/envs/aws-high` root.
