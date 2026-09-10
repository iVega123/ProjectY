[CmdletBinding()]
param(
    [string]$SignedImage = 'ghcr.io/ivega123/projecty/api-gateway:latest'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$policy = Join-Path $root 'deploy\platform\kyverno\unsigned-canary-policy.yaml'

& $kubectl apply -f $policy | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not install the unsigned-image canary policy.' }

try {
    & $kubectl wait --namespace projecty --for=jsonpath='{.status.ready}'=true namespacedimagevalidatingpolicy/projecty-unsigned-canary --timeout=90s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The unsigned-image canary policy did not become ready.' }

    $rejection = & $kubectl run projecty-unsigned-canary --namespace projecty --image ghcr.io/kyverno/test-verify-image:unsigned --restart Never --dry-run=server -o yaml 2>&1
    if ($LASTEXITCODE -eq 0) { throw 'Kyverno admitted the deliberately unsigned image.' }
    if (($rejection -join "`n") -notmatch 'projecty-unsigned-canary|signature|verify') {
        throw "The canary failed for an unrelated reason: $($rejection -join ' ')"
    }

    $signed = & $kubectl run projecty-signed-canary --namespace projecty --image $SignedImage --restart Never --dry-run=server -o name 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Kyverno refused the signed pipeline image: $($signed -join ' ')" }

    [pscustomobject]@{
        observedAt = [DateTimeOffset]::UtcNow.ToString('o')
        unsignedImage = 'ghcr.io/kyverno/test-verify-image:unsigned'
        unsignedResult = 'rejected'
        signedImage = $SignedImage
        signedResult = 'admitted'
        policy = 'verify-projecty-images'
    } | ConvertTo-Json
} finally {
    & $kubectl delete -f $policy --ignore-not-found | Out-Null
}
