# Installs the Windows service from a published dbdatarunner, starts it, checks it answers, restarts it, stops it and removes it again, all through
# `dbdatarunner service ...` (the Service Control Manager, for real) and the account the runner is meant to run under, NT SERVICE\DbDataRunner (plan 28 section 5:
# the tasks it starts run as that account). Run from an elevated prompt, as a windows-latest runner is. Plan 28a R27.
# Usage: pwsh service-e2e.ps1 -Exe <path to the published dbdatarunner.exe>
# The directory the exe is in gets the install ACL (administrators and SYSTEM full, the service read and run): give it a copy, not the build output.
param([Parameter(Mandatory)][string]$Exe)

$ErrorActionPreference = 'Stop'
$ServiceName = 'DbDataRunner'
$Account = 'NT SERVICE\DbDataRunner'
$Data = Join-Path $env:ProgramData 'DbDataRunner\server'

function Run([string[]]$cliArgs, [int[]]$ok = @(0)) {
  Write-Host "> dbdatarunner $($cliArgs -join ' ')"
  & $Exe @cliArgs
  $code = $LASTEXITCODE
  Write-Host "  exit $code"
  if ($ok -notcontains $code) { throw "dbdatarunner $($cliArgs -join ' ') exited $code (expected $($ok -join '/'))." }
}

function Diagnose {
  Write-Host '--- service state ---'; & sc.exe query $ServiceName
  Write-Host '--- service configuration ---'; & sc.exe qc $ServiceName
  Write-Host '--- the data directory ---'
  if (Test-Path $Data) { Get-ChildItem $Data -Force | Format-Table Name, Length, LastWriteTime | Out-String | Write-Host; & icacls.exe $Data }
  Write-Host '--- recent Application and System log ---'
  foreach ($log in 'Application', 'System') {
    Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -like '*DbDataRunner*' -or $_.ProviderName -like '*.NET*' -or $_.ProviderName -like 'Service Control Manager' } | Select-Object -First 20 |
      ForEach-Object { Write-Host ("{0} {1}: {2}" -f $_.TimeCreated.ToString('HH:mm:ss'), $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  }
}

# `health check` is the program's own client of its own address (https://localhost:8495/health/live, a self-signed certificate accepted on the loopback).
function WaitHealthy([int]$seconds = 120) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    & $Exe health check 2>&1 | Out-Host
    if ($LASTEXITCODE -eq 0) { return }
    Start-Sleep -Seconds 3
  }
  Diagnose
  throw "The service did not answer its health check within $seconds s."
}

function RunsAs {
  (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartName
}

if (-not (Test-Path $Exe)) { throw "No executable at $Exe." }
if (-not (Test-Path (Join-Path (Split-Path $Exe) 'taskhost\dbdatakit-taskhost.dll'))) { throw 'The published program has no taskhost\dbdatakit-taskhost.dll beside it.' }

try {
  Run @('service', 'status') @(1)                       # nothing installed yet
  Run @('service', 'install', '--dry-run')              # says what it would do, does it not
  Run @('service', 'status') @(1)
  Run @('service', 'install')
  Run @('service', 'install')                           # again: harmless
  Run @('service', 'status')
  Run @('service', 'doctor')
  $qc = & sc.exe qc $ServiceName | Out-String
  Write-Host $qc
  if ($qc -notmatch 'AUTO_START' -or $qc -match 'DELAYED') { throw 'The service should start automatically (not delayed).' }
  if ((RunsAs) -ne $Account) { throw "The service should run as $Account, it runs as '$(RunsAs)'." }

  Run @('service', 'start')
  WaitHealthy
  Run @('service', 'status')
  Run @('service', 'doctor')
  Run @('service', 'restart')
  WaitHealthy
  Run @('service', 'stop')
  if ((Get-Service $ServiceName).Status -ne 'Stopped') { throw 'The service is not stopped after `service stop`.' }

  Run @('service', 'uninstall')
  Run @('service', 'status') @(1)
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
  & sc.exe query $ServiceName 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 1060) { throw 'The service still exists after uninstall.' }
  Write-Host 'Service end-to-end: OK'
}
catch {
  Diagnose
  throw
}
finally {
  # Leave nothing behind on a failure.
  & sc.exe query $ServiceName 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 1060) {
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe delete $ServiceName | Out-Null
  }
}

# The last command run was `service status` on a removed service, which correctly exits 1; PowerShell hands the last native
# command's exit code to the caller, so say explicitly that the script as a whole succeeded.
$global:LASTEXITCODE = 0
exit 0
