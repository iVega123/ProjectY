#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$RabbitMqDefinitionsPath,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot "..\.env"
}

if ([string]::IsNullOrWhiteSpace($RabbitMqDefinitionsPath)) {
    $RabbitMqDefinitionsPath = Join-Path $PSScriptRoot "..\.rabbitmq-definitions.json"
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedDefinitionsPath = [System.IO.Path]::GetFullPath($RabbitMqDefinitionsPath)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRootPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

foreach ($path in @($resolvedOutputPath, $resolvedDefinitionsPath)) {
    if (-not $path.StartsWith($repositoryRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated secret files must remain inside the ProjectY repository."
    }
}

foreach ($path in @($resolvedOutputPath, $resolvedDefinitionsPath)) {
    if ((Test-Path -LiteralPath $path) -and -not $Force) {
        throw "$path already exists. Use -Force only when intentionally rotating every local credential."
    }
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

function New-RabbitMqPasswordHash {
    param([Parameter(Mandatory = $true)][string]$Password)

    $salt = [byte[]]::new(4)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($salt)
    }
    finally {
        $generator.Dispose()
    }

    $passwordBytes = [System.Text.Encoding]::UTF8.GetBytes($Password)
    $saltedPassword = [byte[]]::new($salt.Length + $passwordBytes.Length)
    [System.Array]::Copy($salt, 0, $saltedPassword, 0, $salt.Length)
    [System.Array]::Copy($passwordBytes, 0, $saltedPassword, $salt.Length, $passwordBytes.Length)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha256.ComputeHash($saltedPassword)
    }
    finally {
        $sha256.Dispose()
    }

    $saltedDigest = [byte[]]::new($salt.Length + $digest.Length)
    [System.Array]::Copy($salt, 0, $saltedDigest, 0, $salt.Length)
    [System.Array]::Copy($digest, 0, $saltedDigest, $salt.Length, $digest.Length)
    return [Convert]::ToBase64String($saltedDigest)
}

$localSuffix = (New-RandomValue -ByteCount 6).ToLowerInvariant()
$values = [ordered]@{
    SWAGGER_ENABLED                   = "true"
    RABBITMQ_ADMIN_USER               = "projecty_admin_$localSuffix"
    RABBITMQ_ADMIN_PASSWORD           = (New-RandomValue)
    MOTO_HUB_RABBITMQ_USER            = "moto_hub_$localSuffix"
    MOTO_HUB_RABBITMQ_PASSWORD        = (New-RandomValue)
    RENTAL_OPERATIONS_RABBITMQ_USER   = "rental_operations_$localSuffix"
    RENTAL_OPERATIONS_RABBITMQ_PASSWORD = (New-RandomValue)
    RENTAL_CORE_RABBITMQ_USER         = "rental_core_$localSuffix"
    RENTAL_CORE_RABBITMQ_PASSWORD     = (New-RandomValue)
    MEDIA_GUARD_RABBITMQ_USER         = "media_guard_$localSuffix"
    MEDIA_GUARD_RABBITMQ_PASSWORD     = (New-RandomValue)
    RIDER_EVENTS_SIGNING_KEY          = (New-RandomValue)
    MINIO_USER                        = "projecty_$localSuffix"
    MINIO_PASSWORD                    = (New-RandomValue)
    GRAFANA_USER                      = "projecty_$localSuffix"
    GRAFANA_PASSWORD                  = (New-RandomValue)
    MOTO_HUB_JWT_SIGNING_KEY          = (New-RandomValue)
    RENTAL_OPERATIONS_JWT_SIGNING_KEY = (New-RandomValue)
    GATEWAY_IDENTITY_SIGNING_KEY      = (New-RandomValue)
    # Sela a semente Ed25519 que o identity guarda em signing_keys. Não é a
    # chave de assinatura: é o que impede quem lê o banco de virar emissor.
    IDENTITY_KEY_ENCRYPTION_KEY       = (New-RandomValue)
    IDENTITY_ADMIN_PASSWORD           = (New-RandomValue)
}

$lines = @(
    "# Generated locally at $([DateTimeOffset]::UtcNow.ToString('O')). Do not commit."
    $values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
)

$rabbitMqUsers = @(
    [ordered]@{
        name = $values.RABBITMQ_ADMIN_USER
        password_hash = (New-RabbitMqPasswordHash -Password $values.RABBITMQ_ADMIN_PASSWORD)
        hashing_algorithm = "rabbit_password_hashing_sha256"
        tags = @("administrator")
        limits = @{}
    },
    [ordered]@{
        name = $values.MOTO_HUB_RABBITMQ_USER
        password_hash = (New-RabbitMqPasswordHash -Password $values.MOTO_HUB_RABBITMQ_PASSWORD)
        hashing_algorithm = "rabbit_password_hashing_sha256"
        tags = @()
        limits = @{}
    },
    [ordered]@{
        name = $values.RENTAL_OPERATIONS_RABBITMQ_USER
        password_hash = (New-RabbitMqPasswordHash -Password $values.RENTAL_OPERATIONS_RABBITMQ_PASSWORD)
        hashing_algorithm = "rabbit_password_hashing_sha256"
        tags = @()
        limits = @{}
    },
    [ordered]@{
        name = $values.RENTAL_CORE_RABBITMQ_USER
        password_hash = (New-RabbitMqPasswordHash -Password $values.RENTAL_CORE_RABBITMQ_PASSWORD)
        hashing_algorithm = "rabbit_password_hashing_sha256"
        tags = @()
        limits = @{}
    },
    [ordered]@{
        name = $values.MEDIA_GUARD_RABBITMQ_USER
        password_hash = (New-RabbitMqPasswordHash -Password $values.MEDIA_GUARD_RABBITMQ_PASSWORD)
        hashing_algorithm = "rabbit_password_hashing_sha256"
        tags = @()
        limits = @{}
    }
)

$rabbitMqPermissions = @(
    [ordered]@{ user = $values.RABBITMQ_ADMIN_USER; vhost = "projecty"; configure = ".*"; write = ".*"; read = ".*" },
    [ordered]@{ user = $values.RABBITMQ_ADMIN_USER; vhost = "projecty-rental"; configure = ".*"; write = ".*"; read = ".*" },
    [ordered]@{ user = $values.MOTO_HUB_RABBITMQ_USER; vhost = "projecty-rental"; configure = "^cmd\.rental\.update-licence$"; write = "^(amq\.default|cmd\.rental\.update-licence)$"; read = "^$" },
    [ordered]@{ user = $values.RENTAL_OPERATIONS_RABBITMQ_USER; vhost = "projecty-rental"; configure = "^(cmd\.rental\.update-licence(\.(retry\.[1-3]|redelivery|dead))?|cmd\.rental\.licence-update\.dead)$"; write = "^(amq\.default|cmd\.rental\.update-licence(\.(retry\.[1-3]|redelivery|dead))?|cmd\.rental\.licence-update\.dead)$"; read = "^(cmd\.rental\.update-licence(\.(retry\.[1-3]|redelivery|dead))?|cmd\.rental\.licence-update\.dead)$" },
    [ordered]@{ user = $values.RENTAL_CORE_RABBITMQ_USER; vhost = "projecty"; configure = ".*"; write = ".*"; read = ".*" },
    [ordered]@{ user = $values.MEDIA_GUARD_RABBITMQ_USER; vhost = "projecty"; configure = ".*"; write = ".*"; read = ".*" }
)

$rabbitMqDefinitions = [ordered]@{
    users = $rabbitMqUsers
    vhosts = @(
        [ordered]@{ name = "projecty" },
        [ordered]@{ name = "projecty-rental" }
    )
    permissions = $rabbitMqPermissions
    topic_permissions = @()
    parameters = @()
    global_parameters = @()
    policies = @()
    queues = @(
        [ordered]@{ name = "cmd.rental.update-licence"; vhost = "projecty-rental"; durable = $true; auto_delete = $false; arguments = @{} }
    )
    exchanges = @()
    bindings = @()
}

[System.IO.File]::WriteAllLines($resolvedOutputPath, $lines)
[System.IO.File]::WriteAllText(
    $resolvedDefinitionsPath,
    ($rabbitMqDefinitions | ConvertTo-Json -Depth 10),
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated a fresh local secret set at $resolvedOutputPath"
Write-Host "Generated RabbitMQ users and service-specific permissions at $resolvedDefinitionsPath"
Write-Host "If persistent volumes used older credentials, recreate those volumes before starting the stack."
