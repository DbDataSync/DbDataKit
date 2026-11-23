<#
.SYNOPSIS
  `dbdatakit setup single-server` on a real Windows machine (docs/install.md, "Setting up one machine, or inviting another"), from the dry run's zips,
  extracted as a person would, with no Scoop and no MSI: each member's win-x64 zip in a folder of its own under one program folder
  (%ProgramFiles%\DbDataKit\console, gateway, runner, workspace). It checks that:
  - one run registers, starts and joins every service: four progress lines reach their last stage, each service runs as NT SERVICE\<name> from its
    extracted program, the console lists the three servers in its instances.yaml, and the summary prints the console's address and the setup link;
  - each program folder's access list cuts the parent's grants and gives Users no write;
  - a second run says it has nothing to do;
  - with the Runner's configured port held by another program, the Runner's line stops at `started`, its startup-failure file is printed and the summary
    names it; a run after the port is freed finishes it;
  - a run with --skip-folder-permissions-check puts the switch on each registered command line;
  - `setup single-server --stop` stops every service.
  Run by the job `suite-single-server-windows` of .github/workflows/suite-release.yml (a dry run), as an elevated user. Written without a Windows
  machine: its first run is its proof.
  -Packages is the folder the dry run's artifacts were downloaded to (release-<product>/ folders, each with the zip and SHA256SUMS).
#>
param(
  [Parameter(Mandatory)][string]$Packages,
  [Parameter(Mandatory)][string]$Version,
  [string]$Work = (Join-Path $env:RUNNER_TEMP 'suite-single-server')
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$suite = Get-Content (Join-Path $repoRoot 'dist\suite.json') -Raw | ConvertFrom-Json
$members = @($suite.members)
$programs = Join-Path $env:ProgramFiles 'DbDataKit'
$data = Join-Path $env:ProgramData 'DbDataKit'
$log = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force $Work, $log | Out-Null
# the services in start order: program, Windows service, data folder (SuiteServices)
$services = @(
  @{ Program = 'dbdatakit'; Service = 'DbDataKit'; Data = 'server' },
  @{ Program = 'dbdatagateway'; Service = 'DbDataGateway'; Data = 'gateway' },
  @{ Program = 'dbdatarunner'; Service = 'DbDataRunner'; Data = 'runner' },
  @{ Program = 'dbdataworkspace'; Service = 'DbDataWorkspace'; Data = 'workspace' }
)

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function Heading([string]$text) { Write-Host "=== $text" }
$step = 0
# `dbdatakit setup single-server` with the given arguments, its output written to a file and shown; returns the exit code and the text
function Setup([string]$name, [string[]]$arguments) {
  $script:step++
  $file = Join-Path $log ("{0:00}-{1}.txt" -f $script:step, $name)
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & (Join-Path $programs 'console\dbdatakit.exe') setup single-server @arguments *>&1 | Tee-Object -FilePath $file | Out-Host; $code = $LASTEXITCODE }
  finally { $ErrorActionPreference = $old }
  if (-not (Test-Path -LiteralPath $file)) { New-Item -ItemType File -Path $file | Out-Null }
  return [pscustomobject]@{ Code = $code; Text = (Get-Content $file -Raw) }
}
function Line([string]$text, [string]$program) { return ([regex]::Match($text, "(?m)^$program +(.*?)\s*$")).Groups[1].Value }
function ListenPort([string]$file) { return [int]([regex]::Match((Get-Content $file -Raw), '(?m)^listen:\s*https://[^\s]*?:(\d+)')).Groups[1].Value }
function Diagnose {
  foreach ($s in $services) { & sc.exe qc $s.Service 2>&1 | Out-Host; & sc.exe query $s.Service 2>&1 | Out-Host }
  Get-ChildItem (Join-Path $data 'logs') -Filter 'startup-failure-*.txt' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "## $($_.FullName)"; Get-Content $_.FullName | Out-Host }
  Get-EventLog -LogName Application -Newest 30 -ErrorAction SilentlyContinue | Where-Object { $_.EntryType -ne 'Information' } | Format-List TimeGenerated, Source, Message | Out-Host
}

try {
  Heading 'Extract every member''s zip into a folder of its own'
  foreach ($m in $members) {
    $zip = Join-Path $Packages "release-$($m.product)\$($m.product)-v$Version-win-x64.zip"
    Assert (Test-Path $zip) "$($m.product) has its win-x64 zip in the dry run"
    Expand-Archive $zip (Join-Path $programs $m.folder) -Force
  }

  Heading 'One run registers, starts and joins every service'
  $first = Setup 'first' @()
  Assert ($first.Code -eq 0) "setup single-server exits 0 (it exited $($first.Code))"
  Assert ((Line $first.Text 'dbdatakit') -eq 'registered · started · listening') "the console's line reaches listening"
  foreach ($p in 'dbdatagateway', 'dbdatarunner', 'dbdataworkspace') { Assert ((Line $first.Text $p) -eq 'registered · started · listening · joined') "$p's line reaches joined" }
  Assert ($first.Text -match '(?m)^The console is on https://\S+:\d+\.\s*$') 'the summary prints the console address'
  Assert ($first.Text -match 'https://\S+/invite#\S+') 'the summary prints the first administrator''s setup link'
  foreach ($s in $services) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$($s.Service)'"
    Assert ($null -ne $service -and $service.State -eq 'Running') "$($s.Service) is registered and running"
    Assert ($service.StartName -eq "NT SERVICE\$($s.Service)") "$($s.Service) runs as NT SERVICE\$($s.Service) (it runs as $($service.StartName))"
    $folder = ($members | Where-Object { $_.product -eq $s.Program }).folder
    Assert ($service.PathName.StartsWith("`"$(Join-Path $programs "$folder\$($s.Program).exe")`"", [StringComparison]::OrdinalIgnoreCase)) "$($s.Service) runs the extracted program ($($service.PathName))"
  }
  $instances = Get-Content (Join-Path $data 'server\instances.yaml') -Raw
  Assert (([regex]::Matches($instances, '(?m)^\s*-\s*\{id:')).Count -eq 3) 'the console lists the three servers in instances.yaml'
  foreach ($d in 'runner', 'workspace') { $port = ListenPort (Join-Path $data "$d\server.yaml"); Assert ($instances -match ":$port") "the console lists the $d at its port $port" }

  Heading 'The program folders: the parent''s grants cut, no write for Users'
  $users = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([Security.Principal.NTAccount]).Value
  # only the bits that change something: FullControl and Modify also hold the read and run bits (and Synchronize) that Users is meant to have
  $write = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, ChangePermissions, TakeOwnership'
  foreach ($m in $members) {
    $acl = Get-Acl (Join-Path $programs $m.folder)
    Assert $acl.AreAccessRulesProtected "$($m.folder)'s access list does not inherit from its parent"
    $granted = @($acl.Access | Where-Object { $_.IdentityReference.Value -eq $users -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band $write) })
    Assert ($granted.Count -eq 0) "Users cannot write to $($m.folder) ($(($acl.Access | ForEach-Object { "$($_.IdentityReference) $($_.FileSystemRights)" }) -join '; '))"
  }

  Heading 'A second run has nothing to do'
  $second = Setup 'second' @()
  Assert ($second.Code -eq 0) "the second run exits 0 (it exited $($second.Code))"
  Assert ($second.Text.Contains('Nothing to do: every service is registered, running and joined.')) 'the second run says it has nothing to do'

  Heading 'A service whose configured port is held'
  $port = ListenPort (Join-Path $data 'runner\server.yaml')
  Stop-Service DbDataRunner
  $holder = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Any), $port
  $holder.Server.ExclusiveAddressUse = $true
  $holder.Start()
  try {
    $held = Setup 'held' @()
    Assert ($held.Code -eq 1) "the run with the Runner's port held exits 1 (it exited $($held.Code))"
    Assert ((Line $held.Text 'dbdatarunner') -eq 'registered · started') "the Runner's line stops at started"
    Assert ($held.Text.Contains('startup-failure-dbdatarunner.txt (the last start failed):')) 'the Runner''s startup-failure file is printed'
    Assert ($held.Text.Contains([string]$port)) "the printed file names the port $port"
    Assert ($held.Text.Contains('Not ready: dbdatarunner')) 'the summary names the Runner as not ready'
    Assert ((Line $held.Text 'dbdataworkspace') -eq 'registered · started · listening · joined (already)') 'the Workspace is left alone'
  } finally { $holder.Stop() }
  $fixed = Setup 'fixed' @()
  Assert ($fixed.Code -eq 0) "the run after the fix exits 0 (it exited $($fixed.Code))"
  Assert ((Line $fixed.Text 'dbdatarunner').StartsWith('registered · started · listening · joined')) 'the run after the fix finishes the Runner'

  Heading '--skip-folder-permissions-check is put on each registered command line'
  $skip = Setup 'skip' @('--skip-folder-permissions-check')
  Assert ($skip.Code -eq 0) "the run with --skip-folder-permissions-check exits 0 (it exited $($skip.Code))"
  foreach ($s in $services) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$($s.Service)'"
    Assert ($service.PathName.EndsWith(' --skip-folder-permissions-check')) "$($s.Service) is registered with the switch ($($service.PathName))"
    Assert ($service.State -eq 'Running') "$($s.Service) runs again"
  }

  Heading '--stop stops every service'
  $stop = Setup 'stop' @('--stop')
  Assert ($stop.Code -eq 0) "setup single-server --stop exits 0 (it exited $($stop.Code))"
  foreach ($s in $services) { Assert ((Get-Service $s.Service).Status -eq 'Stopped') "$($s.Service) is stopped" }
  Write-Host 'setup single-server on Windows: OK'
}
catch { Diagnose; throw }
finally {
  foreach ($s in $services) { & sc.exe stop $s.Service 2>&1 | Out-Null; & sc.exe delete $s.Service 2>&1 | Out-Null }
}
