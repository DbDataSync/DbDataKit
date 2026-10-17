<#
.SYNOPSIS
  Downloads a DbDataProxy release from GitHub and installs (or upgrades) it. Works in Windows PowerShell 5.1.
.DESCRIPTION
  Verifies the download against the release's SHA256SUMS, replaces the program files and keeps an existing
  appsettings.json. A running Windows service is stopped for the upgrade and started again afterwards.
.EXAMPLE
  # Latest release, files only:
  & ([scriptblock]::Create((irm https://github.com/DbDataSync/DbDataProxy/releases/latest/download/install.ps1)))
.EXAMPLE
  # Specific version, registered as a Windows service (run from an elevated prompt):
  ./install.ps1 -Version 1.2.3 -InstallService
#>
[CmdletBinding()]
param(
  [string]$Repo = 'DbDataSync/DbDataProxy',
  [string]$Version = 'latest',
  [string]$InstallDir = (Join-Path $env:ProgramFiles 'DbDataProxy'),
  [string]$ServiceName = 'DbDataProxy',
  [switch]$InstallService
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress rendering makes Invoke-WebRequest very slow in PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($Repo -like 'OWNER/*') { throw "Pass -Repo <owner>/<repository> (this copy of the script has no repository set)." }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this from an elevated (Administrator) prompt: it writes to Program Files and manages the service.'
}
if (-not [Environment]::Is64BitOperatingSystem) { throw 'DbDataProxy requires 64-bit Windows.' }

# --- Find the release ---
$api = if ($Version -eq 'latest') { "https://api.github.com/repos/$Repo/releases/latest" }
       else { "https://api.github.com/repos/$Repo/releases/tags/v$($Version.TrimStart('v'))" }
$release = Invoke-RestMethod $api -Headers @{ 'User-Agent' = 'DbDataProxy-installer' }
$zipAsset = $release.assets | Where-Object { $_.name -like 'DbDataProxy-*-win-x64.zip' } | Select-Object -First 1
$sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
if (-not $zipAsset -or -not $sumsAsset) { throw "Release $($release.tag_name) has no win-x64 package." }
Write-Host "Installing DbDataProxy $($release.tag_name) to $InstallDir"

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
Write-Host "Installed $($release.tag_name)."
if (-not $keepConfig) {
  Write-Host "Next: create a password hash with '$exe hash-password', put it in $InstallDir\appsettings.json (see appsettings.example.json)."
  Write-Host "Then run the proxy in a console ('$exe') or start the service ('sc start $ServiceName')."
}
