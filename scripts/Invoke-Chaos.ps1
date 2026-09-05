#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)]
    [ValidateSet('list', 'add', 'remove', 'reset')][string]$Action,
    [Parameter(Position=1)][ValidatePattern('^[a-z0-9_-]+$')][string]$Proxy,
    [Parameter(Position=2)][ValidatePattern('^[a-z0-9_-]+$')][string]$Name = 'drill',
    [ValidateSet('latency', 'timeout', 'slicer', 'limit_data')][string]$Type = 'latency',
    [ValidateRange(0, 60000)][int]$Value = 500,
    [string]$Url = 'http://127.0.0.1:8474'
)
$ErrorActionPreference = 'Stop'
$Url = $Url.TrimEnd('/')
function Request([string]$Method, [string]$Path, $Body) {
    $parameters = @{Method=$Method; Uri="$Url$Path"; TimeoutSec=10; UseBasicParsing=$true; UserAgent='ProjectY-Chaos/1.0'}
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 8 -Compress
    }
    Invoke-RestMethod @parameters
}
if ($Action -eq 'list') { Request GET '/proxies' $null | ConvertTo-Json -Depth 10; exit }
if ($Action -eq 'reset') { Request POST '/reset' $null; Write-Host 'All toxics cleared and proxies enabled.'; exit }
if (-not $Proxy) { throw 'A proxy name is required; use list to inspect the active topology.' }
$current = Request GET "/proxies/$Proxy" $null
if ($current.toxics | Where-Object { $_.name -eq $Name }) {
    Request DELETE "/proxies/$Proxy/toxics/$Name" $null
}
if ($Action -eq 'remove') { Write-Host "Removed $Name from $Proxy."; exit }
$attributes = switch ($Type) {
    'latency' { @{latency=$Value; jitter=0} }
    'timeout' { @{timeout=$Value} }
    'slicer' { @{average_size=64; size_variation=16; delay=$Value} }
    'limit_data' { @{bytes=$Value} }
}
Request POST "/proxies/$Proxy/toxics" @{name=$Name; type=$Type; stream='downstream'; toxicity=1.0; attributes=$attributes} | Out-Null
Write-Host "Applied $Name ($Type=$Value) to $Proxy."
Write-Host 'Observe: http://localhost:3000 (Grafana), gateway /metrics, and the affected service trace.'
