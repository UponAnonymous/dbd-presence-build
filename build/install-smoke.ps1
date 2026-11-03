<#
.SYNOPSIS
  Install smoke test for the per-user Setup.exe (RELEASE_CHECKLIST §6, SAFETY R-11/R-12, V2-S2e).
.DESCRIPTION
  Installs silently, checks the install folder, the Start-menu and desktop shortcuts and the uninstall entry,
  plants a Run-key value, uninstalls silently and checks that the folder, shortcuts, uninstall entry and Run key are gone.
  Runs only on a disposable Windows runner: it writes to the current user's profile and registry.
  Flags used (Velopack 1.2.161, see docs/DECISIONS/ADR-0006): Setup.exe --silent; Update.exe --uninstall --silent
  (the same command Velopack registers as QuietUninstallString).
.PARAMETER SetupExe
  Path to the Setup.exe produced by vpk pack.
.PARAMETER TimeoutSeconds
  Upper bound for each install or uninstall step.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $SetupExe,
  [int] $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$packId = 'DbdPresence'
$installDir = Join-Path $env:LOCALAPPDATA $packId
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'DBD Presence'
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$desktop = [Environment]::GetFolderPath('Desktop')

function Fail([string] $message) {
  Write-Host "::error::Install smoke FAILED: $message"
  throw "Install smoke FAILED: $message"
}

function Invoke-Bounded([string] $file, [string[]] $arguments, [string] $what) {
  Write-Host "$what : $file $($arguments -join ' ')"
  $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru -WindowStyle Hidden
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill($true)
    Fail "$what did not finish within $TimeoutSeconds s"
  }
  if ($process.ExitCode -ne 0) { Fail "$what exited with code $($process.ExitCode)" }
}

function Wait-Until([scriptblock] $condition, [string] $what) {
  $watch = [Diagnostics.Stopwatch]::StartNew()
  while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    if (& $condition) { return }
    Start-Sleep -Milliseconds 500
  }
  Fail "$what (waited $TimeoutSeconds s)"
}

function Find-Shortcuts([string] $folder) {
  if (-not (Test-Path $folder)) { return @() }
  @(Get-ChildItem -Path $folder -Filter 'DBD Presence*.lnk' -File -ErrorAction SilentlyContinue)
}

function Find-UninstallEntries {
  @(Get-ChildItem -Path $uninstallRoot -ErrorAction SilentlyContinue |
      Where-Object { (Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq 'DBD Presence' })
}

if (-not (Test-Path $SetupExe)) { Fail "Setup.exe not found at $SetupExe" }
if (Test-Path $installDir) { Fail "$installDir already exists; the runner is not clean" }

# 1. Install without UI. --silent also stops Setup from starting the app afterwards.
Invoke-Bounded $SetupExe @('--silent') 'Install'
Wait-Until { Test-Path (Join-Path $installDir 'Update.exe') } "Update.exe not found under $installDir after install"

# 2. The app files, both in the versioned folder and as the root launcher.
$mainExe = Join-Path $installDir 'current\DBD-Presence.exe'
if (-not (Test-Path $mainExe)) { Fail "DBD-Presence.exe missing at $mainExe" }
$rootExes = @(Get-ChildItem -Path $installDir -Filter '*.exe' -File | Where-Object { $_.Name -ne 'Update.exe' })
if ($rootExes.Count -eq 0) { Fail "no root launcher next to Update.exe in $installDir" }
Write-Host ("Root launcher(s): " + (($rootExes | ForEach-Object Name) -join ', '))
Get-ChildItem -Path $installDir | ForEach-Object { Write-Host "  installed: $($_.Name)" }

# 3. Shortcuts and uninstall entry.
if ((Find-Shortcuts $startMenu).Count -eq 0) { Fail "no Start-menu shortcut matching 'DBD Presence*.lnk' under $startMenu" }
if ((Find-Shortcuts $desktop).Count -eq 0) { Fail "no desktop shortcut matching 'DBD Presence*.lnk' under $desktop" }
if ((Find-UninstallEntries).Count -eq 0) { Fail "no 'DBD Presence' entry under $uninstallRoot (Windows Settings > Apps)" }

# 4. Plant an autostart value the way the app would, then uninstall.
New-Item -Path $runKeyPath -Force | Out-Null
Set-ItemProperty -Path $runKeyPath -Name $runValueName -Value "`"$installDir\DBD Presence.exe`" --autostart"
if (-not (Get-ItemProperty -Path $runKeyPath -Name $runValueName -ErrorAction SilentlyContinue)) { Fail 'could not plant the Run value for the uninstall check' }

Invoke-Bounded (Join-Path $installDir 'Update.exe') @('--uninstall', '--silent') 'Uninstall'

# 5. Nothing left behind. Velopack removes its own folder after Update.exe exits, so poll.
Wait-Until { -not (Test-Path $installDir) } "install folder $installDir still exists after uninstall"
if ((Find-Shortcuts $startMenu).Count -ne 0) { Fail 'Start-menu shortcut left behind' }
if ((Find-Shortcuts $desktop).Count -ne 0) { Fail 'desktop shortcut left behind' }
if ((Find-UninstallEntries).Count -ne 0) { Fail 'uninstall registry entry left behind' }
$leftover = (Get-ItemProperty -Path $runKeyPath -ErrorAction SilentlyContinue).PSObject.Properties.Name -contains $runValueName
if ($leftover) { Fail "Run-key value '$runValueName' still present after uninstall (SAFETY R-12)" }

Write-Host 'Install smoke OK: install, shortcuts, uninstall entry, uninstall, no residue.'
