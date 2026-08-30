[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Cockroach', 'Cassandra', 'Kafka', 'MinIO')]
    [string]$Resource,

    [string]$ProjectName = 'projecty'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker) {
    $dockerCandidates = @(
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe')
    )
    $dockerPath = $dockerCandidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $dockerPath) {
        throw 'Docker CLI was not found. Install Docker Desktop or add docker to PATH.'
    }
    $docker = Get-Command $dockerPath -ErrorAction Stop
}
$composeArguments = @(
    'compose',
    '--project-name', $ProjectName,
    '--env-file', '.env',
    '--profile', 'init',
    '--profile', 'full',
    '-f', 'deploy/compose.yaml'
)

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $docker.Source @composeArguments @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    switch ($Resource) {
        'Cockroach' {
            Invoke-Compose -Arguments @('run', '--rm', '--no-deps', 'cockroach-init')
        }
        'Cassandra' {
            Invoke-Compose -Arguments @(
                'exec', '-T', 'cassandra',
                'cqlsh', '-f', '/schema/001_init.cql'
            )
        }
        'Kafka' {
            $topics = Get-Content -LiteralPath 'deploy/kafka/topics.txt' |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -and -not $_.StartsWith('#') }

            foreach ($topic in $topics) {
                Invoke-Compose -Arguments @(
                    'exec', '-T', 'kafka',
                    '/opt/kafka/bin/kafka-topics.sh',
                    '--bootstrap-server', 'localhost:9092',
                    '--create', '--if-not-exists',
                    '--topic', $topic,
                    '--partitions', '3',
                    '--replication-factor', '1'
                )
            }
        }
        'MinIO' {
            $buckets = Get-Content -LiteralPath 'deploy/minio/buckets.txt' |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ -and -not $_.StartsWith('#') }

            foreach ($bucket in $buckets) {
                Invoke-Compose -Arguments @(
                    'exec', '-T', 'minio',
                    'mc', 'mb', '--ignore-existing', "local/$bucket"
                )
            }
        }
    }
}
finally {
    Pop-Location
}
