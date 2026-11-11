<#
.SYNOPSIS
  Installs the dbdatakit-full Scoop manifest on a real Windows machine from the dry run's packages and checks what each step leaves (plan 33 SC14): a per-user install and an
  install over a service of another folder are refused, a global install registers, starts and pairs every member (and answers each member's health check), a second run changes
  nothing, an update runs the services from the new files and still paired, and an uninstall removes the services and the pairings but not the data.
  Run by the job `suite-install-windows` of .github/workflows/suite-release.yml (a dry run), as an elevated user. Written without a Windows machine: its first run is its proof.
  -Packages is the folder the dry run's artifacts were downloaded to (release-<product>/ folders, each with the zip and SHA256SUMS).
#>
param(
  [Parameter(Mandatory)][string]$Packages,
  [Parameter(Mandatory)][string]$Version,
  [string]$Work = (Join-Path $env:RUNNER_TEMP 'suite-scoop'),
  [int]$Port = 8099
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$suite = Get-Content (Join-Path $repoRoot 'dist\suite.json') -Raw | ConvertFrom-Json
$members = @($suite.members)
$data = Join-Path $env:ProgramData 'DbDataKit'
$env:DBDATAKIT_DATA = $data       # what the machine variable gives a new terminal: the commands run here read the folder the services use
$scoopApp = Join-Path $env:ProgramData 'scoop\apps\dbdatakit-full'
$step = 0
$log = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force $Work, $log | Out-Null

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function Heading([string]$text) { Write-Host "=== $text" }
function Exe([string]$product) { Join-Path $scoopApp ("current\{0}\{1}.exe" -f ($members | Where-Object { $_.product -eq $product }).folder, $product) }

# A native command with its output captured to a file and shown; returns the exit code. Never throws for a non-zero exit.
function Native([string]$name, [scriptblock]$command) {
  $script:step++
  $file = Join-Path $log ("{0:00}-{1}.txt" -f $script:step, $name)
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $global:LASTEXITCODE = 0
  try { & $command *>&1 | Tee-Object -FilePath $file | Out-Host; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
  if ($null -eq $code) { $code = 0 }
  return [pscustomobject]@{ Code = $code; File = $file; Text = (Get-Content $file -Raw) }
}
function ServiceExists([string]$name) {
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
  & sc.exe query $name 2>&1 | Out-Null
  return ($LASTEXITCODE -ne 1060)
}
function Services { foreach ($m in $members) { $meta = Get-Content (Join-Path $repoRoot $m.meta) -Raw | ConvertFrom-Json; if ($meta.service) { [pscustomobject]@{ Product = $m.product; Name = $meta.service.name } } } }
function Diagnose {
  Write-Host '--- services'
  foreach ($s in Services) { & sc.exe qc $s.Name 2>&1 | Out-Host; & sc.exe query $s.Name 2>&1 | Out-Host }
  Write-Host '--- recent System and Application events'
  foreach ($l in 'System', 'Application') {
    Get-WinEvent -FilterHashtable @{ LogName = $l; StartTime = (Get-Date).AddMinutes(-20) } -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -like '*DbData*' -or $_.ProviderName -like '*.NET*' -or $_.ProviderName -eq 'Service Control Manager' } | Select-Object -First 40 |
      ForEach-Object { Write-Host ("{0} {1}: {2}" -f $_.TimeCreated.ToString('HH:mm:ss'), $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  }
}
function AssertEveryMemberRunsPairedAndHealthy([string]$why) {
  foreach ($s in Services) {
    $svc = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
    Assert ($null -ne $svc -and $svc.Status -eq 'Running') "$why : the service $($s.Name) is running"
    $account = (Get-CimInstance Win32_Service -Filter "Name='$($s.Name)'").StartName
    Write-Host "   $($s.Name) runs as $account"
    $health = Native "health-$($s.Product)" { & (Exe $s.Product) health check }
    Assert ($health.Code -eq 0) "$why : $($s.Product) answers its health check"
  }
  $listed = Native 'instance-list' { & (Exe 'dbdatakit') instance list }
  foreach ($s in Services) {
    if ($s.Product -eq 'dbdatakit') { continue }
    Assert ($listed.Text -match ("(?m)^{0}\s.*\s\sready(\s|$)" -f [regex]::Escape($s.Product))) "$why : the console lists $($s.Product) as ready"
  }
}

# --- Scoop, and the packages served on this machine as the manifest's urls
Heading 'Scoop'
if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {
  Invoke-Expression "& {$(Invoke-RestMethod https://get.scoop.sh)} -RunAsAdmin"
  $env:Path = "$env:USERPROFILE\scoop\shims;" + $env:Path
}
Assert ($null -ne (Get-Command scoop -ErrorAction SilentlyContinue)) 'scoop is installed'

Heading 'Serve every member''s zip and write the manifest'
$serve = Join-Path $Work 'serve'
$sums = @()
foreach ($m in $members) {
  $from = Join-Path $Packages "release-$($m.product)"
  $zip = Join-Path $from "$($m.product)-v$Version-win-x64.zip"
  Assert (Test-Path $zip) "$($m.product) has its win-x64 zip in the dry run"
  $to = Join-Path $serve "$($m.product)\v$Version"
  New-Item -ItemType Directory -Force $to | Out-Null
  Copy-Item $zip $to
  $sums += (Join-Path $from 'SHA256SUMS')
}
function Bind([string]$version, [string]$out) {
  # a second version: the same files under the second version's names, so the manifest of the update differs only by its version and its urls
  if ($version -ne $Version) {
    foreach ($m in $members) {
      $to = Join-Path $serve "$($m.product)\v$version"
      New-Item -ItemType Directory -Force $to | Out-Null
      Copy-Item (Join-Path $serve "$($m.product)\v$Version\$($m.product)-v$Version-win-x64.zip") (Join-Path $to "$($m.product)-v$version-win-x64.zip")
      $hash = (Get-FileHash (Join-Path $to "$($m.product)-v$version-win-x64.zip") -Algorithm SHA256).Hash.ToLower()
      Add-Content -Path (Join-Path $Work 'SHA256SUMS.second') -Value "$hash  $($m.product)-v$version-win-x64.zip"
    }
    $sumFiles = @((Join-Path $Work 'SHA256SUMS.second'))
  } else { $sumFiles = $sums }
  & python (Join-Path $repoRoot 'dist\dist.py') bind --suite (Join-Path $repoRoot 'dist\suite.json') --root $repoRoot --version $version --sums @sumFiles --base-url "http://127.0.0.1:$Port" --out $out
  if ($LASTEXITCODE -ne 0) { throw "dist.py bind --suite exited $LASTEXITCODE" }
}
$manifestDir = Join-Path $Work 'manifest'
Bind $Version $manifestDir
$manifest = Join-Path $manifestDir 'dbdatakit-full.json'
$server = Start-Process python -ArgumentList '-m', 'http.server', $Port, '--bind', '127.0.0.1', '--directory', $serve -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
  Heading 'A per-user install is refused, and leaves no service'
  $r = Native 'user-install' { scoop install $manifest }
  Assert ($r.Text -match 'scoop install -g') 'the refusal says to use scoop install -g'
  foreach ($s in Services) { Assert (-not (ServiceExists $s.Name)) "$($s.Name) is not registered after the per-user attempt" }
  Native 'user-uninstall' { scoop uninstall dbdatakit-full } | Out-Null

  Heading 'An install over a service of another folder is refused before anything changes'
  $other = Join-Path $Work 'other-route'
  New-Item -ItemType Directory -Force $other | Out-Null
  Expand-Archive (Join-Path $serve "dbdatarunner\v$Version\dbdatarunner-v$Version-win-x64.zip") $other -Force
  $otherExe = Join-Path $other 'dbdatarunner.exe'
  $r = Native 'other-route-register' { & $otherExe service install }
  Assert ($r.Code -eq 0) 'a runner is registered from another folder (as a product''s own install would)'
  $r = Native 'other-route-install' { scoop install -g $manifest }
  Assert ($r.Text -match 'registered from') 'the install names the service registered from another folder'
  Assert (-not (ServiceExists 'DbDataKit')) 'nothing else was registered by the refused install'
  Native 'other-route-uninstall' { scoop uninstall -g dbdatakit-full } | Out-Null
  Native 'other-route-unregister' { & $otherExe service uninstall } | Out-Null
  Assert (-not (ServiceExists 'DbDataRunner')) 'the other route''s service is removed again'

  Heading 'The global install registers, starts and pairs every member'
  $install = Native 'install' { scoop install -g $manifest }
  Assert ($install.Code -eq 0) 'scoop install -g succeeded'
  AssertEveryMemberRunsPairedAndHealthy 'after install'
  Assert ($install.Text -notmatch 'Pairing code:') 'no pairing code is in the install output'
  Assert (Test-Path (Join-Path $data 'server\FIRST-RUN.txt')) 'the console wrote its first-run invitation'
  $before = (Native 'instance-ids' { & (Exe 'dbdatakit') instance list --offline }).Text

  Heading 'Running the install script again changes nothing: nothing is registered or paired twice'
  # Scoop does not run post_install twice for one version, so the script is run the way Scoop runs it (its variables $dir, $global and $cmd set), as a repeat after a failure would
  $text = Get-Content $manifest -Raw | ConvertFrom-Json
  $repeat = Native 'repeat-post-install' {
    $dir = Join-Path $scoopApp $Version; $global = $true; $cmd = 'install'
    Invoke-Expression ($text.post_install -join "`n")
  }
  Assert ($repeat.Text -match 'is registered already') 'the repeat found the services registered'
  Assert ($repeat.Text -match 'is listed by the console already') 'the repeat found the members paired'
  $after = (Native 'instance-ids-again' { & (Exe 'dbdatakit') instance list --offline }).Text
  Assert ($before -eq $after) 'the console lists the same servers, with the same ids'
  AssertEveryMemberRunsPairedAndHealthy 'after the repeat'

  Heading 'An update runs the services from the new files, still paired'
  $second = "$Version.1"
  Bind $second $manifestDir
  $update = Native 'update' { scoop update dbdatakit-full -g }
  Assert ($update.Code -eq 0) 'scoop update -g succeeded'
  Assert (Test-Path (Join-Path $scoopApp $second)) "the new version folder $second exists"
  $current = (Get-Item (Join-Path $scoopApp 'current')).Target
  Write-Host "   current -> $current"
  AssertEveryMemberRunsPairedAndHealthy 'after the update'
  $afterUpdate = (Native 'instance-ids-update' { & (Exe 'dbdatakit') instance list --offline }).Text
  Assert ($before -eq $afterUpdate) 'the update did not pair anything again'

  Heading 'An uninstall removes the services and the pairings, and keeps the data'
  $copy = Join-Path $Work 'console-copy'
  Copy-Item (Join-Path $scoopApp 'current\console') $copy -Recurse     # the console's files go with the uninstall; its list is read from a copy
  $remove = Native 'uninstall' { scoop uninstall -g dbdatakit-full }
  Assert ($remove.Code -eq 0) 'scoop uninstall -g succeeded'
  foreach ($s in Services) { Assert (-not (ServiceExists $s.Name)) "$($s.Name) no longer exists" }
  $listed = Native 'instance-list-after' { & (Join-Path $copy 'dbdatakit.exe') instance list --offline }
  foreach ($s in Services) { if ($s.Product -ne 'dbdatakit') { Assert ($listed.Text -notmatch [regex]::Escape($s.Product)) "the console no longer lists $($s.Product)" } }
  Assert (Test-Path $data) 'the settings and data under %ProgramData% remain'
  Write-Host 'Suite install end-to-end: OK'
}
catch {
  Diagnose
  throw
}
finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
  # leave nothing behind on a failure
  & scoop uninstall -g dbdatakit-full 2>&1 | Out-Null
  foreach ($s in Services) { if (ServiceExists $s.Name) { & sc.exe stop $s.Name | Out-Null; Start-Sleep -Seconds 2; & sc.exe delete $s.Name | Out-Null } }
}
$global:LASTEXITCODE = 0
exit 0
