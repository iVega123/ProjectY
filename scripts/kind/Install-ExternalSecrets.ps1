[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$helm = $tools.Helm
$kubectl = $tools.Kubectl
$chartVersion = '2.10.0'

& $helm upgrade --install external-secrets oci://ghcr.io/external-secrets/charts/external-secrets --version $chartVersion --namespace external-secrets --create-namespace --set installCRDs=true --wait --timeout 5m
if ($LASTEXITCODE -ne 0) { throw "Could not install External Secrets $chartVersion." }

& $kubectl wait --namespace external-secrets --for=condition=Available deployment/external-secrets --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'External Secrets did not become available.' }
Write-Host "External Secrets $chartVersion ready."
