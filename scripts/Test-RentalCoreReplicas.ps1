#requires -Version 5.1
# Acceptance for #70: two rental-core replicas against one database and one broker.
#
# The unit of proof is a backlog, not a request. Kafka is cut while k6 creates
# rentals, so every rental event waits in the outbox; then Kafka comes back and
# both replicas' relays compete for the same rows. The run passes only when:
#
#   - both replicas published something (they really competed),
#   - the replicas' published counts add up to the backlog (neither repeated),
#   - the topic grew by exactly the number of rental events written (none lost,
#     none duplicated on the wire),
#   - the outbox is empty afterwards.
[CmdletBinding()]
param(
    [ValidateRange(1, 20)][int]$Vus = 5,
    [string]$Duration = '30s',
    [switch]$NoBuild,
    [switch]$KeepStack
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$project = 'projecty-load'
$fixture = Join-Path $root '.env.load-compose.json'
$exitCode = 1
function Compose {
    & docker compose -p $project -f $fixture @args
    if ($LASTEXITCODE) { throw "Compose command failed ($LASTEXITCODE): $args" }
}
function Scalar([string]$Sql) {
    [long](Compose exec -T cockroachdb cockroach sql --insecure --database=projecty --format=csv -e $Sql | Select-Object -Last 1)
}
function TopicSize([string]$Topic) {
    $lines = Compose exec -T kafka /opt/kafka/bin/kafka-get-offsets.sh --bootstrap-server kafka:9092 --topic $Topic
    ($lines | Where-Object { $_ -match ':\d+:\d+$' } | ForEach-Object { [long]($_ -split ':')[-1] } | Measure-Object -Sum).Sum
}
try {
    # A hashtable, not an array: array splatting passes '-PrepareOnly' as a
    # positional string, which binds to -Mode.
    $prepare = @{ PrepareOnly = $true; Polyglot = $true; NoBuild = [bool]$NoBuild }
    # In-process, so the same script runs under Windows PowerShell and pwsh on CI.
    & "$PSScriptRoot/Run-LoadTest.ps1" @prepare
    if ($LASTEXITCODE) { throw 'Stack preparation failed.' }

    # The runner strips container_name, which is what lets Compose run two.
    Compose up -d --no-deps --wait --wait-timeout 180 --scale rental-core=2 rental-core
    $replicas = @(docker ps -q --filter "label=com.docker.compose.project=$project" --filter 'label=com.docker.compose.service=rental-core')
    if ($replicas.Count -ne 2) { throw "Expected 2 rental-core replicas, found $($replicas.Count)." }
    $since = [DateTime]::UtcNow.ToString('o')

    # Start from a drained outbox, so the backlog below is only this run's.
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ((Scalar "SELECT count(*) FROM outbox WHERE published_at IS NULL AND aggregate_type = 'rental'") -gt 0) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The outbox did not drain before the run.' }
        Start-Sleep -Seconds 1
    }
    $eventsBefore = Scalar "SELECT count(*) FROM outbox WHERE aggregate_type = 'rental'"
    $topicBefore = TopicSize 'rental.started'

    docker compose -p $project -f $fixture run --rm -e MODE=kafka-down -e VUS=$Vus -e DURATION=$Duration -e SUMMARY_PATH=/results/two-replicas.json k6-load
    $k6Exit = $LASTEXITCODE
    $eventsWritten = (Scalar "SELECT count(*) FROM outbox WHERE aggregate_type = 'rental'") - $eventsBefore
    $backlog = Scalar "SELECT count(*) FROM outbox WHERE published_at IS NULL AND aggregate_type = 'rental'"

    $drain = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Seconds 1
        $pending = Scalar "SELECT count(*) FROM outbox WHERE published_at IS NULL AND aggregate_type = 'rental'"
    } while ($pending -gt 0 -and $drain.Elapsed.TotalSeconds -lt 180)
    $drain.Stop()
    Start-Sleep -Seconds 3
    $topicGrowth = (TopicSize 'rental.started') - $topicBefore

    $perReplica = foreach ($container in $replicas) {
        $published = 0
        # librdkafka writes to stderr, and Windows PowerShell turns a native
        # stderr line into a terminating error under ErrorActionPreference Stop.
        $logs = & { $ErrorActionPreference = 'Continue'; docker logs --since $since $container 2>&1 } | ForEach-Object { "$_" }
        foreach ($line in $logs) {
            if ($line -match '"PublishedCount":(\d+)') { $published += [int]$Matches[1] }
        }
        [pscustomobject]@{ container = (docker inspect --format '{{.Name}}' $container).TrimStart('/'); published = $published }
    }
    $publishedByReplicas = ($perReplica | Measure-Object -Property published -Sum).Sum

    $result = [ordered]@{
        measuredAt = [DateTime]::UtcNow.ToString('o'); commit = (git rev-parse HEAD)
        vus = $Vus; duration = $Duration; k6Exit = $k6Exit
        rentalEventsWritten = $eventsWritten; backlogAtRecovery = $backlog
        publishedByReplicas = $publishedByReplicas; replicas = @($perReplica)
        topicGrowth = $topicGrowth; pendingAfterDrain = $pending
        drainSeconds = [Math]::Round($drain.Elapsed.TotalSeconds, 1)
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content 'load/results/two-replicas-result.json' -Encoding utf8
    $result | ConvertTo-Json -Depth 5 | Write-Host

    $failures = @()
    if ($k6Exit -ne 0) { $failures += "k6 exited with $k6Exit." }
    if ($eventsWritten -le 0) { $failures += 'No rental events were written during the outage.' }
    if ($pending -ne 0) { $failures += "Outbox still holds $pending rental events." }
    if (@($perReplica | Where-Object { $_.published -eq 0 }).Count) { $failures += 'A replica published nothing; the replicas did not compete.' }
    if ($topicGrowth -ne $eventsWritten) { $failures += "Topic grew by $topicGrowth for $eventsWritten events written." }
    # Events written before the toxic took effect may be published by either
    # replica before the backlog is measured; they are logged too.
    if ($publishedByReplicas -ne $eventsWritten) { $failures += "Replicas published $publishedByReplicas for $eventsWritten events written." }
    if ($failures) { throw ($failures -join ' ') }
    Write-Host "PASS: $eventsWritten rental events, $($perReplica[0].published) + $($perReplica[1].published) published across two replicas, topic grew by $topicGrowth."
    $exitCode = 0
} finally {
    if ((Test-Path -LiteralPath $fixture) -and -not $KeepStack) {
        docker compose -p $project -f $fixture down --volumes
        Remove-Item -LiteralPath $fixture -Force
    }
    Pop-Location
}
exit $exitCode
