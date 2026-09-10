[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$envPath = Join-Path $root '.env'

if (-not (Test-Path -LiteralPath $envPath)) { throw '.env does not exist; run scripts/New-LocalSecrets.ps1 first.' }

$values = @{}
foreach ($line in Get-Content -LiteralPath $envPath) {
    if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
    $key, $value = $line -split '=', 2
    $values[$key.Trim()] = $value.Trim().Trim('"').Trim("'")
}

$required = @(
    'GATEWAY_IDENTITY_SIGNING_KEY', 'IDENTITY_KEY_ENCRYPTION_KEY', 'IDENTITY_ADMIN_PASSWORD',
    'MINIO_USER', 'MINIO_PASSWORD', 'RENTAL_OPERATIONS_RABBITMQ_USER', 'RENTAL_OPERATIONS_RABBITMQ_PASSWORD'
)
foreach ($key in $required) {
    if (-not $values.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($values[$key])) {
        throw ".env is missing $key; regenerate local credentials before starting Kubernetes."
    }
}

$values['MINIO_ACCESS_KEY'] = $values['MINIO_USER']
$values['MINIO_SECRET_KEY'] = $values['MINIO_PASSWORD']
$values['MINIO_ROOT_USER'] = $values['MINIO_USER']
$values['MINIO_ROOT_PASSWORD'] = $values['MINIO_PASSWORD']
$values['AWS_ACCESS_KEY_ID'] = $values['MINIO_USER']
$values['AWS_SECRET_ACCESS_KEY'] = $values['MINIO_PASSWORD']
$values['RabbitMQ__UserName'] = $values['RENTAL_OPERATIONS_RABBITMQ_USER']
$values['RabbitMQ__Password'] = $values['RENTAL_OPERATIONS_RABBITMQ_PASSWORD']
$values['RABBITMQ_DEFAULT_USER'] = $values['RENTAL_OPERATIONS_RABBITMQ_USER']
$values['RABBITMQ_DEFAULT_PASS'] = $values['RENTAL_OPERATIONS_RABBITMQ_PASSWORD']
$values['GatewayIdentity__SigningKey'] = $values['GATEWAY_IDENTITY_SIGNING_KEY']
$values['TELEMETRY_SECRET_KEY_BASE'] = $values['GATEWAY_IDENTITY_SIGNING_KEY']
$values['TELEMETRY_TICKET_KEY'] = $values['GATEWAY_IDENTITY_SIGNING_KEY']

$data = @{}
foreach ($entry in $values.GetEnumerator()) {
    $data[$entry.Key] = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$entry.Value))
}

$manifest = @{
    apiVersion = 'v1'
    kind = 'Secret'
    metadata = @{name = 'projecty-runtime-source'; namespace = 'projecty-secrets'}
    type = 'Opaque'
    data = $data
} | ConvertTo-Json -Depth 8

$namespaceManifest = (& $kubectl create namespace projecty-secrets --dry-run=client -o json) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not render the local secret-backend namespace.' }
$namespaceManifest | & $kubectl apply -f - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the local secret-backend namespace.' }
$manifest | & $kubectl apply -f - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not publish local values to the secret backend.' }
Write-Host 'Local secret backend updated without committing secret material.'
