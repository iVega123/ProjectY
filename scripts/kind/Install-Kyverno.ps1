[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$version = 'v1.19.0'
$manifest = "https://github.com/kyverno/kyverno/releases/download/$version/install.yaml"

& $kubectl apply --server-side --force-conflicts -f $manifest | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not install Kyverno $version." }
& $kubectl wait --namespace kyverno --for=condition=Available deployment/kyverno-admission-controller --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'Kyverno admission controller did not become available.' }

$auditPolicy = Join-Path $root 'deploy\platform\kyverno\policy.yaml'
$auditApplied = $false
for ($attempt = 1; $attempt -le 12; $attempt++) {
    & $kubectl apply -f $auditPolicy | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $auditApplied = $true
        break
    }
    if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
}
if (-not $auditApplied) { throw 'Could not apply the image policy in audit mode.' }
& $kubectl wait --for=jsonpath='{.status.conditionStatus.ready}'=true imagevalidatingpolicy/verify-projecty-images --timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'The audit image policy did not become ready.' }

$enforcePolicy = Join-Path $root 'deploy\platform\kyverno'
$enforceApplied = $false
for ($attempt = 1; $attempt -le 12; $attempt++) {
    & $kubectl apply -k $enforcePolicy | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $enforceApplied = $true
        break
    }
    if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
}
if (-not $enforceApplied) { throw 'Could not promote the image policy to enforce mode.' }
& $kubectl wait --for=jsonpath='{.status.conditionStatus.ready}'=true imagevalidatingpolicy/verify-projecty-images --timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'The enforced image policy did not become ready.' }

Write-Host "Kyverno $version ready; ProjectY image verification promoted Audit -> Enforce."
