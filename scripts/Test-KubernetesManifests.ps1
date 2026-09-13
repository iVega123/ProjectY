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
        $operatorManagedWorkloads = @($documents | Where-Object { $_ -match '(?m)^kind:\s+Kafka\s*$' })
        $logicalWorkloadCount = $workloads.Count + $operatorManagedWorkloads.Count
        if ($logicalWorkloadCount -ne 19) {
            throw "$overlay renders $logicalWorkloadCount logical workloads; expected 19."
        }
        if ($overlay -eq 'aws' -and
            ($operatorManagedWorkloads.Count -ne 1 -or
             $rendered -notmatch '(?m)^\s+min\.insync\.replicas:\s+2\s*$' -or
             $rendered -notmatch '(?m)^\s+default\.replication\.factor:\s+3\s*$')) {
            throw 'aws must replace the base Kafka StatefulSet with one replicated Strimzi Kafka resource.'
        }
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

        # A4: the edge is the only place that terminates TLS, and each overlay
        # has to terminate it in its own way -- cert-manager fills a Secret on
        # kind, ACM sits in front of the load balancer on AWS. An overlay that
        # carries neither is serving the stack in clear.
        $ingress = @($documents | Where-Object { $_ -match '(?m)^kind:\s+Ingress\s*$' })
        if ($ingress.Count -ne 1) { throw "$overlay renders $($ingress.Count) Ingress objects; expected 1." }
        # A `tls` block without hosts is ignored by ingress-nginx, and the
        # hostnames cannot be shared between overlays -- so neither overlay is
        # allowed to declare one. Each environment names its certificate in its
        # own platform layer instead, and this asserts both halves.
        if ($ingress[0] -match '(?m)^\s+tls:\s*$') {
            throw "$overlay declares a tls block; a hostless one is ignored and a hosted one is not portable."
        }
        if ($overlay -eq 'selfhost') {
            if ($ingress[0] -notmatch 'nginx\.ingress\.kubernetes\.io/force-ssl-redirect:\s*"true"') {
                throw 'selfhost does not redirect plain HTTP to TLS at the ingress.'
            }
            $authority = (& $kubectl kustomize 'deploy/platform/cert-manager') -join "`n"
            if ($LASTEXITCODE -ne 0) { throw 'Kustomize failed for the local certificate authority.' }
            if ($authority -notmatch '(?m)^\s+secretName:\s+projecty-tls\s*$') {
                throw 'The local authority does not issue the projecty-tls serving certificate.'
            }
            if ($authority -notmatch '(?m)^\s+isCA:\s+true\s*$') {
                throw 'The local authority does not issue from a CA of its own.'
            }
        }
        if ($overlay -eq 'aws') {
            if ($ingress[0] -notmatch 'alb\.ingress\.kubernetes\.io/certificate-arn:') {
                throw 'aws does not name a certificate at the load balancer.'
            }
            if ($ingress[0] -match 'nginx\.ingress\.kubernetes\.io/') {
                throw 'aws mixes nginx annotations into an ALB Ingress.'
            }
        }
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
        Write-Host "PASS: $overlay renders $logicalWorkloadCount bounded, isolated logical workloads with external secrets"
    }
} finally {
    Pop-Location
}
