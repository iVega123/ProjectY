#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)][string]$Drill,
    [switch]$Clear,
    [string]$Url = 'http://127.0.0.1:8474',
    # The Compose project a container drill acts on. Tilt names its project `projecty`.
    [ValidatePattern('^[a-z0-9][a-z0-9_-]*$')][string]$Project = 'projecty'
)
$ErrorActionPreference = 'Stop'
$catalog = Get-Content (Join-Path $PSScriptRoot '../deploy/chaos/drills.json') -Raw | ConvertFrom-Json
$entry = $catalog | Where-Object { $_.id -eq $Drill }
if (-not $entry) { throw "Unknown drill: $Drill" }
Write-Host $entry.expectation
Write-Host "Observe: $($entry.observe) Grafana: http://localhost:3000"

if ($entry.kind -eq 'container') {
    # By label, not by name: the container name belongs to Compose, and the
    # isolated validation projects rename every container they start.
    $filters = @('--filter', "label=com.docker.compose.project=$Project", '--filter', "label=com.docker.compose.service=$($entry.service)")
    $containers = @(docker ps -a -q @filters)
    if ($LASTEXITCODE) { throw 'Docker is not reachable.' }
    # A stop that finds nothing must fail. Reporting success here would turn a
    # drill that never happened into evidence that the system survived it.
    if ($containers.Count -eq 0) {
        throw "No $($entry.service) container in Compose project '$Project'. This drill needs the full topology: tilt up -- --orchestrator=compose --full"
    }
    $verb = if ($Clear) { 'start' } else { 'stop' }
    docker $verb @containers | Out-Null
    if ($LASTEXITCODE) { throw "docker $verb failed for $($entry.service)." }
    Write-Host "Ran docker $verb on $($entry.service) ($($containers.Count) container)."
    return
}

$action = if ($Clear) { 'remove' } else { 'add' }
try {
    foreach ($toxic in $entry.toxics) {
        & "$PSScriptRoot/Invoke-Chaos.ps1" $action $entry.proxy $toxic.name -Type $toxic.type -Value $toxic.value -Url $Url
    }
} catch {
    if (-not $Clear) {
        foreach ($toxic in $entry.toxics) {
            try { & "$PSScriptRoot/Invoke-Chaos.ps1" remove $entry.proxy $toxic.name -Url $Url }
            catch { Write-Warning "Could not clear $($toxic.name); use Invoke-Chaos.ps1 reset." }
        }
    }
    throw
}
