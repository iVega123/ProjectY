[CmdletBinding()]
param(
    [string]$Environment = 'aws-mid'
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot "../infra/envs/$Environment"
$layers = @('00-network', '10-data', '20-platform', '30-workloads')

foreach ($layer in $layers) {
    $path = Join-Path $root $layer
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Missing Terraform state layer: $path"
    }

    $backend = Get-Content -LiteralPath (Join-Path $path 'backend.tf') -Raw
    if ($backend -notmatch 'backend\s+"s3"' -or $backend -notmatch 'use_lockfile\s*=\s*true') {
        throw "$layer must use the S3 backend with native lockfile locking."
    }
}

$expectedPrevious = @{
    '10-data'     = '00-network'
    '20-platform' = '10-data'
    '30-workloads' = '20-platform'
}

foreach ($layer in $expectedPrevious.Keys) {
    $files = Get-ChildItem -LiteralPath (Join-Path $root $layer) -Filter '*.tf'
    $configuration = ($files | Get-Content -Raw) -join "`n"
    $remoteStates = [regex]::Matches($configuration, 'data\s+"terraform_remote_state"').Count
    if ($remoteStates -ne 1) {
        throw "$layer must read exactly one remote state; found $remoteStates."
    }

    $expectedKey = "$Environment/$($expectedPrevious[$layer])/terraform.tfstate"
    if ($configuration -notmatch [regex]::Escape($expectedKey)) {
        throw "$layer must read only its immediately preceding state ($expectedKey)."
    }
}

$dataConfiguration = (Get-ChildItem -LiteralPath (Join-Path $root '10-data') -Filter '*.tf' | Get-Content -Raw) -join "`n"
if ($dataConfiguration -notmatch 'prevent_destroy\s*=\s*true') {
    throw '10-data must refuse an unqualified destroy.'
}

$workloadConfiguration = (Get-ChildItem -LiteralPath (Join-Path $root '30-workloads') -Filter '*.tf' | Get-Content -Raw) -join "`n"
if ($workloadConfiguration -match '(?i)arn:(aws|aws-us-gov|aws-cn):') {
    throw '30-workloads must not contain a cloud resource ARN.'
}

$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/terraform-deploy.yml') -Raw
if ($workflow -match '(?i)AWS_ACCESS_KEY_ID|AWS_SECRET_ACCESS_KEY') {
    throw 'Terraform deployment must use OIDC, not static AWS access keys.'
}
if ($workflow -notmatch 'id-token:\s*write' -or $workflow -notmatch 'configure-aws-credentials') {
    throw 'Terraform deployment is missing GitHub OIDC federation.'
}

[pscustomobject]@{
    environment = $Environment
    layers = $layers.Count
    remoteStateEdges = $expectedPrevious.Count
    nativeLocking = $true
    dataDestroyGuard = $true
    workloadArns = 0
    authentication = 'github-oidc'
} | ConvertTo-Json
