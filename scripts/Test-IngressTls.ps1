[CmdletBinding()]
param(
    [string]$HttpsHost = 'localhost',
    [int]$HttpsPort = 8443,
    [int]$HttpPort = 8080,
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

# The chain is validated in .NET rather than by curl.
#
# curl on Windows is built against schannel, which ignores --cacert: it can only
# trust what the machine already trusts, so it answers 60 for a certificate from
# an authority that is deliberately in no trust store. Pinning the chain to our
# own CA is the entire point of this test, so it is done here, the same way on
# every platform the runbook names.
function Invoke-TlsRequest {
    param(
        [Parameter(Mandatory)] [string]$TargetHost,
        [Parameter(Mandatory)] [int]$Port,
        [Parameter(Mandatory)] [string]$Path,
        # $null means "trust nothing extra", which must fail.
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Authority
    )

    $script:nameMatched = $false
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.ReceiveTimeout = 20000
        $client.SendTimeout = 20000
        $client.Connect($TargetHost, $Port)

        $validation = [System.Net.Security.RemoteCertificateValidationCallback] {
            param($senderObject, $certificate, $chain, $sslPolicyErrors)

            # A hostname mismatch is a failure of the SAN, not of the chain, so
            # it is recorded separately instead of folded into one bit.
            $script:nameMatched = -not $sslPolicyErrors.HasFlag([System.Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch)

            if (-not $Authority) { return $false }

            $leaf = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certificate
            $verifier = New-Object System.Security.Cryptography.X509Certificates.X509Chain
            $verifier.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            # The authority is offered as an extra root, and the chain that gets
            # built is then required to actually end at it. Allowing an unknown
            # CA without checking where the chain landed would trust anything.
            $verifier.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::AllowUnknownCertificateAuthority
            $verifier.ChainPolicy.ExtraStore.Add($Authority) | Out-Null
            if (-not $verifier.Build($leaf)) { return $false }
            $root = $verifier.ChainElements[$verifier.ChainElements.Count - 1].Certificate
            return $root.Thumbprint -eq $Authority.Thumbprint
        }

        $ssl = New-Object System.Net.Security.SslStream $client.GetStream(), $false, $validation
        try {
            $ssl.AuthenticateAsClient($TargetHost, $null, [System.Security.Authentication.SslProtocols]::Tls12, $false)
            $request = "GET $Path HTTP/1.1`r`nHost: ${TargetHost}:${Port}`r`nConnection: close`r`nAccept: */*`r`n`r`n"
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
            $ssl.Write($bytes, 0, $bytes.Length)
            $ssl.Flush()
            $reader = New-Object System.IO.StreamReader $ssl
            $statusLine = $reader.ReadLine()
            [pscustomobject]@{
                Handshake = 'verified'
                NameMatched = $script:nameMatched
                Status = ($statusLine -split ' ')[1]
            }
        } finally {
            $ssl.Dispose()
        }
    } catch {
        [pscustomobject]@{
            Handshake = 'refused'
            NameMatched = $script:nameMatched
            Status = $null
            Reason = $_.Exception.Message
        }
    } finally {
        $client.Close()
    }
}

# The authority, not the leaf. Trusting the served certificate would prove only
# that the ingress serves something; pinning the CA proves the leaf was issued
# from the authority this cluster created.
$encodedCa = & $kubectl get secret projecty-local-ca --namespace cert-manager -o jsonpath='{.data.ca\.crt}'
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($encodedCa)) {
    throw 'Could not read the local certificate authority.'
}
$authority = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (, [System.Convert]::FromBase64String($encodedCa))

try {
    if ($UseFixtureBackends) {
        # A previous run tears the fixtures down without waiting, so a rerun can
        # find a pod still terminating. `apply` then reports it unchanged and
        # `wait` is satisfied by the dying pod, which serves nothing -- a 503
        # that looks like a wiring bug. Delete and wait for it to be gone first.
        $fixtures | & $kubectl delete -f - --ignore-not-found --wait --timeout=90s | Out-Null

        $fixtures | & $kubectl apply -f - | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the TLS edge fixtures.' }
        & $kubectl wait --namespace projecty --for=condition=Ready pod/console-tls-fixture pod/api-gateway-tls-fixture --timeout=120s | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The TLS edge fixtures did not become ready.' }

        # A ready pod is not a reloaded ingress. The controller has to observe
        # each new EndpointSlice and reload before it stops answering 503, and
        # nothing in `kubectl wait` covers that -- so the edge is polled until
        # it converges, and only then does the run start asserting.
        #
        # Both paths, not just the first: the two fixtures land in the
        # controller's view independently, so waiting only on `/` left
        # `/health/ready` still answering 503 when the assertions began.
        $paths = @('/', '/health/ready')
        $pending = $paths
        for ($attempt = 1; $attempt -le 30 -and $pending.Count -gt 0; $attempt++) {
            if ($attempt -gt 1) { Start-Sleep -Seconds 2 }
            $pending = @($pending | Where-Object {
                $probe = Invoke-TlsRequest -TargetHost $HttpsHost -Port $HttpsPort -Path $_ -Authority $authority
                -not ($probe.Handshake -eq 'verified' -and $probe.Status -eq '200')
            })
        }
        if ($pending.Count -gt 0) {
            throw "The ingress did not start serving the TLS edge fixtures at: $($pending -join ', ')"
        }
    }

    $console = Invoke-TlsRequest -TargetHost $HttpsHost -Port $HttpsPort -Path '/' -Authority $authority
    if ($console.Handshake -ne 'verified') { throw "The console certificate did not verify: $($console.Reason)" }
    if (-not $console.NameMatched) { throw "The certificate does not cover $HttpsHost." }
    if ($console.Status -ne '200') { throw "The console answered $($console.Status) over TLS." }

    $gateway = Invoke-TlsRequest -TargetHost $HttpsHost -Port $HttpsPort -Path '/health/ready' -Authority $authority
    if ($gateway.Handshake -ne 'verified') { throw "The gateway certificate did not verify: $($gateway.Reason)" }
    if ($gateway.Status -ne '200') { throw "The gateway readiness endpoint answered $($gateway.Status) over TLS." }

    # A client trusting nothing extra must fail. If it succeeded, the two probes
    # above would have proven nothing about the chain.
    $untrusted = Invoke-TlsRequest -TargetHost $HttpsHost -Port $HttpsPort -Path '/' -Authority $null
    if ($untrusted.Handshake -eq 'verified') { throw 'The ingress certificate verified with no authority at all.' }

    # The redirect is what replaced UseHttpsRedirection, so it is verified where
    # it now lives instead of being trusted to an annotation.
    $plain = [System.Net.HttpWebRequest]::Create("http://${HttpsHost}:${HttpPort}/")
    $plain.AllowAutoRedirect = $false
    $plain.Timeout = 20000
    try {
        $response = $plain.GetResponse()
        $redirectStatus = [int]$response.StatusCode
        $response.Close()
    } catch [System.Net.WebException] {
        if (-not $_.Exception.Response) { throw }
        $redirectStatus = [int]$_.Exception.Response.StatusCode
    }
    if ($redirectStatus -notin @(301, 308)) {
        throw "Plain HTTP answered $redirectStatus instead of redirecting to TLS."
    }

    [ordered]@{
        observedAt = [DateTimeOffset]::UtcNow.ToString('o')
        authority = "cert-manager/projecty-local-ca $($authority.Thumbprint)"
        backends = if ($UseFixtureBackends) { 'busybox fixtures standing in for console and gateway' } else { 'the running console and gateway' }
        console = "https://${HttpsHost}:${HttpsPort}/ chained to the local authority, name matched, 200"
        gateway = "https://${HttpsHost}:${HttpsPort}/health/ready chained to the local authority, 200"
        untrustedClient = 'rejected'
        plainHttp = "$redirectStatus redirect to TLS"
    } | ConvertTo-Json
} finally {
    if ($UseFixtureBackends) {
        $fixtures | & $kubectl delete -f - --ignore-not-found --wait=false | Out-Null
    }
}
