[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$helm = $tools.Helm
$kubectl = $tools.Kubectl
$chartVersion = 'v1.21.1'

& $helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager --version $chartVersion --namespace cert-manager --create-namespace --set crds.enabled=true --wait --timeout 5m
if ($LASTEXITCODE -ne 0) { throw "Could not install cert-manager $chartVersion." }

foreach ($deployment in @('cert-manager', 'cert-manager-webhook', 'cert-manager-cainjector')) {
    & $kubectl wait --namespace cert-manager --for=condition=Available "deployment/$deployment" --timeout=240s
    if ($LASTEXITCODE -ne 0) { throw "$deployment did not become available." }
}

# The webhook reports Available before it serves; applying an Issuer against a
# webhook that is not yet answering fails with a connection refused that says
# nothing about the manifest. Retry instead of failing the first start.
$authority = Join-Path $root 'deploy\platform\cert-manager'
$applied = $false
for ($attempt = 1; $attempt -le 12; $attempt++) {
    & $kubectl apply -k $authority | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $applied = $true
        break
    }
    if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
}
if (-not $applied) { throw 'Could not apply the ProjectY local certificate authority.' }

& $kubectl wait --namespace cert-manager --for=condition=Ready certificate/projecty-local-ca --timeout=180s
if ($LASTEXITCODE -ne 0) { throw 'The local certificate authority did not become ready.' }
& $kubectl wait --namespace projecty --for=condition=Ready certificate/projecty-tls --timeout=180s
if ($LASTEXITCODE -ne 0) { throw 'The ingress serving certificate did not become ready.' }

Write-Host "cert-manager $chartVersion ready; projecty-tls issued by the local authority."
