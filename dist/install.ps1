<#
.SYNOPSIS
  Downloads a release from GitHub and installs (or upgrades) it. Works in Windows PowerShell 5.1 and PowerShell 7.
.DESCRIPTION
  One of the standard distribution files (dist/README.md). The copy attached to a release is bound to one product by dist/dist.py,
  from <Product>/dist.json; this is the template, and it refuses to run unbound.

  Every product's releases are in one repository, so a release is this product's only if its tag starts with <product>/v. 'latest' means
  the newest of those that is not a prerelease (GitHub's own "latest release" is one marker for the whole repository and is not used).

  The package is checked against the release's SHA256SUMS, the program files are replaced, and files the product lists as configuration
  (an existing appsettings.json, say) are kept. A Windows service the product registers is stopped for the upgrade and started again.
  Installing again is how you upgrade.
.EXAMPLE
  # Latest stable release (the one-liner on the product's release page):
  irm https://github.com/DbDataSync/DbDataKit/releases/download/<product>/dist/install.ps1 | iex
.EXAMPLE
  # A specific version, or see what would be installed without installing anything:
  ./install.ps1 -Version 2026.10.19.1200
  ./install.ps1 -DryRun -IncludePrerelease
#>
[CmdletBinding()]
param(
  [string]$Version = 'latest',
  [switch]$IncludePrerelease,
  [switch]$DryRun,
  [string]$InstallDir,
  # Install for all users (needs an elevated prompt). Each product has a default; this forces the machine-wide one.
  [switch]$Machine,
  # Leave the user's PATH alone.
  [switch]$NoPath,
  # Register the product's Windows service, for a product that has one.
  [switch]$InstallService,
  [string]$Repo,
  [string]$ApiBase = 'https://api.github.com'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress rendering makes Invoke-WebRequest very slow in PS 5.1
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# >>> bound by dist/dist.py when a release is published: edit <Product>/dist.json, not this block <<<
$Bound = @{
  Product = ''
}
# <<< end bound >>>

if (-not $Bound.Product) { throw 'This copy of install.ps1 is not bound to a product. Use the one attached to a release.' }
$product = $Bound.Product
$display = $Bound.DisplayName
if (-not $Repo) { $Repo = $Bound.Repo }
$onWindows = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
$rid = 'win-x64'

# --- Find the release ---
$tagPrefix = "$product/v"
$wanted = ($Version -replace "^$product/", '') -replace '^v', ''
$candidates = @()
for ($page = 1; $page -le 5; $page++) {
  # Windows PowerShell 5.1 returns the JSON array as one object; the extra pipeline stage makes both versions yield the releases.
  $batch = @(Invoke-RestMethod "$ApiBase/repos/$Repo/releases?per_page=100&page=$page" -Headers @{ 'User-Agent' = "$product-installer"; 'Accept' = 'application/vnd.github+json' } | ForEach-Object { $_ })
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
    throw "No $display release was found in $Repo.$hint"
  }
}
else {
  $release = $candidates | Where-Object { $_.tag_name -eq ($tagPrefix + $wanted) } | Select-Object -First 1
  if (-not $release) { throw "No $display release $wanted was found in $Repo." }
}
$releaseVersion = $release.tag_name.Substring($tagPrefix.Length)
$zipName = "$product-v$releaseVersion-$rid.zip"
$zipAsset = $release.assets | Where-Object { $_.name -eq $zipName } | Select-Object -First 1
$sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
if (-not $zipAsset -or -not $sumsAsset) { throw "Release $($release.tag_name) has no $rid package ($zipName)." }

if ($DryRun) {
  Write-Host "$display $releaseVersion ($($release.tag_name))$(if ($release.prerelease) { ' [prerelease]' })"
  Write-Host "  package   $($zipAsset.browser_download_url)"
  Write-Host "  checksums $($sumsAsset.browser_download_url)"
  return
}

# --- Where, and with what rights ---
$machineWide = $Machine -or ($Bound.WindowsScope -eq 'machine')
if (-not $InstallDir) {
  $InstallDir = if ($machineWide) { Join-Path $env:ProgramFiles $display } else { Join-Path $env:LOCALAPPDATA "Programs\$display" }
}
if ($onWindows) {
  if (-not [Environment]::Is64BitOperatingSystem) { throw "$display requires 64-bit Windows." }
  if ($machineWide) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
      throw 'Run this from an elevated (Administrator) prompt: it writes to Program Files and manages the service.'
    }
  }
}
Write-Host "Installing $display $releaseVersion to $InstallDir"

# --- Download and verify ---
$work = Join-Path ([IO.Path]::GetTempPath()) ("$product-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $zip = Join-Path $work $zipAsset.name
  Invoke-WebRequest $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing
  $sums = (Invoke-WebRequest $sumsAsset.browser_download_url -UseBasicParsing).Content
  if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }   # served as octet-stream
  $line = ($sums -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($zipAsset.name) } | Select-Object -First 1
  if (-not $line) { throw 'SHA256SUMS does not list the package.' }
  $expected = ($line -split '\s+')[0].ToLowerInvariant()
  $sha = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($zip)
  try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
  finally { $stream.Dispose(); $sha.Dispose() }
  if ($actual -ne $expected) { throw "Checksum mismatch for $($zipAsset.name): expected $expected, got $actual." }

  $extracted = Join-Path $work 'files'
  Expand-Archive $zip -DestinationPath $extracted

  # --- Stop the service while files are replaced ---
  $service = $null
  $wasRunning = $false
  if ($onWindows -and $Bound.ServiceName) {
    $service = Get-Service -Name $Bound.ServiceName -ErrorAction SilentlyContinue
    $wasRunning = $service -and $service.Status -eq 'Running'
    if ($wasRunning) { Write-Host 'Stopping service...'; Stop-Service $Bound.ServiceName; $service.WaitForStatus('Stopped', '00:00:30') }
  }

  # --- Copy files, keeping the administrator's configuration ---
  New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
  $kept = @()
  Get-ChildItem $extracted -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($extracted.Length).TrimStart('\', '/')
    $destination = Join-Path $InstallDir $relative
    if (($Bound.Preserve -contains $relative) -and (Test-Path $destination)) { $kept += $relative; return }
    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
    Copy-Item $_.FullName $destination -Force
  }
  foreach ($k in $kept) { Write-Host "Kept your existing $k." }

  # --- Service ---
  $exe = Join-Path $InstallDir $Bound.Exe
  if ($onWindows -and $Bound.ServiceName -and $InstallService -and -not $service) {
    $registerArgs = ($Bound.ServiceRegister -replace '\{name\}', $Bound.ServiceName) -split '\s+'
    & $exe @registerArgs
    if ($LASTEXITCODE) { throw 'Registering the Windows service failed.' }
  }
  if ($wasRunning) { Write-Host 'Starting service...'; Start-Service $Bound.ServiceName }

  # --- PATH (a per-user install only: a machine-wide one is left to the administrator) ---
  if ($onWindows -and -not $NoPath -and -not $machineWide) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (($userPath -split ';') -contains $InstallDir)) {
      [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $InstallDir).TrimStart(';'), 'User')
      Write-Host "Added $InstallDir to your PATH (open a new terminal to use it)."
    }
  }
}
finally {
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Installed $display $releaseVersion."
if ($Bound.RequiresDotnet) {
  $runtimes = try { (& dotnet --list-runtimes 2>$null) -join "`n" } catch { '' }
  if ($runtimes -notmatch "Microsoft\.NETCore\.App $([regex]::Escape($Bound.RequiresDotnet.Split('.')[0]))\.") {
    Write-Warning "$display needs the .NET $($Bound.RequiresDotnet) runtime, which was not found. Install it from https://dotnet.microsoft.com/download"
  }
}
if ($Bound.Notes) { Write-Host $Bound.Notes }
