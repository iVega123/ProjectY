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

# Probes that are allowed to fail cannot redirect a native command's stderr.
#
# On Windows PowerShell 5.1, `docker inspect ... 2>$null` wraps every stderr
# line in a NativeCommandError, and with $ErrorActionPreference = 'Stop' that
# terminates the script. So the first `tilt up` on a clean Windows machine --
# the platform the runbook names first -- died on "no such object:
# projecty-registry", which is the expected answer to "does the registry exist".
# CI never saw it, because CI runs pwsh 7 on Linux.
function Invoke-Probe {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [string[]]$Arguments = @()
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>&1
    } finally {
        $ErrorActionPreference = $previous
    }
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Lines = @($output | ForEach-Object { $_.ToString() })
    }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required for the local kind cluster. Start Docker Desktop and retry `tilt up`.'
}

& docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker is installed but its engine is not reachable.' }

$registryState = Invoke-Probe -FilePath 'docker' -Arguments @('inspect', '-f', '{{.State.Running}}', $registryName)
if ($registryState.ExitCode -ne 0 -or ($registryState.Lines -join '').Trim() -ne 'true') {
    Invoke-Probe -FilePath 'docker' -Arguments @('rm', '-f', $registryName) | Out-Null
    & docker run --detach --restart=always --name $registryName --publish "127.0.0.1:${registryPort}:5000" registry:3.0.0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the ProjectY local registry.' }
}

$clusters = Invoke-Probe -FilePath $kind -Arguments @('get', 'clusters')
if ($clusters.Lines -notcontains $clusterName) {
    & $kind create cluster --name $clusterName --config (Join-Path $root 'deploy\kind\cluster.yaml')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the ProjectY kind cluster.' }
}

$networkProbe = Invoke-Probe -FilePath 'docker' -Arguments @('inspect', '-f', '{{json .NetworkSettings.Networks.kind}}', $registryName)
$network = ($networkProbe.Lines -join '').Trim()
if ($networkProbe.ExitCode -ne 0 -or -not $network -or $network -eq '<no value>' -or $network -eq 'null') {
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
