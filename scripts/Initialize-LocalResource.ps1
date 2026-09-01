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
    '-f', 'deploy/overlays/selfhost/compose.yaml'
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

function Get-OrderedMigration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    $migrations = @(
        Get-ChildItem -LiteralPath $Directory -File |
            Where-Object { $_.Extension -eq $Extension } |
            Sort-Object -Property Name
    )
    if ($migrations.Count -eq 0) {
        throw "No $Extension migrations were found in $Directory."
    }

    return $migrations
}

Push-Location $repositoryRoot
try {
    switch ($Resource) {
        'Cockroach' {
            # O bootstrap de outro engine vive no mesmo diretorio (o schema e
            # compartilhado de proposito) e nao deve ser aplicado aqui.
            $migrations = @(
                Get-OrderedMigration -Directory 'deploy/db/sql' -Extension '.sql' |
                    Where-Object { $_.Name -notlike '*.postgres.sql' }
            )
            foreach ($migration in $migrations) {
                Write-Host "Applying Cockroach migration $($migration.Name)..."
                # O bootstrap cria o banco, entao roda sem apontar para ele.
                $database = if ($migration.Name -like '000_bootstrap.*') { '' } else { ' --database=projecty' }
                Invoke-Compose -Arguments @(
                    'run', '--rm', '--no-deps', 'cockroach-init',
                    "cockroach sql --insecure --host=cockroachdb:26257$database --file=/sql/$($migration.Name)"
                )
            }
        }
        'Cassandra' {
            $migrations = @(Get-OrderedMigration -Directory 'deploy/db/cassandra' -Extension '.cql')
            foreach ($migration in $migrations) {
                Write-Host "Applying Cassandra migration $($migration.Name)..."
                Invoke-Compose -Arguments @(
                    'exec', '-T', 'cassandra',
                    'cqlsh', '-f', "/schema/$($migration.Name)"
                )
            }
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
