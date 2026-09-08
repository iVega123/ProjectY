#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $models = @('docker-compose.yml', 'deploy/base/compose.yaml', 'deploy/overlays/selfhost/compose.yaml')
    $expected = $null
    foreach ($model in $models) {
        $json = docker compose -f $model config --no-interpolate --format json
        if ($LASTEXITCODE -ne 0) { throw "Invalid Compose model: $model" }
        $config = ($json -join "`n") | ConvertFrom-Json
        $names = @($config.services.PSObject.Properties.Name | Sort-Object)
        if ($null -eq $expected) { $expected = $names }
        if (Compare-Object $expected $names) { throw "Application topology differs: $model" }
        foreach ($name in @('auth-gate', 'rider-manager', 'rental-core', 'api-gateway', 'media-guard')) {
            $service = $config.services.$name
            if (-not $service.build) { throw "Missing build: $model / $name" }
            $dockerfile = Join-Path $service.build.context $service.build.dockerfile
            if (-not (Test-Path -LiteralPath $dockerfile)) { throw "Missing Dockerfile: $dockerfile" }
            if ($service.environment.OTEL_EXPORTER_OTLP_ENDPOINT -ne 'http://otel-collector:4317') {
                throw "Missing collector wiring: $model / $name"
            }
        }
        if ($config.services.'rental-core'.environment.OTEL_SERVICE_NAME -ne 'rental-core') {
            throw "Rental SLO resource mismatch: $model"
        }
    }
    Write-Host 'PASS: shared application topology, real builds and rental SLO resource'
} finally {
    Pop-Location
}
