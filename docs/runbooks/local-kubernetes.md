# Local Kubernetes runbook

`tilt up` is ProjectY's default development entrypoint. It creates a disposable
single-node kind cluster named `projecty`, connects a local OCI registry, installs
Calico and ingress-nginx, then deploys the `selfhost` Kustomize overlay. No global
kind, kubectl or Helm installation is required: verified pinned binaries are kept
under the ignored `.tools/kubernetes` directory.

## Prerequisites and capacity

- Docker Engine or Docker Desktop with Linux containers
- Tilt
- Windows PowerShell 5.1+ or PowerShell 7+
- Free host ports `5001`, `8080`, `8443` and `10350`
- Recommended Docker allocation: 8 CPU cores, 16 GiB RAM and 30 GiB free disk

The capacity is an operating recommendation for the complete polyglot topology,
not a benchmark guarantee. The first start downloads the Kubernetes node,
platform components and workload images and is therefore network-dependent.

## Start and stop

Generate ignored local credentials and start the environment:

```powershell
./scripts/New-LocalSecrets.ps1
tilt up
```

Open the Tilt UI at <http://localhost:10350>, the console at
<http://localhost:8080>, or the gateway readiness endpoint at
<http://localhost:8080/health/ready>.

Stop and delete the cluster, registry and local cluster data with:

```bash
tilt down
```

For the temporary Compose migration path, run
`tilt up -- --orchestrator=compose --full`.

## Security boundaries

The `projecty` namespace enforces the `restricted` Pod Security Standard. Every
workload has its own ServiceAccount, does not mount its API token by default,
runs without privilege escalation and drops all Linux capabilities. Calico
enforces default-deny ingress and egress; explicit policies grant each service
only its owned dependencies. Only edge-labeled workloads accept ingress traffic.

The local External Secrets provider reads one generated source Secret from the
separate `projecty-secrets` namespace through a resource-name-scoped Role. Nine
target Secrets are materialized for the workloads that need them. The committed
manifests contain references and property names, never secret values. The AWS
overlay keeps the same ExternalSecret resources and replaces only the SecretStore
provider with AWS Secrets Manager and workload-identity authentication.

Kyverno first receives the ProjectY image-verification policy in Audit mode and
is then promoted from the same manifest to Enforce. The policy trusts only the
keyless certificate identity of `.github/workflows/ci.yml` on `main` for ProjectY
release images. Local Tilt images use the isolated local registry; the acceptance
probe separately proves rejection of a deliberately unsigned registry image and
admission of a pipeline-signed ProjectY image.

## Reproduce the evidence

Static checks need no running cluster:

```powershell
./scripts/Test-KubernetesManifests.ps1
```

With `tilt up` running, reproduce the three live acceptance checks:

```powershell
./scripts/Test-ExternalSecrets.ps1
./scripts/Test-KubernetesSecurity.ps1
./scripts/Test-SignedAdmission.ps1
```

The security test creates temporary listener pods to prove that an identity-labeled
pod can reach its owned CockroachDB endpoint but cannot connect to Rental Core's
RabbitMQ endpoint. It also submits an otherwise restricted pod with UID 0 and
requires admission to reject it. The signed-admission test uses server dry-run,
so neither canary workload is persisted. CI executes the same sequence in an
ephemeral kind cluster and uploads the JSON output as `kubernetes-security-<sha>`.

## Troubleshooting

- If Docker is not reachable, start its engine before retrying `tilt up`.
- If a pinned cluster or CNI setting changes, run `tilt down` before recreating it.
- If exactly one of `.env` and `.rabbitmq-definitions.json` exists, regenerate the
  credential set intentionally as described in [getting started](../getting-started.md).
- `tilt down` is destructive only to the disposable `projecty` kind cluster and
  `projecty-registry`; persistent local Kubernetes data is not retained.
