#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\.env"),
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

if (-not $resolvedOutputPath.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The output path must remain inside the ProjectY repository."
}

if ((Test-Path -LiteralPath $resolvedOutputPath) -and -not $Force) {
    throw "$resolvedOutputPath already exists. Use -Force only when intentionally rotating every local credential."
}

function New-RandomValue {
    param([int]$ByteCount = 32)

    $bytes = [byte[]]::new($ByteCount)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$localSuffix = (New-RandomValue -ByteCount 6).ToLowerInvariant()
$values = [ordered]@{
    AUTH_GATE_POSTGRES_DB             = "AuthGateDB"
    MOTO_HUB_POSTGRES_DB              = "MotoHubDB"
    RIDER_MANAGER_POSTGRES_DB         = "RiderManagerDB"
    POSTGRES_USER                     = "projecty_$localSuffix"
    POSTGRES_PASSWORD                 = (New-RandomValue)
    PGADMIN_EMAIL                     = "admin@projecty.local"
    PGADMIN_PASSWORD                  = (New-RandomValue)
    MONGO_DB                          = "RentalOperationsDB"
    MONGO_USER                        = "projecty_$localSuffix"
    MONGO_PASSWORD                    = (New-RandomValue)
    RABBITMQ_USER                     = "projecty_$localSuffix"
    RABBITMQ_PASSWORD                 = (New-RandomValue)
    MINIO_USER                        = "projecty_$localSuffix"
    MINIO_PASSWORD                    = (New-RandomValue)
    GRAFANA_USER                      = "projecty_$localSuffix"
    GRAFANA_PASSWORD                  = (New-RandomValue)
    AUTH_GATE_JWT_SIGNING_KEY         = (New-RandomValue)
    MOTO_HUB_JWT_SIGNING_KEY          = (New-RandomValue)
    RIDER_MANAGER_JWT_SIGNING_KEY     = (New-RandomValue)
    RENTAL_OPERATIONS_JWT_SIGNING_KEY = (New-RandomValue)
    MOTO_HUB_API_KEY                  = (New-RandomValue)
    RIDER_MANAGER_API_KEY             = (New-RandomValue)
    RENTAL_OPERATIONS_API_KEY         = (New-RandomValue)
}

$lines = @(
    "# Generated locally at $([DateTimeOffset]::UtcNow.ToString('O')). Do not commit."
    $values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
)

[System.IO.File]::WriteAllLines($resolvedOutputPath, $lines)
Write-Host "Generated a fresh local secret set at $resolvedOutputPath"
Write-Host "If persistent volumes used older credentials, recreate those volumes before starting the stack."
