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
if ($current -notlike "*default-ssl-certificate*") {
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

# The Secret already exists at this point, so the restart comes up serving it
# instead of the controller's own self-signed placeholder.
& $kubectl rollout restart deployment/ingress-nginx-controller --namespace ingress-nginx | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restart the ingress controller.' }
& $kubectl rollout status deployment/ingress-nginx-controller --namespace ingress-nginx --timeout=240s
if ($LASTEXITCODE -ne 0) { throw 'The ingress controller did not come back after the restart.' }

Write-Host "cert-manager $chartVersion ready; the ingress serves projecty-tls from the local authority."
