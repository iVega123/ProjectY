#requires -Version 5.1
# Integration contract: real Redis traffic crosses the proxy and recovers in-place.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$fixture = Join-Path $root '.env.chaos-validation.json'
$project = 'projecty-chaos-validation'
try {
    $json = docker compose -f docker-compose.yml -f docker-compose.chaos.yml config --format json
    if ($LASTEXITCODE) { throw 'Compose configuration failed.' }
    $model = $json | ConvertFrom-Json
    foreach ($service in @('auth-gate','rider-manager','moto-hub','rental-operations')) {
        $environment = $model.services.$service.environment
        foreach ($key in @('Redis__ConnectionString','RabbitMQ__HostName')) {
            if ($environment.$key -notmatch '^toxiproxy(:|$)') { throw "$service bypasses proxy: $key" }
        }
        $dbKey = if ($service -eq 'rental-operations') { 'MongoDbSettings__ConnectionString' } else { 'ConnectionStrings__Postgresql' }
        if ($environment.$dbKey -notmatch 'toxiproxy') { throw "$service bypasses its database proxy." }
        if ($model.services.$service.depends_on.toxiproxy.condition -ne 'service_healthy') { throw "$service is not health-gated." }
    }
    if ($model.services.'api-gateway'.environment.GATEWAY_REDIS_URL -notmatch '://toxiproxy:') { throw 'Gateway bypasses Redis proxy.' }
    if ($model.services.'rider-manager'.environment.MinIO__Endpoint -ne 'toxiproxy') { throw 'Object storage bypasses proxy.' }
    # Isolate from existing developer containers, names, ports and persistent data.
    $model.name = $project
    foreach ($service in $model.services.PSObject.Properties.Value) { $service.PSObject.Properties.Remove('container_name'); $service.PSObject.Properties.Remove('ports') }
    foreach ($volume in $model.volumes.PSObject.Properties.Value) { $volume.PSObject.Properties.Remove('name') }
    foreach ($network in $model.networks.PSObject.Properties.Value) { $network.PSObject.Properties.Remove('name') }
    $model.services.toxiproxy | Add-Member -NotePropertyName ports -NotePropertyValue @(@{target=8474; published='18474'; host_ip='127.0.0.1'; protocol='tcp'})
    $model | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $fixture -Encoding utf8
    docker compose -p $project -f $fixture up -d --wait redis toxiproxy
    if ($LASTEXITCODE) { throw 'Fixture startup failed.' }
    function PingProxy {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $reply = docker compose -p $project -f $fixture exec -T redis redis-cli -h toxiproxy -p 6379 ping
        if ($LASTEXITCODE -or $reply -ne 'PONG') { throw 'Redis did not respond through Toxiproxy.' }
        $timer.Stop()
        $timer.ElapsedMilliseconds
    }
    $api = 'http://127.0.0.1:18474'
    $before = PingProxy
    & "$PSScriptRoot/Invoke-Chaos.ps1" add redis test-latency -Value 500 -Url $api
    $during = PingProxy
    if ($during - $before -lt 350) { throw "Latency injection was not observed: before=$before during=$during" }
    & "$PSScriptRoot/Invoke-Chaos.ps1" remove redis test-latency -Url $api
    & "$PSScriptRoot/Invoke-Chaos.ps1" remove redis test-latency -Url $api
    $after = PingProxy
    if ($during - $after -lt 350) { throw "Latency did not recover: during=$during after=$after" }
    & "$PSScriptRoot/Invoke-Chaos.ps1" add redis test-timeout -Type timeout -Value 0 -Url $api
    & "$PSScriptRoot/Invoke-Chaos.ps1" reset -Url $api
    $null = PingProxy
    Write-Host "PASS: proxy latency baseline=$before ms injected=$during ms recovered=$after ms; reset restored traffic."
} finally {
    if (Test-Path -LiteralPath $fixture) {
        docker compose -p $project -f $fixture down
        Remove-Item -LiteralPath $fixture
    }
    Pop-Location
}
