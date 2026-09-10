[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolDirectory = Join-Path $root '.tools\kubernetes'
$kindName = if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) { 'kind.exe' } else { 'kind' }
$kind = Join-Path $toolDirectory $kindName

if (Test-Path -LiteralPath $kind) {
    & $kind delete cluster --name projecty
    if ($LASTEXITCODE -ne 0) { throw 'Could not delete the ProjectY kind cluster.' }
}

if (Get-Command docker -ErrorAction SilentlyContinue) {
    $registryId = & docker ps --all --quiet --filter 'name=^projecty-registry$'
    if ($registryId) {
        & docker rm --force projecty-registry | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not remove the ProjectY local registry.' }
    }
}

Write-Host 'ProjectY kind cluster and local registry removed.'
