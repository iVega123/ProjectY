[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = & (Join-Path $PSScriptRoot 'Install-ProjectYKubernetesTools.ps1')
$helm = $tools.Helm
$kubectl = $tools.Kubectl
$chartVersion = 'v1.21.1'

& $helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager --version $chartVersion --namespace cert-manager --create-namespace --set crds.enabled=true --wait --timeout 5m
if ($LASTEXITCODE -ne 0) { throw "Could not install cert-manager $chartVersion." }

foreach ($deployment in @('cert-manager', 'cert-manager-webhook', 'cert-manager-cainjector')) {
    & $kubectl wait --namespace cert-manager --for=condition=Available "deployment/$deployment" --timeout=240s
    if ($LASTEXITCODE -ne 0) { throw "$deployment did not become available." }
}

# The webhook reports Available before it serves; applying an Issuer against a
# webhook that is not yet answering fails with a connection refused that says
# nothing about the manifest. Retry instead of failing the first start.
$authority = Join-Path $root 'deploy\platform\cert-manager'
$applied = $false
for ($attempt = 1; $attempt -le 12; $attempt++) {
    & $kubectl apply -k $authority | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $applied = $true
        break
    }
    if ($attempt -lt 12) { Start-Sleep -Seconds 5 }
}
if (-not $applied) { throw 'Could not apply the ProjectY local certificate authority.' }

& $kubectl wait --namespace cert-manager --for=condition=Ready certificate/projecty-local-ca --timeout=180s
if ($LASTEXITCODE -ne 0) { throw 'The local certificate authority did not become ready.' }
& $kubectl wait --namespace projecty --for=condition=Ready certificate/projecty-tls --timeout=180s
if ($LASTEXITCODE -ne 0) { throw 'The ingress serving certificate did not become ready.' }

# The Ingress carries no `tls` block, because one without hosts is ignored and
# the hostnames are not shareable with the AWS overlay. So the certificate is
# named here, on the controller: --default-ssl-certificate makes projecty-tls
# the certificate served for every request that reaches this cluster's edge.
# It is the local counterpart of the ACM annotation. See ADR 0025.
$flag = '--default-ssl-certificate=projecty/projecty-tls'
$current = & $kubectl get deployment ingress-nginx-controller --namespace ingress-nginx -o jsonpath='{.spec.template.spec.containers[0].args}'
if ($LASTEXITCODE -ne 0) { throw 'Could not read the ingress controller arguments.' }
$patched = $false
if ($current -notlike "*default-ssl-certificate*") {
    $patched = $true
    # Through a file, not through -p: a JSON patch on the command line loses its
    # quoting on the way to a native executable on Windows, and kubectl answers
    # with "the request is invalid" that names nothing.
    $patchFile = Join-Path ([System.IO.Path]::GetTempPath()) "projecty-ingress-patch-$PID.json"
    $patch = @(
        [ordered]@{
            op = 'add'
            path = '/spec/template/spec/containers/0/args/-'
            value = $flag
        }
    )
    try {
        Set-Content -LiteralPath $patchFile -Value (ConvertTo-Json -InputObject $patch -Depth 5) -Encoding utf8
        & $kubectl patch deployment ingress-nginx-controller --namespace ingress-nginx --type=json --patch-file $patchFile | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not point the ingress controller at the local certificate.' }
    } finally {
        Remove-Item -LiteralPath $patchFile -Force -ErrorAction SilentlyContinue
    }
}

# Patching the args already rolls the Deployment, so a restart on top of it
# would take the controller down twice. The explicit restart is only for the
# already-patched case, where the Secret may have been reissued under a
# controller that has been running since before it existed.
if (-not $patched) {
    & $kubectl rollout restart deployment/ingress-nginx-controller --namespace ingress-nginx | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restart the ingress controller.' }
}
& $kubectl rollout status deployment/ingress-nginx-controller --namespace ingress-nginx --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'The ingress controller did not come back after the restart.' }

# Rolling the controller takes its admission webhook down with it, and
# `rollout status` returning is not permission to create an Ingress: the
# readiness probe answers on /healthz:10254 while the webhook listens on 8443,
# so there is a window where the pod is Ready and the webhook refuses the
# connection. Whatever applied an Ingress next got "connection refused" from
# validate.nginx.ingress.kubernetes.io and never created it.
#
# Pod readiness therefore cannot be the signal -- the only honest check is to
# put a request through the webhook. A server-side dry run does exactly that
# and persists nothing. Same shape as the Kyverno startup race already handled
# in Install-Kyverno.ps1, and the wait lives with the component that caused the
# outage instead of with every caller.
# The canary needs a host and a path of its own. nginx rejects a second Ingress
# that claims a host and path another one already has, so a canary on `/` is
# admitted on a fresh cluster and denied on every later run -- the webhook
# answering, read as the webhook being down.
$canary = @"
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata: {name: projecty-admission-canary, namespace: ingress-nginx}
spec:
  ingressClassName: nginx
  rules:
    - host: projecty-admission-canary.invalid
      http:
        paths:
          - path: /projecty-admission-canary
            pathType: Prefix
            backend:
              service: {name: projecty-admission-canary, port: {number: 80}}
"@

$admits = $false
for ($attempt = 1; $attempt -le 60; $attempt++) {
    # The dry run is expected to fail while the webhook is down, and a native
    # command's stderr terminates the script under Windows PowerShell 5.1, so
    # the preference is relaxed around it.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $answer = ($canary | & $kubectl apply --dry-run=server -f - 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    # Any answer proves the webhook is serving, including a denial. Only a
    # failure to reach it is worth another attempt -- that is the distinction
    # the first version of this wait did not make.
    if ($exitCode -eq 0 -or $answer -notmatch 'failed calling webhook|connection refused|no endpoints available') {
        $admits = $true
        break
    }
    Start-Sleep -Seconds 2
}
if (-not $admits) { throw 'The ingress admission webhook did not start answering.' }

Write-Host "cert-manager $chartVersion ready; the ingress serves projecty-tls from the local authority."
