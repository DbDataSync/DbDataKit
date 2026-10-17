<#
.SYNOPSIS
  Downloads a DbDataProxy release from GitHub and installs (or upgrades) it. Works in Windows PowerShell 5.1.
.DESCRIPTION
  DbDataProxy is released from the DbDataKit repository, which also publishes other products' releases, so a DbDataProxy
  release is recognised by its tag, dbdataproxy/v<version> (the version is the UTC build time, YYYY.M.D.HHmm, with a -beta
  suffix on a prerelease). 'latest' means the newest of those that is not a prerelease; GitHub's own "latest release" is one
  marker for the whole repository and is not used.

  Verifies the download against the release's SHA256SUMS, replaces the program files and keeps an existing
  appsettings.json. A running Windows service is stopped for the upgrade and started again afterwards.
.EXAMPLE
  # Latest release, files only (main always holds the script of the last release):
  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/DbDataSync/DbDataKit/main/DbDataProxy/scripts/install.ps1)))
.EXAMPLE
  # Specific version, registered as a Windows service (run from an elevated prompt):
  ./install.ps1 -Version 2026.10.17.1530 -InstallService
.EXAMPLE
  # See which release and files would be used, without installing anything (no elevation needed):
  ./install.ps1 -DryRun
  ./install.ps1 -DryRun -IncludePrerelease
#>
[CmdletBinding()]
param(
  [string]$Repo = 'DbDataSync/DbDataKit',
  [string]$Version = 'latest',
  [switch]$IncludePrerelease,
  [switch]$DryRun,
  [string]$InstallDir = (Join-Path $env:ProgramFiles 'DbDataProxy'),
  [string]$ServiceName = 'DbDataProxy',
  [switch]$InstallService
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress rendering makes Invoke-WebRequest very slow in PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($Repo -like 'OWNER/*') { throw "Pass -Repo <owner>/<repository> (this copy of the script has no repository set)." }

# --- Find the release ---
# Every product's releases are in this one repository, so a release is DbDataProxy's only if its tag starts with this.
$tagPrefix = 'dbdataproxy/v'
$wanted = ($Version -replace '^dbdataproxy/', '') -replace '^v', ''
$candidates = @()
for ($page = 1; $page -le 3; $page++) {
  $batch = @(Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=100&page=$page" -Headers @{ 'User-Agent' = 'DbDataProxy-installer' })
  foreach ($r in $batch) {
    if ($r.draft -or -not $r.tag_name.StartsWith($tagPrefix)) { continue }
    $candidates += $r
  }
  if ($batch.Count -lt 100) { break }
}
if ($Version -eq 'latest') {
  $eligible = @($candidates | Where-Object { $IncludePrerelease -or -not $_.prerelease })
  $release = $eligible | Sort-Object { [datetime]$_.published_at } -Descending | Select-Object -First 1
  if (-not $release) {
    $hint = if ($candidates.Count -gt 0) { ' Only prereleases exist; pass -IncludePrerelease to allow one.' } else { '' }
    throw "No DbDataProxy release was found in $Repo.$hint"
  }
}
else {
  $release = $candidates | Where-Object { $_.tag_name -eq ($tagPrefix + $wanted) } | Select-Object -First 1
  if (-not $release) { throw "No DbDataProxy release $wanted was found in $Repo." }
}
$zipAsset = $release.assets | Where-Object { $_.name -like 'DbDataProxy-*-win-x64.zip' } | Select-Object -First 1
$sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
if (-not $zipAsset -or -not $sumsAsset) { throw "Release $($release.tag_name) has no win-x64 package." }
$releaseVersion = $release.tag_name.Substring($tagPrefix.Length)

if ($DryRun) {
  Write-Host "DbDataProxy $releaseVersion ($($release.tag_name))$(if ($release.prerelease) { ' [prerelease]' })"
  Write-Host "  package   $($zipAsset.browser_download_url)"
  Write-Host "  checksums $($sumsAsset.browser_download_url)"
  return
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this from an elevated (Administrator) prompt: it writes to Program Files and manages the service.'
}
if (-not [Environment]::Is64BitOperatingSystem) { throw 'DbDataProxy requires 64-bit Windows.' }
Write-Host "Installing DbDataProxy $releaseVersion to $InstallDir"

# --- Download and verify ---
$work = Join-Path ([IO.Path]::GetTempPath()) ("dbdataproxy-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $zip = Join-Path $work $zipAsset.name
  Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip
  $sums = (Invoke-WebRequest $sumsAsset.browser_download_url -UseBasicParsing).Content
  if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }   # served as octet-stream
  $line = ($sums -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($zipAsset.name) } | Select-Object -First 1
  if (-not $line) { throw 'SHA256SUMS does not list the package.' }
  $expected = ($line -split '\s+')[0].ToLowerInvariant()
  $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "Checksum mismatch for $($zipAsset.name): expected $expected, got $actual." }

  $extracted = Join-Path $work 'files'
  Expand-Archive $zip -DestinationPath $extracted

  # --- Stop the service while files are replaced ---
  $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
  $wasRunning = $service -and $service.Status -eq 'Running'
  if ($wasRunning) { Write-Host 'Stopping service...'; Stop-Service $ServiceName; $service.WaitForStatus('Stopped', '00:00:30') }

  # --- Copy files, keeping the administrator's configuration ---
  New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
  $keepConfig = Test-Path (Join-Path $InstallDir 'appsettings.json')
  Get-ChildItem $extracted -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($extracted.Length).TrimStart('\')
    if ($keepConfig -and $relative -eq 'appsettings.json') { return }
    $destination = Join-Path $InstallDir $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item $_.FullName $destination -Force
  }
  if ($keepConfig) { Write-Host 'Kept your existing appsettings.json (see appsettings.example.json for new options).' }

  # --- Service ---
  $exe = Join-Path $InstallDir 'DbDataProxy.exe'
  if ($InstallService -and -not $service) {
    & $exe install --name $ServiceName
    if ($LASTEXITCODE) { throw 'Registering the Windows service failed.' }
  }
  if ($wasRunning) { Write-Host 'Starting service...'; Start-Service $ServiceName }
}
finally {
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Installed DbDataProxy $releaseVersion."
if (-not $keepConfig) {
  Write-Host "Next: create a password hash with '$exe hash-password', put it in $InstallDir\appsettings.json (see appsettings.example.json)."
  Write-Host "Then run the proxy in a console ('$exe') or start the service ('sc start $ServiceName')."
}
