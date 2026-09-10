[CmdletBinding()]
param(
    [string]$HttpsBase = 'https://localhost:8443',
    [string]$HttpBase = 'http://localhost:8080',
    # CI has no application images in the cluster, so it stands two busybox
    # listeners in for the console and the gateway -- the same fixture pattern
    # the NetworkPolicy acceptance uses. What is proven is the edge: the chain,
    # the SAN, the redirect and the refusal of an untrusted client. Run without
    # this switch under `tilt up` to prove the real console and gateway answer.
    [switch]$UseFixtureBackends
)

$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl

if (-not (Get-Command curl -ErrorAction SilentlyContinue) -and -not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw 'curl is required to verify the certificate chain against an explicit authority.'
}

$fixtures = @"
apiVersion: v1
kind: Service
metadata: {name: console, namespace: projecty}
spec:
  selector: {app.kubernetes.io/name: console}
  ports: [{name: http, port: 3001, targetPort: 3001}]
---
apiVersion: v1
kind: Pod
metadata:
  name: console-tls-fixture
  namespace: projecty
  labels: {app.kubernetes.io/name: console, projecty.io/tier: edge}
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext: {runAsNonRoot: true, runAsUser: 65532, seccompProfile: {type: RuntimeDefault}}
  volumes: [{name: root, emptyDir: {}}]
  containers:
    - name: listener
      image: busybox:1.37.0
      command: [sh, -c, 'echo ok > /www/index.html && httpd -f -p 3001 -h /www']
      volumeMounts: [{name: root, mountPath: /www}]
      securityContext: {allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}, readOnlyRootFilesystem: true}
      resources: {requests: {cpu: 5m, memory: 8Mi}, limits: {cpu: 50m, memory: 32Mi}}
---
apiVersion: v1
kind: Service
metadata: {name: api-gateway, namespace: projecty}
spec:
  selector: {app.kubernetes.io/name: api-gateway}
  ports: [{name: http, port: 8090, targetPort: 8090}]
---
apiVersion: v1
kind: Pod
metadata:
  name: api-gateway-tls-fixture
  namespace: projecty
  labels: {app.kubernetes.io/name: api-gateway, projecty.io/tier: edge}
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext: {runAsNonRoot: true, runAsUser: 65532, seccompProfile: {type: RuntimeDefault}}
  volumes: [{name: root, emptyDir: {}}]
  containers:
    - name: listener
      image: busybox:1.37.0
      command: [sh, -c, 'mkdir -p /www/health && echo ok > /www/health/ready && httpd -f -p 8090 -h /www']
      volumeMounts: [{name: root, mountPath: /www}]
      securityContext: {allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}, readOnlyRootFilesystem: true}
      resources: {requests: {cpu: 5m, memory: 8Mi}, limits: {cpu: 50m, memory: 32Mi}}
"@

function Invoke-Probe {
    param(
        [Parameter(Mandatory)] [string]$Url,
        [string[]]$ExtraArguments = @()
    )

    $body = [System.IO.Path]::GetTempFileName()
    try {
        $arguments = @('--silent', '--show-error', '--max-time', '20', '--output', $body, '--write-out', '%{http_code}') + $ExtraArguments + @($Url)
        $status = (& curl @arguments 2>&1 | Out-String).Trim()
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Status = $status }
    } finally {
        Remove-Item -LiteralPath $body -Force -ErrorAction SilentlyContinue
    }
}

# The authority, not the leaf. Trusting the served certificate would prove only
# that the ingress serves something; trusting the CA proves the leaf was issued
# from the authority this cluster created.
$encodedCa = & $kubectl get secret projecty-local-ca --namespace cert-manager -o jsonpath='{.data.ca\.crt}'
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($encodedCa)) {
    throw 'Could not read the local certificate authority.'
}
$caFile = Join-Path ([System.IO.Path]::GetTempPath()) "projecty-local-ca-$PID.crt"
[System.IO.File]::WriteAllBytes($caFile, [System.Convert]::FromBase64String($encodedCa))
$emptyAuthority = Join-Path ([System.IO.Path]::GetTempPath()) "projecty-empty-authority-$PID.crt"
Set-Content -LiteralPath $emptyAuthority -Value '' -NoNewline

try {
    if ($UseFixtureBackends) {
        $fixtures | & $kubectl apply -f - | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the TLS edge fixtures.' }
        & $kubectl wait --namespace projecty --for=condition=Ready pod/console-tls-fixture pod/api-gateway-tls-fixture --timeout=120s | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The TLS edge fixtures did not become ready.' }
    }

    # No --insecure and no --resolve override: the SAN has to actually cover
    # what a visitor types, and the chain has to reach the local authority.
    $console = Invoke-Probe -Url "$HttpsBase/" -ExtraArguments @('--cacert', $caFile)
    if ($console.ExitCode -ne 0) { throw "The console is not reachable over TLS: curl exited $($console.ExitCode)." }
    if ($console.Status -ne '200') { throw "The console answered $($console.Status) over TLS." }

    $gateway = Invoke-Probe -Url "$HttpsBase/health/ready" -ExtraArguments @('--cacert', $caFile)
    if ($gateway.ExitCode -ne 0) { throw "The gateway is not reachable over TLS: curl exited $($gateway.ExitCode)." }
    if ($gateway.Status -ne '200') { throw "The gateway readiness endpoint answered $($gateway.Status) over TLS." }

    # An untrusted client must fail. If it succeeded, the two probes above would
    # have proven nothing about the chain.
    $untrusted = Invoke-Probe -Url "$HttpsBase/" -ExtraArguments @('--cacert', $emptyAuthority)
    if ($untrusted.ExitCode -eq 0) { throw 'The ingress certificate verified against an empty authority.' }

    # The redirect is what replaced UseHttpsRedirection, so it is verified where
    # it now lives instead of being trusted to an annotation.
    $redirect = Invoke-Probe -Url "$HttpBase/"
    if ($redirect.Status -notin @('301', '308')) {
        throw "Plain HTTP answered $($redirect.Status) instead of redirecting to TLS."
    }

    [ordered]@{
        observedAt = [DateTimeOffset]::UtcNow.ToString('o')
        authority = 'cert-manager/projecty-local-ca'
        backends = if ($UseFixtureBackends) { 'busybox fixtures standing in for console and gateway' } else { 'the running console and gateway' }
        console = "$HttpsBase/ verified against the local authority, 200"
        gateway = "$HttpsBase/health/ready verified against the local authority, 200"
        untrustedClient = 'rejected'
        plainHttp = "$($redirect.Status) redirect to TLS"
    } | ConvertTo-Json
} finally {
    if ($UseFixtureBackends) {
        $fixtures | & $kubectl delete -f - --ignore-not-found --wait=false | Out-Null
    }
    Remove-Item -LiteralPath $caFile, $emptyAuthority -Force -ErrorAction SilentlyContinue
}
