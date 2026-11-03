<#
.SYNOPSIS
  Packs the self-contained publish folder into an MSIX package for the Microsoft Store (FR-LIFE-01; docs/owner, Store route).
.DESCRIPTION
  Lays out the publish folder, build/msix/AppxManifest.xml (identity filled in) and build/msix/Assets, then runs makeappx.exe pack from the
  Windows SDK. The result is UNSIGNED: the Store signs the package it publishes, so friends see no SmartScreen warning and Windows uninstalls
  it cleanly. The three identity values come from Partner Center (the owner supplies them; the release workflow reads them from repository
  variables). The version is Directory.Build.props mapped to X.Y.Z.0 by build/msix_version.py.
  -SignWithTestCertificate is for CI install tests ONLY: it makes a throw-away self-signed code-signing certificate whose subject equals the
  Publisher, signs with signtool (/fd SHA256) and exports the public .cer. Never ship that package; the private key is deleted at once.
  Output: <OutputDir>\dist\DBD-Presence-<version>.msix (and DBD-Presence-test.cer plus README.txt when test-signed).
#>
[CmdletBinding()]
param(
  [string] $PublishDir = 'publish/win-x64',
  [string] $OutputDir = 'msix',
  [Parameter(Mandatory)] [string] $IdentityName,
  [Parameter(Mandatory)] [string] $Publisher,
  [Parameter(Mandatory)] [string] $PublisherDisplayName,
  [switch] $SignWithTestCertificate,
  [string] $Commit = $env:GITHUB_SHA,
  [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

foreach ($required in @(
    @{ Name = 'IdentityName'; Value = $IdentityName },
    @{ Name = 'Publisher'; Value = $Publisher },
    @{ Name = 'PublisherDisplayName'; Value = $PublisherDisplayName })) {
  if ([string]::IsNullOrWhiteSpace($required.Value)) { throw "$($required.Name) is empty. Take it from Partner Center (Product identity)." }
}
if ($IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$') { throw "IdentityName '$IdentityName' is not a valid package name (3 to 50 letters, digits, '.' or '-')." }
if ($Publisher -notmatch '^CN=') { throw "Publisher '$Publisher' must be a distinguished name that starts with CN= (copy it exactly from Partner Center)." }
if (-not (Test-Path (Join-Path $PublishDir 'DBD-Presence.exe'))) { throw "DBD-Presence.exe not found in $PublishDir (publish first)." }

$version = (python build/msix_version.py Directory.Build.props).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "could not map the app version to an MSIX version (got '$version')" }

function Find-SdkTool([string] $name) {
  $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.*\x64\$name" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
  if (-not $found) { throw "$name not found under '${env:ProgramFiles(x86)}\Windows Kits\10\bin'. Install the Windows 10/11 SDK." }
  $found.FullName
}

function Invoke-Bounded([string] $file, [string[]] $arguments, [string] $what) {
  Write-Host "$what : $file"
  $quoted = $arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
  $process = Start-Process -FilePath $file -ArgumentList $quoted -PassThru -NoNewWindow
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill($true)
    throw "$what did not finish within $TimeoutSeconds s"
  }
  if ($process.ExitCode -ne 0) { throw "$what exited with code $($process.ExitCode)" }
}

$makeappx = Find-SdkTool 'makeappx.exe'
$layout = Join-Path $OutputDir 'layout'
$dist = Join-Path $OutputDir 'dist'
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout, $dist | Out-Null

# 1. The layout: the published files (not the development-build note), the manifest and the logos.
Get-ChildItem -Path $PublishDir -Force | Where-Object { $_.Name -ne 'README-DEV-BUILD.txt' } | Copy-Item -Destination $layout -Recurse -Force
Copy-Item -Recurse -Force 'build/msix/Assets' (Join-Path $layout 'Assets')

$esc = { param($v) [Security.SecurityElement]::Escape($v) }
$manifest = Get-Content -Raw 'build/msix/AppxManifest.xml'
$manifest = $manifest.Replace('{{IDENTITY_NAME}}', (& $esc $IdentityName))
$manifest = $manifest.Replace('{{PUBLISHER}}', (& $esc $Publisher))
$manifest = $manifest.Replace('{{PUBLISHER_DISPLAY_NAME}}', (& $esc $PublisherDisplayName))
$manifest = $manifest.Replace('{{VERSION}}', $version)
if ($manifest -match '\{\{') { throw 'the manifest still has an unfilled {{value}}' }
[void][xml]$manifest   # throws when the filled manifest is not well-formed
[IO.File]::WriteAllText((Join-Path (Resolve-Path $layout) 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

# 2. Pack. /o overwrites; semantic validation stays on (no /nv), so a wrong manifest or a missing logo fails here.
$msix = Join-Path (Resolve-Path $dist) "DBD-Presence-$version.msix"
Invoke-Bounded $makeappx @('pack', '/d', (Resolve-Path $layout).Path, '/p', $msix, '/o') 'makeappx pack'
if (-not (Test-Path $msix)) { throw "makeappx did not produce $msix" }

# 3. CI only: sign with a throw-away certificate whose subject is the Publisher, and export the public certificate.
if ($SignWithTestCertificate) {
  $signtool = Find-SdkTool 'signtool.exe'
  $work = Join-Path ([IO.Path]::GetTempPath()) ("dbdp-msix-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $work | Out-Null
  $cert = $null
  try {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName 'DBD Presence test signing (CI only)' `
      -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(30) `
      -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $password = ConvertTo-SecureString -String ([guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $pfx = Join-Path $work 'test.pfx'
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'DBD-Presence-test.cer') | Out-Null
    $plain = [Net.NetworkCredential]::new('', $password).Password
    Invoke-Bounded $signtool @('sign', '/fd', 'SHA256', '/f', $pfx, '/p', $plain, $msix) 'signtool sign'
  }
  finally {
    if ($cert) { Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
  }

  @(
    "DBD Presence $version - development MSIX package (TEST-SIGNED, for sideload tests only)"
    ''
    "Commit: $Commit"
    "Package identity: $IdentityName; publisher $Publisher"
    ''
    'This is not the Store package. It is signed with a throw-away test certificate so CI can install it. The real package is'
    'built unsigned by the Release workflow and signed by the Microsoft Store.'
    ''
    'Sideload test (Windows 10 2004 or newer / Windows 11; use a test PC or a virtual machine)'
    '  1. Open PowerShell as administrator in this folder.'
    '  2. Trust the test certificate (this lets Windows install packages signed by it, so remove it afterwards):'
    '       Import-Certificate -FilePath .\DBD-Presence-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople'
    '  3. Install:   Add-AppxPackage .\' + (Split-Path $msix -Leaf)
    '  4. Start DBD Presence from the Start menu. Turn "Start with Windows" on in Settings; it uses the package''s startup task.'
    '  5. Uninstall: Windows Settings > Apps > Installed apps > DBD Presence > Uninstall, or Remove-AppxPackage (Get-AppxPackage *DbdPresence*).'
    '  6. Remove the certificate again:'
    '       Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq ''' + $Publisher + ''' | Remove-Item'
    ''
    'Unofficial; not affiliated with or endorsed by Behaviour Interactive or Discord.'
  ) | Set-Content -Encoding utf8 (Join-Path $dist 'README.txt')
}

$hash = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Packed $msix ($((Get-Item $msix).Length) bytes) SHA-256 $hash; test-signed: $([bool]$SignWithTestCertificate)"
