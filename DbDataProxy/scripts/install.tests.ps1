<#
.SYNOPSIS
  Tests how install.ps1 chooses a release, against a mocked GitHub API (no network, no installation). Runs in Windows
  PowerShell 5.1 and PowerShell 7:   powershell -NoProfile -File scripts/install.tests.ps1
#>
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'install.ps1'
$global:failures = 0

function Release($tag, [bool]$prerelease = $false, [bool]$draft = $false, $published = '2026-10-01T10:00:00Z', [bool]$assets = $true) {
  $a = @()
  if ($assets) {
    $version = $tag.Substring($tag.IndexOf('/v') + 2)
    $product = if ($tag -like 'dbdatasync/*') { 'DbDataSync' } else { 'DbDataProxy' }
    $a = @(
      [pscustomobject]@{ name = "$product-$version-win-x64.zip"; browser_download_url = "https://example.test/dl/$tag/$product-$version-win-x64.zip" },
      [pscustomobject]@{ name = 'SHA256SUMS'; browser_download_url = "https://example.test/dl/$tag/SHA256SUMS" })
  }
  [pscustomobject]@{ tag_name = $tag; draft = $draft; prerelease = $prerelease; published_at = $published; assets = $a }
}

# What GitHub lists: several products in one repository, newest first.
$global:listing = @(
  (Release 'dbdatasync/v2026.10.9.1000' $false $false '2026-10-09T10:00:00Z'),
  (Release 'dbdataproxy/v2026.10.8.0900-beta' $true $false '2026-10-08T09:00:00Z'),
  (Release 'dbdataproxy/v2026.10.7.1200' $false $true '2026-10-07T12:00:00Z'),        # a draft
  (Release 'dbdataproxy/v2026.10.5.1500' $false $false '2026-10-05T15:00:00Z'),
  (Release 'dbdatabuild/v2026.10.4.1000' $false $false '2026-10-04T10:00:00Z'),
  (Release 'v2026.10.3.1000' $false $false '2026-10-03T10:00:00Z'),                    # no product prefix
  (Release 'dbdataproxy/v2026.10.1.1000' $false $false '2026-10-01T10:00:00Z')
)

# The script calls Invoke-RestMethod; a function of that name in the calling scope wins over the cmdlet.
function Invoke-RestMethod {
  param([Parameter(Position = 0)][string]$Uri, $Headers)
  $global:requests += $Uri
  if ($Uri -match '[?&]page=(\d+)') { $page = [int]$Matches[1] } else { $page = 1 }
  if ($page -eq 1) { return $global:listing } else { return @() }
}

function Run([hashtable]$parameters) {
  $global:requests = @()
  $parameters['InstallDir'] = 'unused'      # the default is under $env:ProgramFiles, which is not set everywhere
  try { $out = & $script @parameters *>&1 | Out-String; return @{ Out = $out; Error = $null } }
  catch { return @{ Out = ''; Error = $_.Exception.Message } }
}
function Check($name, $condition) {
  if ($condition) { Write-Host "ok   $name" } else { Write-Host "FAIL $name"; $global:failures++ }
}

# latest = the newest DbDataProxy release that is not a prerelease, not a draft, and not another product's
$r = Run @{ DryRun = $true }
Check 'latest picks the newest stable DbDataProxy release' ($r.Out -match '2026\.10\.5\.1500 \(dbdataproxy/v2026\.10\.5\.1500\)')
Check 'latest ignores the newer DbDataSync release' ($r.Out -notmatch '2026\.10\.9')
Check 'latest ignores the beta unless asked' ($r.Out -notmatch 'beta')
Check 'it names the package and checksum files' (($r.Out -match 'DbDataProxy-2026\.10\.5\.1500-win-x64\.zip') -and ($r.Out -match 'SHA256SUMS'))
Check 'it reads this repository by default' ($global:requests[0] -like 'https://api.github.com/repos/DbDataSync/DbDataKit/releases?*')

$r = Run @{ DryRun = $true; IncludePrerelease = $true }
Check '-IncludePrerelease lets latest pick a newer beta' (($r.Out -match '2026\.10\.8\.0900-beta') -and ($r.Out -match 'prerelease'))

# a specific version, in any of the spellings
foreach ($v in @('2026.10.1.1000', 'v2026.10.1.1000', 'dbdataproxy/v2026.10.1.1000')) {
  $r = Run @{ DryRun = $true; Version = $v }
  Check "-Version $v" ($r.Out -match '\(dbdataproxy/v2026\.10\.1\.1000\)')
}
$r = Run @{ DryRun = $true; Version = '2026.10.8.0900-beta' }
Check 'a specific beta is found without -IncludePrerelease' ($r.Out -match '2026\.10\.8\.0900-beta')
$r = Run @{ DryRun = $true; Version = '2026.10.7.1200' }
Check 'a draft is never found' ($null -ne $r.Error -and $r.Error -match 'No DbDataProxy release 2026\.10\.7\.1200')
$r = Run @{ DryRun = $true; Version = '2026.10.9.1000' }
Check "another product's version is not found" ($null -ne $r.Error)

# only prereleases exist
$saved = $global:listing
$global:listing = @((Release 'dbdataproxy/v2026.10.8.0900-beta' $true), (Release 'dbdatasync/v2026.10.9.1000'))
$r = Run @{ DryRun = $true }
Check 'with only prereleases, latest says so' ($null -ne $r.Error -and $r.Error -match '-IncludePrerelease')
$global:listing = @((Release 'dbdatasync/v2026.10.9.1000'))
$r = Run @{ DryRun = $true }
Check 'with no DbDataProxy release at all, latest fails clearly' ($null -ne $r.Error -and $r.Error -match 'No DbDataProxy release was found' -and $r.Error -notmatch 'IncludePrerelease')

# a release with no package is refused
$global:listing = @((Release 'dbdataproxy/v2026.10.6.1000' $false $false '2026-10-06T10:00:00Z' $false))
$r = Run @{ DryRun = $true }
Check 'a release without a win-x64 package is refused' ($null -ne $r.Error -and $r.Error -match 'no win-x64 package')

# more than one page of releases
$page1 = 1..100 | ForEach-Object { Release "dbdatasync/v2026.9.$_.1000" }
$page2 = @((Release 'dbdataproxy/v2026.8.1.1000' $false $false '2026-08-01T10:00:00Z'))
function Invoke-RestMethod {
  param([Parameter(Position = 0)][string]$Uri, $Headers)
  $global:requests += $Uri
  if ($Uri -match '[?&]page=2') { return $page2 } elseif ($Uri -match '[?&]page=1') { return $page1 } else { return @() }
}
$r = Run @{ DryRun = $true }
Check 'it follows a full page to the next one' ($r.Out -match '2026\.8\.1\.1000' -and $global:requests.Count -eq 2)

if ($global:failures) { Write-Host "$($global:failures) check(s) failed."; exit 1 }
Write-Host 'All checks passed.'
