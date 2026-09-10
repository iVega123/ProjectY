# Local Kubernetes runbook

`tilt up` is ProjectY's default development entrypoint. It creates a disposable
single-node kind cluster named `projecty`, connects a local OCI registry, installs
Calico, ingress-nginx and cert-manager, then deploys the `selfhost` Kustomize
overlay. No global kind, kubectl or Helm installation is required: verified pinned
binaries are kept under the ignored `.tools/kubernetes` directory.

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
<https://localhost:8443>, or the gateway readiness endpoint at
<https://localhost:8443/health/ready>. Port `8080` answers every request with a
308 to `8443`.

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

## TLS

TLS terminates at the ingress and nowhere else. cert-manager creates a
self-signed root, issues the `projecty-local-ca` authority from it, and issues
the `projecty-tls` serving certificate for `localhost`, `projecty.localtest.me`
and `127.0.0.1` from that authority. The `Ingress` in `deploy/base` names the
Secret and knows nothing else about it — on AWS the same field is dropped and
ACM terminates in front of the load balancer.

The authority is local, so no public CA vouches for it and browsers warn on the
first visit. To verify the chain deliberately instead of clicking through:

```powershell
$tools = ./scripts/kind/Install-ProjectYKubernetesTools.ps1
& $tools.Kubectl get secret projecty-local-ca --namespace cert-manager -o jsonpath='{.data.ca\.crt}' |
  ForEach-Object { [System.Convert]::FromBase64String($_) } |
  Set-Content projecty-local-ca.crt -AsByteStream
curl --cacert projecty-local-ca.crt https://localhost:8443/health/ready
```

Between services the traffic is plain HTTP. That is a decision, not an
omission: every internal hop is authenticated above the transport by the signed
identity envelope, and the integrity gap that leaves — same-route body
substitution inside the 30 second window — is stated in
[ADR 0025](../adr/0025-tls-terminates-at-the-ingress.md) and tracked as
[#191](https://github.com/iVega123/ProjectY/issues/191). No service redirects to
HTTPS; `rental-core` reads `X-Forwarded-Proto` through `UseForwardedHeaders`
with a forward limit of two, because the ingress and the gateway are two hops.

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
./scripts/Test-IngressTls.ps1
```

The security test creates temporary listener pods to prove that an identity-labeled
pod can reach its owned CockroachDB endpoint but cannot connect to Rental Core's
RabbitMQ endpoint. It also submits an otherwise restricted pod with UID 0 and
requires admission to reject it. The signed-admission test uses server dry-run,
so neither canary workload is persisted. The TLS test verifies the served
certificate against the cluster's own authority with no `--insecure`, and then
requires an empty authority to be rejected — otherwise the first check would
prove only that something answered on 8443.

CI executes the same sequence in an ephemeral kind cluster and uploads the JSON
output as `kubernetes-security-<sha>`. One difference is worth naming: CI has no
application images in that cluster, so it runs the TLS test with
`-UseFixtureBackends`, standing two busybox listeners in for the console and the
gateway. CI therefore proves the edge — chain, SAN, redirect, refusal of an
untrusted client — and the claim that the real console and gateway answer over
TLS is what the switch-free run above verifies under `tilt up`.

## Troubleshooting

- If Docker is not reachable, start its engine before retrying `tilt up`.
- If a pinned cluster or CNI setting changes, run `tilt down` before recreating it.
- If exactly one of `.env` and `.rabbitmq-definitions.json` exists, regenerate the
  credential set intentionally as described in [getting started](../getting-started.md).
- `tilt down` is destructive only to the disposable `projecty` kind cluster and
  `projecty-registry`; persistent local Kubernetes data is not retained.
