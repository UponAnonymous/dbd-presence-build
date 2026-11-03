<#
.SYNOPSIS
  Packs the self-contained publish folder into a per-user Setup.exe and a portable ZIP with Velopack (V2-S2e, ADR-0006).
.DESCRIPTION
  Unsigned on purpose (decision D7): no signing parameters are passed. Updates (V2-S6) do not depend on Authenticode:
  the full package this writes (<packId>-<version>-stable-full.nupkg) is signed into a release feed offline by the owner
  (tools/DbdPresence.ReleaseSigner, docs/owner/release-keys.md), and the app installs it only when it matches that feed.
  Needs `dotnet tool restore` to have run (the vpk tool is pinned in .config/dotnet-tools.json).
  Output: <OutputDir>\vpk (everything vpk wrote) and <OutputDir>\dist (the files to upload).
#>
[CmdletBinding()]
param(
  [string] $PublishDir = 'publish/win-x64',
  [string] $OutputDir = 'installer',
  [string] $Commit = $env:GITHUB_SHA,
  # 'release' only from .github/workflows/release.yml (a production build of a tagged commit on main); CI (Windows) packs development builds.
  [ValidateSet('development', 'release')] [string] $BuildKind = 'development'
)

$ErrorActionPreference = 'Stop'

$props = [xml](Get-Content -Raw 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$version' in Directory.Build.props is not SemVer" }

$vpkDir = Join-Path $OutputDir 'vpk'
$dist = Join-Path $OutputDir 'dist'
Remove-Item -Recurse -Force $OutputDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $vpkDir, $dist | Out-Null

dotnet vpk pack `
  --packId DbdPresence `
  --packVersion $version `
  --packDir $PublishDir `
  --mainExe DBD-Presence.exe `
  --packTitle 'DBD Presence' `
  --packAuthors 'DBD Presence contributors' `
  --icon 'src/DbdPresence.App/Assets/app.ico' `
  --runtime win-x64 `
  --channel stable `
  --outputDir $vpkDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

$setup = Join-Path $vpkDir 'DbdPresence-stable-Setup.exe'
$portable = Join-Path $vpkDir 'DbdPresence-stable-Portable.zip'
foreach ($f in @($setup, $portable)) { if (-not (Test-Path $f)) { throw "vpk did not produce $f" } }

Copy-Item $setup (Join-Path $dist 'DBD-Presence-Setup.exe')
Copy-Item $portable (Join-Path $dist 'DBD-Presence-Portable.zip')

@(
  $(if ($BuildKind -eq 'release') { "DBD Presence $version - release build (installer not code-signed; updates are signed)" } else { "DBD Presence $version - development build (UNSIGNED)" })
  ''
  "Commit: $Commit"
  ''
  'Install'
  '  1. Run DBD-Presence-Setup.exe. It installs for your Windows account only (no administrator rights) into'
  '     %LocalAppData%\DbdPresence and adds Start-menu and desktop shortcuts.'
  '  2. Windows SmartScreen may warn ("Windows protected your PC") because this build is not code-signed.'
  '     Choose More info, then Run anyway. To check the download first, compare its SHA-256 with SHA256SUMS.txt:'
  '     in PowerShell, Get-FileHash .\DBD-Presence-Setup.exe -Algorithm SHA256'
  ''
  'First start'
  '  You need the Discord desktop app (not Discord in a browser), running on this PC.'
  '  1. Sign in with Discord: your browser opens Discord''s own sign-in page. Only your Discord ID and display name'
  '     are shared with the DBD Presence service.'
  '  2. While access is by invitation, enter the invite code you were given (once).'
  '  3. A short setup follows. In Discord, turn on sharing your activity (User Settings > Activity Privacy) so your'
  '     status can be shown.'
  '  Advanced: to show your status from a Discord application you created yourself, use Settings > Advanced >'
  '  "Use my own Discord app". Most people should leave this off.'
  ''
  'Uninstall'
  '  To delete your account on the service, use the app''s Account page (Delete my account) before uninstalling.'
  '  Windows Settings > Apps > Installed apps > DBD Presence > Uninstall. This removes the program, the shortcuts'
  '  and the "start with Windows" entry. Your settings and logs are kept on this PC; to remove them, delete the'
  '  folders %APPDATA%\DBDPresence and %LOCALAPPDATA%\DBDPresence.Data.'
  ''
  'Portable'
  '  DBD-Presence-Portable.zip runs from any folder: extract it, then start DBD Presence.exe. Nothing is installed.'
  ''
  'Notes'
  '  - Updates install automatically while you are signed in with access. Each update must carry the developer''s'
  '    signature and is checked before it is installed. You can also run a newer Setup.exe yourself.'
  '  - Unofficial; not affiliated with or endorsed by Behaviour Interactive or Discord.'
) | Set-Content -Encoding utf8 (Join-Path $dist 'README.txt')

$sums = Get-ChildItem $dist -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content -Encoding ascii (Join-Path $dist 'SHA256SUMS.txt')
Get-Content (Join-Path $dist 'SHA256SUMS.txt')
