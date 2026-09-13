[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '../infra/envs/local'
$configuration = (Get-ChildItem -LiteralPath $root -Filter '*.tf' | Get-Content -Raw) -join "`n"
$moduleRoot = Join-Path $PSScriptRoot '../infra/modules/local'
$documentation = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../docs/runbooks/local-terraform.md') -Raw

$capabilities = @(
    'transactional_store',
    'event_bus',
    'command_bus',
    'cache',
    'time_series_store',
    'object_store',
    'kubernetes_cluster'
)

foreach ($capability in $capabilities) {
    if (-not (Test-Path -LiteralPath (Join-Path $moduleRoot $capability) -PathType Container)) {
        throw "Missing local capability implementation: $capability."
    }
    if ($configuration -notmatch "module\s+`"$capability`"") {
        throw "The local composition root does not select $capability."
    }
}

$requiredImages = @(
    'cockroachdb/cockroach:',
    'apache/kafka:',
    'rabbitmq:',
    'valkey/valkey:',
    'cassandra:',
    'localstack/localstack:'
)
$moduleConfiguration = (Get-ChildItem -LiteralPath $moduleRoot -Filter '*.tf' -Recurse | Get-Content -Raw) -join "`n"
foreach ($image in $requiredImages) {
    if ($moduleConfiguration -notmatch [regex]::Escape($image)) {
        throw "The local environment is missing the real pinned $image engine image."
    }
}

if ($configuration -notmatch 'provider\s+"aws"' -or
    $configuration -notmatch 's3\s*=\s*var\.localstack_endpoint' -or
    $configuration -notmatch 'secretsmanager\s*=\s*var\.localstack_endpoint' -or
    $configuration -notmatch 'kms\s*=\s*var\.localstack_endpoint') {
    throw 'S3, Secrets Manager and KMS must use the real AWS provider against LocalStack.'
}

if ($configuration -match 'data\s+"aws_caller_identity"' -or
    $configuration -match 'assume_role' -or
    $configuration -match 'arn:aws:iam') {
    throw 'The local root must not require an AWS account or IAM role.'
}

foreach ($setting in @('AWS_ENDPOINT_URL_S3', 'AWS_ENDPOINT_URL_SECRETS_MANAGER', 'S3_BUCKET')) {
    if ($configuration -notmatch $setting) {
        throw "The kind runtime contract is missing AWS SDK setting $setting."
    }
}

foreach ($boundary in @('real', 'LocalStack', 'not emulated', 'Pod Identity')) {
    if ($documentation -notmatch [regex]::Escape($boundary)) {
        throw "The fidelity document must explicitly cover '$boundary'."
    }
}

[pscustomobject]@{
    capabilities = $capabilities.Count
    realProtocolEngines = 5
    localstackAwsApis = @('KMS', 'S3', 'Secrets Manager')
    awsAccountRequired = $false
    kubernetes = 'kind'
    fidelityBoundaryDocumented = $true
} | ConvertTo-Json
