#requires -Version 5.1
<#
Reproduces the rate limiter's Redis ceiling from #193.

The token bucket is read from services/api-gateway/src/rate_limit.rs at run time,
so the measurement follows the script the gateway actually sends, as EVALSHA.
Each persistence mode gets a fresh Redis container, and redis-benchmark runs
inside it: the network is loopback, so the number is Redis's and not the host's.

Absolute numbers depend on the machine. The ratios between modes are the finding.
#>
[CmdletBinding()]
param(
    [ValidateRange(1000, 10000000)][int]$Requests = 20000,
    [ValidateRange(1, 1000)][int]$Clients = 50,
    [string]$Image = 'redis:8.10-alpine',
    [string]$OutputPath = 'docs/measurements/rate-limiter-redis.json'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$container = 'projecty-rate-limiter-benchmark'
$scriptFile = Join-Path ([IO.Path]::GetTempPath()) 'projecty-token-bucket.lua'

function Invoke-Docker {
    # Native stderr must not become a terminating error under 'Stop'; the exit
    # code is what decides.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & docker @args 2>&1 } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE) { throw "docker $($args -join ' ') failed ($LASTEXITCODE): $output" }
    $output | ForEach-Object { "$_" }
}

function Invoke-Benchmark([string[]]$Command) {
    $argv = @('exec', $container, 'redis-benchmark', '-q', '-n', "$Requests", '-c', "$Clients", '-r', '10000') + $Command
    $output = (Invoke-Docker @argv) -join "`n"
    $found = [regex]::Matches($output, '([\d.]+) requests per second, p50=([\d.]+) msec')
    if ($found.Count -eq 0) { throw "redis-benchmark printed no result: $output" }
    $last = $found[$found.Count - 1]
    [pscustomobject]@{ Rps = [double]$last.Groups[1].Value; P50Ms = [double]$last.Groups[2].Value }
}

try {
    $source = [IO.File]::ReadAllText((Join-Path $root 'services/api-gateway/src/rate_limit.rs'))
    $match = [regex]::Match($source, '(?s)TOKEN_BUCKET_SCRIPT: &str = r#"(.*?)"#;')
    if (-not $match.Success) { throw 'TOKEN_BUCKET_SCRIPT was not found in rate_limit.rs.' }
    # Written without a BOM and copied in, not piped: Windows PowerShell 5.1 adds a
    # BOM when piping text to a native process, and Lua rejects the first character.
    [IO.File]::WriteAllText($scriptFile, ($match.Groups[1].Value -replace "`r", ''), (New-Object Text.UTF8Encoding $false))

    # always: what every consumer paid before #193. everysec: the shared Redis now.
    # none: the rate limiter's own Redis now.
    $modes = [ordered]@{
        always   = '--appendonly yes --appendfsync always'
        everysec = '--appendonly yes --appendfsync everysec'
        none     = "--save '' --appendonly no"
    }
    $results = @()
    foreach ($mode in $modes.Keys) {
        try { Invoke-Docker rm -f $container | Out-Null } catch { }
        Invoke-Docker run -d --name $container $Image sh -c "exec redis-server $($modes[$mode])" | Out-Null
        $ready = $false
        for ($attempt = 0; $attempt -lt 50 -and -not $ready; $attempt++) {
            try { $ready = ((Invoke-Docker exec $container redis-cli ping) -join '') -eq 'PONG' } catch { }
            if (-not $ready) { Start-Sleep -Milliseconds 200 }
        }
        if (-not $ready) { throw "Redis did not start with persistence '$mode'." }
        Invoke-Docker cp $scriptFile "${container}:/tmp/token-bucket.lua" | Out-Null
        $sha = ((Invoke-Docker exec $container sh -c 'redis-cli -x SCRIPT LOAD < /tmp/token-bucket.lua') | Select-Object -Last 1).Trim()
        if ($sha -notmatch '^[0-9a-f]{40}$') { throw "SCRIPT LOAD returned '$sha'." }

        $set = Invoke-Benchmark @('-t', 'set')
        # The capacity and refill the gateway is configured with; the key is
        # randomised over 10,000 principals, so buckets both allow and refuse.
        $bucket = Invoke-Benchmark @('evalsha', $sha, '1', 'projecty:ratelimit:benchmark:__rand_int__', '120', '120')
        $results += [pscustomobject][ordered]@{
            persistence      = $mode
            setRps           = $set.Rps
            setP50Ms         = $set.P50Ms
            tokenBucketRps   = $bucket.Rps
            tokenBucketP50Ms = $bucket.P50Ms
        }
        Invoke-Docker rm -f $container | Out-Null
    }

    $always = ($results | Where-Object { $_.persistence -eq 'always' }).tokenBucketRps
    foreach ($result in $results) {
        $result | Add-Member -NotePropertyName tokenBucketVsAlways -NotePropertyValue ([Math]::Round($result.tokenBucketRps / $always, 1))
    }
    $report = [ordered]@{
        measuredAt = [DateTime]::UtcNow.ToString('o')
        commit     = (git rev-parse HEAD)
        image      = $Image
        requests   = $Requests
        clients    = $Clients
        keyspace   = 10000
        docker     = ((Invoke-Docker info --format '{{.ServerVersion}}') -join '')
        cpus       = [int]((Invoke-Docker info --format '{{.NCPU}}') -join '')
        results    = $results
    }
    [IO.File]::WriteAllText((Join-Path $root $OutputPath), (($report | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding $false))
    $results | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "Written to $OutputPath"
} finally {
    try { Invoke-Docker rm -f $container | Out-Null } catch { }
    Remove-Item -LiteralPath $scriptFile -Force -ErrorAction SilentlyContinue
    Pop-Location
}
