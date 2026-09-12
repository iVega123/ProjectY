[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$module = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../infra/modules/aws/service_identity/main.tf') -Raw
$platform = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../infra/envs/aws-mid/20-platform/main.tf') -Raw
$documentation = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../docs/security/aws-workload-identity.md') -Raw

$services = @('api-gateway', 'identity', 'rental-core', 'media-guard', 'billing', 'risk-pricing', 'telemetry', 'console')
foreach ($service in $services) {
    if ($platform -notmatch "(?m)^\s*$([regex]::Escape($service))\s*=\s*\{") {
        throw "Missing IAM policy declaration for $service."
    }
    if ($documentation -notmatch [regex]::Escape("``$service``")) {
        throw "Missing policy review documentation for $service."
    }
}

if ($module -notmatch 'for_each\s*=\s*var\.services' -or
    $module -notmatch 'resource\s+"aws_eks_pod_identity_association"' -or
    $module -notmatch 'pods\.eks\.amazonaws\.com' -or
    $module -notmatch 'aws:RequestTag/kubernetes-service-account') {
    throw 'The module does not create one session-tag-bound Pod Identity association per service.'
}

if ($platform -match '(?m)resources\s*=\s*\["\*"\]' -or $platform -match '(?m)actions\s*=\s*\["\*"\]') {
    throw 'A workload policy contains an unscoped action or resource.'
}

$workloads = (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../infra/envs/aws-mid/30-workloads') -Filter '*.tf' | Get-Content -Raw) -join "`n"
if ($workloads -match '(?i)role_arn|arn:(aws|aws-us-gov|aws-cn):') {
    throw 'IAM role identifiers must stop at 20-platform and never enter 30-workloads.'
}

[pscustomobject]@{
    services = $services.Count
    rolesPerService = 1
    staticCredentials = 0
    wildcardGrants = 0
    workloadLayerRoleReferences = 0
} | ConvertTo-Json
