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

& $kubectl apply -f (Join-Path $root 'deploy\platform\kyverno\policy.yaml') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not apply the image policy in audit mode.' }
& $kubectl wait --for=condition=Ready clusterpolicy/verify-projecty-images --timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'The audit image policy did not become ready.' }

& $kubectl apply -k (Join-Path $root 'deploy\platform\kyverno') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not promote the image policy to enforce mode.' }
& $kubectl wait --for=condition=Ready clusterpolicy/verify-projecty-images --timeout=90s
if ($LASTEXITCODE -ne 0) { throw 'The enforced image policy did not become ready.' }

Write-Host "Kyverno $version ready; ProjectY image verification promoted Audit -> Enforce."
