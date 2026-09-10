[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot 'kind\Install-ProjectYKubernetesTools.ps1')
$kubectl = $tools.Kubectl
$probeName = 'network-policy-probe'

$fixtures = @"
apiVersion: v1
kind: Service
metadata: {name: cockroachdb, namespace: projecty}
spec:
  selector: {app.kubernetes.io/name: cockroachdb}
  ports: [{name: sql, port: 26257, targetPort: 26257}]
---
apiVersion: v1
kind: Pod
metadata:
  name: cockroachdb-policy-fixture
  namespace: projecty
  labels: {app.kubernetes.io/name: cockroachdb, projecty.io/tier: data}
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext: {runAsNonRoot: true, runAsUser: 65532, seccompProfile: {type: RuntimeDefault}}
  containers:
    - name: listener
      image: busybox:1.37.0
      command: [sh, -c, 'while true; do nc -l -p 26257; done']
      securityContext: {allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}}
      resources: {requests: {cpu: 5m, memory: 8Mi}, limits: {cpu: 50m, memory: 32Mi}}
---
apiVersion: v1
kind: Service
metadata: {name: rabbitmq, namespace: projecty}
spec:
  selector: {app.kubernetes.io/name: rabbitmq}
  ports: [{name: amqp, port: 5672, targetPort: 5672}]
---
apiVersion: v1
kind: Pod
metadata:
  name: rabbitmq-policy-fixture
  namespace: projecty
  labels: {app.kubernetes.io/name: rabbitmq, projecty.io/tier: data}
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext: {runAsNonRoot: true, runAsUser: 65532, seccompProfile: {type: RuntimeDefault}}
  containers:
    - name: listener
      image: busybox:1.37.0
      command: [sh, -c, 'while true; do nc -l -p 5672; done']
      securityContext: {allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}}
      resources: {requests: {cpu: 5m, memory: 8Mi}, limits: {cpu: 50m, memory: 32Mi}}
"@

$probe = @"
apiVersion: v1
kind: Pod
metadata:
  name: $probeName
  namespace: projecty
  labels:
    app.kubernetes.io/name: identity
spec:
  restartPolicy: Never
  automountServiceAccountToken: false
  securityContext:
    runAsNonRoot: true
    runAsUser: 65532
    seccompProfile: {type: RuntimeDefault}
  containers:
    - name: probe
      image: busybox:1.37.0
      command: [sh, -c, 'sleep 600']
      securityContext:
        allowPrivilegeEscalation: false
        capabilities: {drop: [ALL]}
      resources:
        requests: {cpu: 5m, memory: 8Mi}
        limits: {cpu: 50m, memory: 32Mi}
"@

$rootPod = @"
apiVersion: v1
kind: Pod
metadata:
  name: forbidden-root-probe
  namespace: projecty
spec:
  automountServiceAccountToken: false
  securityContext:
    runAsNonRoot: true
    seccompProfile: {type: RuntimeDefault}
  containers:
    - name: probe
      image: busybox:1.37.0
      securityContext: {runAsUser: 0, allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}}
"@

# These probes are meant to be rejected, and a rejection arrives on stderr.
#
# On Windows PowerShell 5.1, redirecting a native command's stderr wraps every
# line in a NativeCommandError, and with $ErrorActionPreference = 'Stop' that
# terminates the script before the exit code can be read -- so the expected
# rejection killed the run instead of satisfying it. CI never saw this, because
# CI runs pwsh 7 on Linux.
function Invoke-Rejection {
    param([Parameter(Mandatory)] [scriptblock]$Probe)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Probe 2>&1
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Text = (@($output | ForEach-Object { $_.ToString() })) -join "`n"
        }
    } finally {
        $ErrorActionPreference = $previous
    }
}

try {
    $root = Invoke-Rejection -Probe { $rootPod | & $kubectl apply --server-side --dry-run=server -f - }
    if ($root.ExitCode -eq 0) { throw 'Pod Security admitted a container explicitly running as root.' }
    if ($root.Text -notmatch 'PodSecurity|runAsUser|non-root') {
        throw "The root probe failed for an unrelated reason: $($root.Text)"
    }

    $fixtures | & $kubectl apply -f - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the NetworkPolicy fixtures.' }
    & $kubectl wait --namespace projecty --for=condition=Ready pod/cockroachdb-policy-fixture pod/rabbitmq-policy-fixture --timeout=90s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The NetworkPolicy fixtures did not become ready.' }

    $probe | & $kubectl apply -f - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the network-policy probe.' }
    & $kubectl wait --namespace projecty --for=condition=Ready "pod/$probeName" --timeout=90s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The network-policy probe did not become ready.' }

    & $kubectl exec --namespace projecty $probeName -- nc -z -w 5 cockroachdb 26257 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Identity could not reach its owned CockroachDB dependency.' }

    $foreign = Invoke-Rejection -Probe { & $kubectl exec --namespace projecty $probeName -- nc -z -w 5 rabbitmq 5672 }
    if ($foreign.ExitCode -eq 0) { throw 'Identity unexpectedly reached Rental Core owned RabbitMQ.' }

    [ordered]@{
        podSecurityRestricted = 'root pod rejected'
        ownedDependency = 'cockroachdb:26257 reachable'
        foreignDependency = 'rabbitmq:5672 denied'
    } | ConvertTo-Json
} finally {
    & $kubectl delete pod $probeName --namespace projecty --ignore-not-found --wait=false | Out-Null
    $fixtures | & $kubectl delete -f - --ignore-not-found --wait=false | Out-Null
}
