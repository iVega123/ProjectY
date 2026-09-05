#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)][string]$Drill,
    [switch]$Clear,
    [string]$Url = 'http://127.0.0.1:8474'
)
$ErrorActionPreference = 'Stop'
$catalog = Get-Content (Join-Path $PSScriptRoot '../deploy/chaos/drills.json') -Raw | ConvertFrom-Json
$entry = $catalog | Where-Object { $_.id -eq $Drill }
if (-not $entry) { throw "Unknown drill: $Drill" }
if (-not $entry.available) { throw $entry.expectation }
Write-Host $entry.expectation
Write-Host "Observe: $($entry.observe) Grafana: http://localhost:3000"
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
