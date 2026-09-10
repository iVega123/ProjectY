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
        if ($workloads.Count -ne 19) { throw "$overlay renders $($workloads.Count) workloads; expected 19." }
        $serviceAccounts = @($documents | Where-Object { $_ -match '(?m)^kind:\s+ServiceAccount\s*$' } | ForEach-Object {
            [regex]::Match($_, '(?m)^  name:\s*([^\s]+)').Groups[1].Value
        })
        foreach ($workload in $workloads) {
            $name = [regex]::Match($workload, '(?m)^  name:\s*([^\s]+)').Groups[1].Value
            if ($workload -notmatch '(?m)^\s+requests:\s*') { throw "$overlay/$name has no resource requests." }
            if ($workload -notmatch '(?m)^\s+limits:\s*') { throw "$overlay/$name has no resource limits." }
            if ($workload -notmatch 'runAsNonRoot:\s+true') { throw "$overlay/$name is not declared non-root." }
            if ($workload -notmatch 'allowPrivilegeEscalation:\s+false') { throw "$overlay/$name allows privilege escalation." }
            if ($workload -notmatch '(?ms)capabilities:.*?drop:\s*(?:\[ALL\]|\n\s*-\s*ALL)') { throw "$overlay/$name does not drop all Linux capabilities." }
            if ($workload -notmatch 'automountServiceAccountToken:\s+false') { throw "$overlay/$name mounts a service-account token by default." }
            $serviceAccount = [regex]::Match($workload, 'serviceAccountName:\s*([^\s]+)').Groups[1].Value
            if ([string]::IsNullOrWhiteSpace($serviceAccount) -or $serviceAccounts -notcontains $serviceAccount) {
                throw "$overlay/$name does not reference a declared ServiceAccount."
            }
        }

        $networkPolicies = @($documents | Where-Object { $_ -match '(?m)^kind:\s+NetworkPolicy\s*$' })
        $externalSecrets = @($documents | Where-Object { $_ -match '(?m)^kind:\s+ExternalSecret\s*$' })
        if ($networkPolicies.Count -lt 18 -or $rendered -notmatch '(?m)^\s+name:\s+default-deny\s*$') {
            throw "$overlay does not carry the default-deny and ownership NetworkPolicies."
        }
        if ($externalSecrets.Count -ne 9) { throw "$overlay renders $($externalSecrets.Count) ExternalSecrets; expected 9." }
        if ($rendered -match 'secretRef:\s*\{name:\s*projecty-runtime\}') { throw "$overlay still uses the former shared runtime Secret." }
        if ($rendered -notmatch 'pod-security\.kubernetes\.io/enforce:\s+restricted') { throw "$overlay does not enforce restricted Pod Security." }
        if ($overlay -eq 'selfhost' -and $rendered -notmatch '(?ms)^kind:\s+SecretStore.*?provider:\s*\n\s+kubernetes:') {
            throw 'selfhost does not use the Kubernetes External Secrets provider.'
        }
        if ($overlay -eq 'aws' -and $rendered -notmatch '(?ms)^kind:\s+SecretStore.*?provider:\s*\n\s+aws:') {
            throw 'aws does not use the AWS External Secrets provider.'
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
        Write-Host "PASS: $overlay renders $($workloads.Count) bounded, isolated workloads with external secrets"
    }
} finally {
    Pop-Location
}
