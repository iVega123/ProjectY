[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolDirectory = Join-Path $root '.tools\kubernetes'
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

if ($architecture -ne 'x64') {
    throw "The ProjectY bootstrap currently pins amd64 binaries; detected $architecture."
}

New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null

$kindVersion = 'v0.33.0'
$kindFile = if ($isWindowsHost) { 'kind-windows-amd64' } else { 'kind-linux-amd64' }
$kindExpected = if ($isWindowsHost) {
    '4b22adaa135368c5a465d56bbd8e520cbea87272a06ca00b6078e7b81515c9fc'
} else {
    'aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d'
}
$kindName = if ($isWindowsHost) { 'kind.exe' } else { 'kind' }
$kindPath = Join-Path $toolDirectory $kindName

if (-not (Test-Path -LiteralPath $kindPath) -or
    (Get-FileHash -LiteralPath $kindPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $kindExpected) {
    Invoke-WebRequest -Uri "https://github.com/kubernetes-sigs/kind/releases/download/$kindVersion/$kindFile" -OutFile $kindPath
    $actual = (Get-FileHash -LiteralPath $kindPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $kindExpected) {
        Remove-Item -LiteralPath $kindPath -Force
        throw "kind checksum mismatch: expected $kindExpected, got $actual"
    }
}

$kubectlVersion = 'v1.37.0'
$platform = if ($isWindowsHost) { 'windows' } else { 'linux' }
$kubectlName = if ($isWindowsHost) { 'kubectl.exe' } else { 'kubectl' }
$kubectlPath = Join-Path $toolDirectory $kubectlName
$kubectlUrl = "https://dl.k8s.io/release/$kubectlVersion/bin/$platform/amd64/$kubectlName"
$checksumPath = Join-Path $toolDirectory "$kubectlName.sha256"

if (-not (Test-Path -LiteralPath $kubectlPath)) {
    Invoke-WebRequest -Uri $kubectlUrl -OutFile $kubectlPath
}
Invoke-WebRequest -Uri "$kubectlUrl.sha256" -OutFile $checksumPath
$expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim().ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $kubectlPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $kubectlPath -Force
    throw "kubectl checksum mismatch: expected $expected, got $actual"
}

if (-not $isWindowsHost) {
    & chmod +x $kindPath $kubectlPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not make kind and kubectl executable.' }
}

[pscustomobject]@{
    Kind = $kindPath
    Kubectl = $kubectlPath
}
