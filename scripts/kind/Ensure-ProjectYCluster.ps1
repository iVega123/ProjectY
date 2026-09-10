[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$kind = $tools.Kind
$kubectl = $tools.Kubectl
$clusterName = 'projecty'
$context = "kind-$clusterName"
$registryName = 'projecty-registry'
$registryPort = 5001

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required for the local kind cluster. Start Docker Desktop and retry `tilt up`.'
}

& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but its engine is not reachable.' }

$registryExists = (& docker inspect -f '{{.State.Running}}' $registryName 2>$null) -eq 'true'
if (-not $registryExists) {
    & docker rm -f $registryName 2>$null | Out-Null
    & docker run --detach --restart=always --name $registryName --publish "127.0.0.1:${registryPort}:5000" registry:3.0.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the ProjectY local registry.' }
}

$clusters = @(& $kind get clusters 2>$null)
if ($clusters -notcontains $clusterName) {
    & $kind create cluster --name $clusterName --config (Join-Path $root 'deploy\kind\cluster.yaml')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the ProjectY kind cluster.' }
}

$network = & docker inspect -f '{{json .NetworkSettings.Networks.kind}}' $registryName 2>$null
if (-not $network -or $network -eq '<no value>') {
    & docker network connect kind $registryName
    if ($LASTEXITCODE -ne 0) { throw 'Could not attach the local registry to the kind network.' }
}

& $kubectl config use-context $context | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not select kubectl context $context." }

$calicoVersion = 'v3.32.2'
$calicoManifest = "https://raw.githubusercontent.com/projectcalico/calico/$calicoVersion/manifests/calico.yaml"
& $kubectl apply --server-side -f $calicoManifest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not install Calico $calicoVersion." }
& $kubectl rollout status --namespace kube-system daemonset/calico-node --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'Calico nodes did not become ready.' }
& $kubectl rollout status --namespace kube-system deployment/calico-kube-controllers --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'Calico controllers did not become ready.' }

$registryDiscovery = @"
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: "localhost:$registryPort"
    help: "https://kind.sigs.k8s.io/docs/user/local-registry/"
"@
$registryDiscovery | & $kubectl apply -f - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not publish local registry discovery.' }

$ingressVersion = 'controller-v1.15.1'
$ingressManifest = "https://raw.githubusercontent.com/kubernetes/ingress-nginx/$ingressVersion/deploy/static/provider/kind/deploy.yaml"
& $kubectl apply --server-side -f $ingressManifest | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not install ingress-nginx.' }
& $kubectl wait --namespace ingress-nginx --for=condition=Available deployment/ingress-nginx-controller --timeout=180s
if ($LASTEXITCODE -ne 0) { throw 'ingress-nginx did not become available.' }

Write-Host "ProjectY cluster ready: $context (Calico $calicoVersion, registry localhost:$registryPort, ingress http://localhost:8080)"
