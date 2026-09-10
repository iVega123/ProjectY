[CmdletBinding()]
param(
    [string]$SignedImage = 'ghcr.io/ivega123/projecty/api-gateway:latest'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$policy = Join-Path $root 'deploy\platform\kyverno\unsigned-canary-policy.yaml'

function Test-ImageAdmission {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Image
    )

    $manifest = @{
        apiVersion = 'v1'
        kind = 'Pod'
        metadata = @{ name = $Name; namespace = 'projecty' }
        spec = @{
            automountServiceAccountToken = $false
            restartPolicy = 'Never'
            securityContext = @{
                runAsNonRoot = $true
                seccompProfile = @{ type = 'RuntimeDefault' }
            }
            containers = @(
                @{
                    name = $Name
                    image = $Image
                    securityContext = @{
                        allowPrivilegeEscalation = $false
                        capabilities = @{ drop = @('ALL') }
                        runAsNonRoot = $true
                    }
                }
            )
        }
    } | ConvertTo-Json -Depth 10

    $output = $manifest | & $kubectl create --dry-run=server -f - -o name 2>&1
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
}

& $kubectl apply -f $policy | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not install the unsigned-image canary policy.' }

try {
    & $kubectl wait --for=jsonpath='{.status.conditionStatus.ready}'=true imagevalidatingpolicy/projecty-unsigned-canary --timeout=90s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The unsigned-image canary policy did not become ready.' }

    $rejection = Test-ImageAdmission -Name projecty-unsigned-canary -Image ghcr.io/kyverno/test-verify-image:unsigned
    if ($rejection.ExitCode -eq 0) { throw 'Kyverno admitted the deliberately unsigned image.' }
    if (($rejection.Output -join "`n") -notmatch 'signature|verify|imagevalidatingpolicy|must be signed') {
        throw "The canary failed for an unrelated reason: $($rejection.Output -join ' ')"
    }

    $signed = Test-ImageAdmission -Name projecty-signed-canary -Image $SignedImage
    if ($signed.ExitCode -ne 0) { throw "Kyverno refused the signed pipeline image: $($signed.Output -join ' ')" }

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
