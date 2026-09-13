# Local Terraform environment

This root creates the complete local infrastructure without an AWS account:

- LocalStack free image for S3, Secrets Manager and KMS;
- upstream CockroachDB, Kafka, RabbitMQ, Valkey and Cassandra containers;
- a real Kubernetes cluster created with kind;
- `projecty-cloud-runtime` in the `projecty` namespace with endpoints for the
  same AWS SDK and protocol clients used by cloud workloads.

Docker Desktop (or a compatible Docker Engine exposing the Docker API) must be
running. From the repository root:

```powershell
terraform -chdir=infra/envs/local init
terraform -chdir=infra/envs/local apply
terraform -chdir=infra/envs/local destroy
```

The `localstack` access and secret keys are deliberately non-production request
signing values. They grant no access outside the workstation. Terraform state
contains the disposable cache and RabbitMQ bootstrap credentials, so the local
state must remain uncommitted.

See `docs/runbooks/local-terraform.md` for the fidelity boundary and runtime
connection model.
