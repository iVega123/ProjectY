[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Join-Path $PSScriptRoot '..'
$profiles = @('aws-low', 'aws-mid', 'aws-high')

foreach ($profile in $profiles) {
    $path = Join-Path $repository "infra/envs/$profile"
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Missing cost-profile environment: $profile"
    }

    $readme = Get-Content -LiteralPath (Join-Path $path 'README.md') -Raw
    foreach ($promise in @('RTO', 'RPO', 'Survives')) {
        if ($readme -notmatch $promise) {
            throw "$profile does not state $promise."
        }
    }
}

$comparison = Get-Content -LiteralPath (Join-Path $repository 'docs/cost-profiles.md') -Raw
foreach ($required in @(
    '2026-09-13',
    'aws-low',
    'aws-mid',
    'aws-high',
    'STANDARD',
    'ADVANCED',
    '$172.27',
    '$1,143.31',
    '$5,856.13',
    'second vendor',
    'official CockroachDB Terraform provider',
    'no longer literally true'
)) {
    if (-not $comparison.Contains($required)) {
        throw "Cost comparison is missing required evidence: $required"
    }
}

foreach ($source in @(
    'https://www.cockroachlabs.com/pricing/',
    'https://aws.amazon.com/eks/pricing/',
    'https://aws.amazon.com/vpc/pricing/',
    'https://aws.amazon.com/msk/pricing/',
    'https://aws.amazon.com/amazon-mq/pricing/'
)) {
    if (-not $comparison.Contains($source)) {
        throw "Cost comparison is missing pricing source: $source"
    }
}

$lowManifest = Get-Content -LiteralPath (Join-Path $repository 'deploy/overlays/aws-low/daily-cockroach-dump.yaml') -Raw
if ($lowManifest -notmatch 'kind:\s+CronJob' -or
    $lowManifest -notmatch 'schedule:\s+"0 2 \* \* \*"' -or
    $lowManifest -notmatch 'pg_dumpall' -or
    $lowManifest -notmatch 'cockroach/.+\.sql') {
    throw 'aws-low must declare the daily CockroachDB logical dump job.'
}

$mid = (Get-ChildItem -LiteralPath (Join-Path $repository 'infra/envs/aws-mid') -Recurse -Filter '*.tf' | Get-Content -Raw) -join "`n"
if ($mid -notmatch 'plan\s*=\s*"STANDARD"' -or $mid -notmatch 'strimzi-kafka-operator') {
    throw 'aws-mid must use CockroachDB Standard and Strimzi.'
}

$high = (Get-ChildItem -LiteralPath (Join-Path $repository 'infra/envs/aws-high') -Filter '*.tf' | Get-Content -Raw) -join "`n"
if ($high -notmatch 'plan\s*=\s*"ADVANCED"' -or
    $high -notmatch 'resource\s+"aws_msk_replicator"' -or
    $high -notmatch 'execution_policy\s*=\s*"review-only; never apply"') {
    throw 'aws-high must declare Advanced multi-region SQL, MSK replication, and the review-only policy.'
}

$deployment = Get-Content -LiteralPath (Join-Path $repository '.github/workflows/terraform-deploy.yml') -Raw
if ($deployment -match '(?m)^\s*name:\s*Apply aws-high' -or $deployment -match 'infra/envs/aws-high') {
    throw 'The deployment workflow must never plan or apply aws-high with paid credentials.'
}

[pscustomobject]@{
    profiles = $profiles.Count
    datedPricingSources = 5
    dailyDump = $true
    managedCockroachTiers = @('STANDARD', 'ADVANCED')
    highExecution = 'mock-plan-only'
} | ConvertTo-Json
