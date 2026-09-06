#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot ('..\.env.queue-test-' + [guid]::NewGuid().ToString('N') + '.json')
$legacy = @{
    users = @(@{name='fixture'; password_hash='unchanged-fixture-hash'})
    permissions = @(@{user='fixture';vhost='projecty-rider';configure='^(rider_info_queue(\.(retry\.[1-3]|redelivery|dead))?|rider_info_poison_queue)$';write='^(amq\.default|rider_info_queue)$';read='^rider_info_queue$'})
    queues = @(@{name='rider_info_queue';vhost='projecty-rider';durable=$true;auto_delete=$false;arguments=@{}})
}
try {
    [IO.File]::WriteAllText($fixture, ($legacy | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Update-CommandQueueNames.ps1') -DefinitionsPath $fixture
    $first = [IO.File]::ReadAllText($fixture)
    $result = $first | ConvertFrom-Json
    if ($result.users[0].password_hash -ne 'unchanged-fixture-hash') { throw 'Credentials changed' }
    if (@($result.queues).Count -ne 2) { throw 'Legacy queue lost or command queue missing' }
    $acl = $result.permissions[0]
    foreach ($name in @('rider_info_queue','cmd.rider.register','cmd.rider.register.retry.1','cmd.rider.dead')) {
        if ($name -notmatch $acl.configure) { throw "Expected configure permission: $name" }
    }
    foreach ($name in @('cmdXriderXregister','cmd.rider.register.retry.4','cmd.rental.update-licence')) {
        if ($name -match $acl.configure) { throw "Unexpected configure permission: $name" }
    }
    if ('cmd.rider.register' -notmatch $acl.write -or 'cmd.rider.register' -notmatch $acl.read) { throw 'Command delivery permissions missing' }
    & (Join-Path $PSScriptRoot 'Update-CommandQueueNames.ps1') -DefinitionsPath $fixture
    if ([IO.File]::ReadAllText($fixture) -ne $first) { throw 'Update is not idempotent' }
    Write-Host 'PASS: existing queues, credentials, bounded ACLs and idempotent upgrade'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Force }
}
