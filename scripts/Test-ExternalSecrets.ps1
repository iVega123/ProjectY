[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$externalSecrets = @(
    'api-gateway-secrets', 'identity-secrets', 'rental-core-secrets', 'billing-secrets',
    'risk-pricing-secrets', 'telemetry-secrets', 'console-secrets', 'rabbitmq-secrets', 'minio-secrets'
)

& $kubectl wait --namespace projecty --for=condition=Ready secretstore/projecty-secrets --timeout=120s | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The ProjectY SecretStore did not become ready.' }

foreach ($name in $externalSecrets) {
    & $kubectl wait --namespace projecty --for=condition=Ready "externalsecret/$name" --timeout=120s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ExternalSecret $name did not become ready." }
    & $kubectl get secret $name --namespace projecty -o name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ExternalSecret $name did not create its target Secret." }
}

[ordered]@{secretStore = 'ready'; synchronizedTargets = $externalSecrets.Count} | ConvertTo-Json
