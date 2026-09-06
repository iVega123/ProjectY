#requires -Version 5.1
[CmdletBinding()]
param([string]$DefinitionsPath = (Join-Path $PSScriptRoot '..\.rabbitmq-definitions.json'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$path = [IO.Path]::GetFullPath($DefinitionsPath)
$separator = [IO.Path]::DirectorySeparatorChar
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd($separator) + $separator
if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Definitions must remain inside the repository.'
}
$definitions = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$mapping = [ordered]@{
    rider_info_queue = 'cmd.rider.register'
    image_stream_queue = 'cmd.rider.store-document'
    licence_update_queue = 'cmd.rental.update-licence'
    rider_info_poison_queue = 'cmd.rider.dead'
    licence_update_poison_queue = 'cmd.rental.licence-update.dead'
}
# Add names and permissions without removing old queues or rotating credentials.
foreach ($old in $mapping.Keys) {
    $new = $mapping[$old]
    $escaped = [regex]::Escape($new)
    foreach ($permission in $definitions.permissions) {
        foreach ($field in @('configure', 'write', 'read')) {
            if ($permission.$field.Contains($old) -and -not $permission.$field.Contains($escaped)) {
                $permission.$field = $permission.$field.Replace($old, "($old|$escaped)")
            }
        }
    }
    foreach ($queue in @($definitions.queues | Where-Object { $_.name -eq $old })) {
        $vhost = $queue.vhost
        if (-not @($definitions.queues | Where-Object { $_.name -eq $new -and $_.vhost -eq $vhost }).Count) {
            $copy = $queue | ConvertTo-Json -Depth 20 | ConvertFrom-Json
            $copy.name = $new
            $definitions.queues = @($definitions.queues) + $copy
        }
    }
}
$json = $definitions | ConvertTo-Json -Depth 30
[IO.File]::WriteAllText($path, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host 'Command queue names added; existing queues and credentials preserved.'
