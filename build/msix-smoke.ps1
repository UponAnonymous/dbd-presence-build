<#
.SYNOPSIS
  Install smoke test for the test-signed development MSIX (FR-LIFE-01; the Microsoft Store route; docs/owner).
.DESCRIPTION
  Imports the test certificate into Cert:\LocalMachine\TrustedPeople, installs the package with Add-AppxPackage, checks that the package, its
  executable and its startup task (windows.startupTask, TaskId DbdPresenceStartup) are there, removes it with Remove-AppxPackage, checks that
  nothing is left (package, install folder, per-user package folder) and removes the certificate again.
  Runs only on a disposable, administrator Windows runner: it writes to the machine's certificate store and installs a package.
  The Appx cmdlets run in Windows PowerShell (powershell.exe), one bounded process per step, as install-smoke.ps1 does for the installer.
  The app is not started here (it needs a desktop); starting it and the startup task's effect are PENDING-ENV, checked by hand.
.PARAMETER MsixPath
  The test-signed .msix from build/pack-msix.ps1 -SignWithTestCertificate.
.PARAMETER CerPath
  The exported public certificate that signed it.
.PARAMETER IdentityName
  The package name used when packing.
.PARAMETER TimeoutSeconds
  Upper bound for each step.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $MsixPath,
  [Parameter(Mandatory)] [string] $CerPath,
  [Parameter(Mandatory)] [string] $IdentityName,
  [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$taskId = 'DbdPresenceStartup'
$work = Join-Path ([IO.Path]::GetTempPath()) ("dbdp-msix-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Fail([string] $message) {
  Write-Host "::error::MSIX smoke FAILED: $message"
  throw "MSIX smoke FAILED: $message"
}

# Runs one Windows PowerShell command line in its own process, bounded by the timeout, and returns what it printed.
function Invoke-Ps([string] $command, [string] $what) {
  Write-Host "$what"
  $out = Join-Path $work ("out-" + [guid]::NewGuid().ToString('N') + '.txt')
  $err = Join-Path $work ("err-" + [guid]::NewGuid().ToString('N') + '.txt')
  $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', ('"' + ($command -replace '"', '\"') + '"'))
  $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill($true)
    Fail "$what did not finish within $TimeoutSeconds s"
  }
  $text = (Get-Content -Raw $out -ErrorAction SilentlyContinue)
  if ($process.ExitCode -ne 0) { Fail "$what exited with code $($process.ExitCode): $(Get-Content -Raw $err -ErrorAction SilentlyContinue)" }
  if ($null -eq $text) { return '' }
  $text.Trim()
}

function Wait-Until([scriptblock] $condition, [string] $what) {
  $watch = [Diagnostics.Stopwatch]::StartNew()
  while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    if (& $condition) { return }
    Start-Sleep -Milliseconds 500
  }
  Fail "$what (waited $TimeoutSeconds s)"
}

if (-not (Test-Path $MsixPath)) { Fail "package not found at $MsixPath" }
if (-not (Test-Path $CerPath)) { Fail "certificate not found at $CerPath" }
$msix = (Resolve-Path $MsixPath).Path
$cer = (Resolve-Path $CerPath).Path
$thumbprint = $null

try {
  if ((Invoke-Ps "(Get-AppxPackage -Name '$IdentityName' | Measure-Object).Count" 'Check the runner is clean') -ne '0') { Fail "$IdentityName is already installed; the runner is not clean" }

  # 1. Trust the test certificate (the Publisher of the package), then install.
  $thumbprint = Invoke-Ps "(Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople).Thumbprint" 'Import the test certificate into TrustedPeople'
  if ($thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { Fail "the certificate import did not return a thumbprint (got '$thumbprint')" }
  Invoke-Ps "Add-AppxPackage -Path '$msix'" 'Install the package' | Out-Null

  # 2. The package, its executable and its startup task.
  $location = Invoke-Ps "(Get-AppxPackage -Name '$IdentityName').InstallLocation" 'Read the install location'
  if (-not $location) { Fail "Get-AppxPackage found no package named $IdentityName after Add-AppxPackage" }
  $family = Invoke-Ps "(Get-AppxPackage -Name '$IdentityName').PackageFamilyName" 'Read the package family name'
  $fullName = Invoke-Ps "(Get-AppxPackage -Name '$IdentityName').PackageFullName" 'Read the package full name'
  if (-not (Test-Path (Join-Path $location 'DBD-Presence.exe'))) { Fail "DBD-Presence.exe missing under $location" }
  if (-not (Test-Path (Join-Path $location 'DbdPresence.ImageHelper.exe'))) { Fail "DbdPresence.ImageHelper.exe missing under $location" }
  $categories = Invoke-Ps "(Get-AppxPackageManifest -Package '$fullName').Package.Applications.Application.Extensions.Extension.Category" 'Read the installed manifest extensions'
  if ($categories -notmatch 'windows\.startupTask') { Fail "the installed manifest declares no windows.startupTask extension (got '$categories')" }
  $tasks = Invoke-Ps "(Get-AppxPackageManifest -Package '$fullName').Package.Applications.Application.Extensions.Extension.StartupTask.TaskId" 'Read the startup task id'
  if ($tasks -ne $taskId) { Fail "expected the startup task $taskId, the installed manifest declares '$tasks'" }
  Write-Host "Installed: $fullName at $location"

  # 3. Remove it, then nothing may be left.
  Invoke-Ps "Remove-AppxPackage -Package '$fullName'" 'Remove the package' | Out-Null
  Wait-Until { (Invoke-Ps "(Get-AppxPackage -Name '$IdentityName' | Measure-Object).Count" 'Count packages') -eq '0' } 'the package is still registered after Remove-AppxPackage'
  Wait-Until { -not (Test-Path $location) } "install folder $location still exists after removal"
  $packageData = Join-Path $env:LOCALAPPDATA "Packages\$family"
  Wait-Until { -not (Test-Path $packageData) } "per-user package folder $packageData still exists after removal"
}
finally {
  if ($thumbprint) {
    Remove-Item -Path "Cert:\LocalMachine\TrustedPeople\$thumbprint" -Force -ErrorAction SilentlyContinue
  }
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

Write-Host 'MSIX smoke OK: certificate trusted, package installed, executable and startup task present, removed, no residue.'
