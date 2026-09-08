#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('baseline','slow-db','db-down','rabbit-down')][string]$Mode = 'baseline',
    [ValidateRange(1, 20)][int]$Vus = 5,
    [string]$Duration = '30s',
    [switch]$KeepStack,
    [switch]$PrepareOnly,
    [switch]$Polyglot,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$fixture = Join-Path $root '.env.load-compose.json'
$project = 'projecty-load'
$exitCode = 1
function Set-Field($Object, [string]$Name, $Value) {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}
function Compose {
    & docker compose -p $project -f $fixture @args
    if ($LASTEXITCODE) { throw "Benchmark Compose command failed ($LASTEXITCODE)." }
}
try {
    if (-not (Test-Path '.env.load')) {
        & "$PSScriptRoot/New-LocalSecrets.ps1" -OutputPath (Join-Path $root '.env.load') -RabbitMqDefinitionsPath (Join-Path $root '.env.load-rabbitmq.json')
    }
    & "$PSScriptRoot/Update-CommandQueueNames.ps1" -DefinitionsPath (Join-Path $root '.env.load-rabbitmq.json')
    New-Item -ItemType Directory -Force load/results | Out-Null
    $composeFiles = @('-f', 'docker-compose.yml', '-f', 'docker-compose.chaos.yml')
    if ($Polyglot) { $composeFiles += @('-f', 'docker-compose.polyglot.yml') }
    $json = docker compose --env-file .env.load @composeFiles config --format json
    if ($LASTEXITCODE) { throw 'Benchmark model generation failed.' }
    $model = $json | ConvertFrom-Json
    $model.name = $project
    foreach ($property in $model.services.PSObject.Properties) {
        $service = $property.Value
        $service.PSObject.Properties.Remove('container_name')
        $service.PSObject.Properties.Remove('ports')
        if ($service.build) {
            Set-Field $service 'image' ("projecty-load/" + $property.Name.Replace('-migrations', '') + ':local')
        }
    }
    foreach ($volume in $model.volumes.PSObject.Properties.Value) { $volume.PSObject.Properties.Remove('name') }
    foreach ($network in $model.networks.PSObject.Properties.Value) { $network.PSObject.Properties.Remove('name') }
    foreach ($mount in $model.services.rabbitmq.volumes) {
        if ($mount.target -eq '/etc/rabbitmq/definitions.json') {
            $mount.source = Join-Path $root '.env.load-rabbitmq.json'
        }
    }
    $fixtureMount = @{type='bind';source=(Join-Path $root 'load/fixtures');target='/load';read_only=$true}
    $model.services.'api-gateway'.build.target = 'final'
    $model.services.'api-gateway'.healthcheck.test = @('CMD','/app/api-gateway','--healthcheck')
    $model.services.'api-gateway'.environment.GATEWAY_JWKS_URL = 'http://load-identity:8080/jwks'
    Set-Field $model.services.'api-gateway'.depends_on 'load-identity' @{condition='service_healthy'}
    Set-Field $model.services 'load-identity' @{
        image='node:24-alpine'; command=@('node','/load/identity.mjs'); networks=@('projecty')
        volumes=@($fixtureMount)
        healthcheck=@{test=@('CMD','node','-e','fetch("http://127.0.0.1:8080/health").then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))');interval='2s';timeout='2s';retries=15}
    }
    foreach ($port in @(
        @('api-gateway',8090,18090), @('toxiproxy',8474,18474),
        @('grafana',3000,13000), @('prometheus',9090,19090))) {
        Set-Field $model.services.($port[0]) 'ports' @(@{target=$port[1];published=[string]$port[2];host_ip='127.0.0.1';protocol='tcp'})
    }
    if ($Polyglot) {
        Set-Field $model.services.console 'ports' @(@{target=3001;published='13001';host_ip='127.0.0.1';protocol='tcp'})
        Set-Field $model.services.telemetry 'ports' @(@{target=4000;published='14000';host_ip='127.0.0.1';protocol='tcp'})
        $model.services.console.environment.CONSOLE_ORIGIN = 'http://localhost:13001'
        $model.services.console.environment.TELEMETRY_PUBLIC_URL = 'ws://localhost:14000/socket/websocket'
        $model.services.telemetry.environment | Add-Member -NotePropertyName TELEMETRY_ORIGINS -NotePropertyValue '//localhost:13001' -Force
    }
    Set-Field $model.services 'k6-load' @{
        image='grafana/k6:2.2.0'; user='0:0'; profiles=@('load'); networks=@('projecty')
        command=@('run','--out','experimental-prometheus-rw','/scripts/rental-flow.js')
        environment=@{
            BASE_URL='http://api-gateway:8090'; IDENTITY_URL='http://load-identity:8080'
            MODE=$Mode; VUS=[string]$Vus; DURATION=$Duration; SUMMARY_PATH="/results/$Mode.json"
            K6_PROMETHEUS_RW_SERVER_URL='http://prometheus:9090/api/v1/write'
            K6_PROMETHEUS_RW_TREND_STATS='p(95),p(99),min,max'
        }
        volumes=@(
            @{type='bind';source=(Join-Path $root 'load/k6');target='/scripts';read_only=$true},
            @{type='bind';source=(Join-Path $root 'load/results');target='/results'})
    }
    $model | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fixture -Encoding utf8
    Compose up -d --wait --wait-timeout 180 rabbitmq
    Compose exec -T rabbitmq rabbitmqctl import_definitions /etc/rabbitmq/definitions.json
    [string[]]$build = if ($NoBuild) { @() } else { @('--build') }
    $startServices = @('api-gateway', 'grafana', 'minio')
    if ($Polyglot) { $startServices += @('console', 'telemetry', 'risk-pricing') }
    Compose up @build -d --wait --wait-timeout 300 @startServices
    # Only this generated project and its fresh, separately named volumes are touched.
    Get-Content load/fixtures/seed-rider.sql -Raw | docker compose -p $project -f $fixture exec -T postgres sh -c 'exec psql -U "$POSTGRES_USER" -d "$RIDER_MANAGER_POSTGRES_DB" -v ON_ERROR_STOP=1'
    if ($LASTEXITCODE) { throw 'Rider fixture failed.' }
    Get-Content load/fixtures/seed-rental-core.sql -Raw | docker compose -p $project -f $fixture exec -T cockroachdb cockroach sql --insecure --database=projecty
    if ($LASTEXITCODE) { throw 'Rental-core fixture failed.' }
    Compose exec -T redis redis-cli FLUSHDB
    & "$PSScriptRoot/Invoke-Chaos.ps1" reset -Url 'http://127.0.0.1:18474'
    if ($PrepareOnly) { $exitCode = 0; return }
    $metadata = @{
        measuredAt=[DateTime]::UtcNow.ToString('o'); commit=(git rev-parse HEAD)
        docker=(docker info --format '{{.ServerVersion}}'); cpus=(docker info --format '{{.NCPU}}')
        memoryBytes=(docker info --format '{{.MemTotal}}'); mode=$Mode; vus=$Vus; duration=$Duration
    }
    $metadata | ConvertTo-Json | Set-Content "load/results/$Mode-environment.json" -Encoding utf8
    docker compose -p $project -f $fixture run --rm k6-load
    $exitCode = $LASTEXITCODE
} finally {
    if (Test-Path -LiteralPath $fixture) {
        if ($KeepStack -or $PrepareOnly) { Write-Host "Benchmark stack retained: $fixture (Grafana http://localhost:13000)." }
        else {
            docker compose -p $project -f $fixture down --volumes
            Remove-Item -LiteralPath $fixture -Force
        }
    }
    Pop-Location
}
exit $exitCode
