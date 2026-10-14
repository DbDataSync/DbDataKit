<#
.SYNOPSIS
  Publishes the proxy and zips it as DbDataProxy-<version>-win-x64.zip with a SHA256SUMS file beside it.
.EXAMPLE
  ./build/package.ps1 -Version 1.2.3     # -> artifacts/release/DbDataProxy-1.2.3-win-x64.zip
#>
param(
  [Parameter(Mandatory)][string]$Version,
  [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\release')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

& (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version
$published = Join-Path $root 'artifacts\publish\win-x64'

# Leave out debug symbols and the IIS web.config (IIS is not a supported host).
$stage = Join-Path $root "artifacts\stage\DbDataProxy-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $published '*') $stage -Recurse -Exclude '*.pdb', 'web.config'
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $stage

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$zip = Join-Path $Output "DbDataProxy-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Zip the folder's contents (not the folder itself) so the installer can extract straight into the install directory.
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content (Join-Path $Output 'SHA256SUMS') -Encoding ascii
Write-Host "Packaged $zip"
