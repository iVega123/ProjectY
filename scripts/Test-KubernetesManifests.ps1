[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$expectedApplications = @(
    'api-gateway', 'identity', 'rental-core', 'media-guard',
    'billing', 'risk-pricing', 'telemetry', 'console'
)

Push-Location $root
try {
    foreach ($overlay in @('selfhost', 'aws')) {
        $rendered = (& $kubectl kustomize "deploy/overlays/$overlay") -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Kustomize failed for $overlay." }
        if ($rendered -match 'arn:aws') { throw "Cloud resource identifier leaked into $overlay workloads." }
        if ($rendered -match '(?m)^kind:\s+Secret\s*$') { throw "A plain Secret is committed in $overlay." }

        $documents = [regex]::Split($rendered, '(?m)^---\s*$')
        $workloads = @($documents | Where-Object { $_ -match '(?m)^kind:\s+(Deployment|StatefulSet|Job)\s*$' })
        foreach ($workload in $workloads) {
            $name = [regex]::Match($workload, '(?ms)^metadata:\s*\n\s+name:\s*([^\s]+)').Groups[1].Value
            if ($workload -notmatch '(?m)^\s+requests:\s*') { throw "$overlay/$name has no resource requests." }
            if ($workload -notmatch '(?m)^\s+limits:\s*') { throw "$overlay/$name has no resource limits." }
            if ($workload -notmatch 'runAsNonRoot:\s+true') { throw "$overlay/$name is not declared non-root." }
            if ($workload -notmatch 'allowPrivilegeEscalation:\s+false') { throw "$overlay/$name allows privilege escalation." }
        }

        foreach ($application in $expectedApplications) {
            $escaped = [regex]::Escape($application)
            if ($rendered -notmatch "(?ms)^kind: Deployment.*?^metadata:.*?name: $escaped(?:\s|$)") {
                throw "$overlay is missing the $application Deployment."
            }
            if ($rendered -notmatch "(?ms)^kind: Service.*?^metadata:.*?name: $escaped(?:\s|$)") {
                throw "$overlay is missing the $application Service."
            }
        }
        Write-Host "PASS: $overlay renders $($workloads.Count) bounded, non-root workloads"
    }
} finally {
    Pop-Location
}
