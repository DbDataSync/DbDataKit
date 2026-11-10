# Installs the Windows service from the build output, starts it, checks it answers, restarts it, stops it and removes it again, all through
# `dbdatasync service ...` (the Service Control Manager, for real). Run from an elevated prompt, as a windows-latest runner is.
# Usage: pwsh service-e2e.ps1 -Exe <path to the built dbdatasync executable>
param([Parameter(Mandatory)][string]$Exe)

$ErrorActionPreference = 'Stop'
$ServiceName = 'DbDataSync'
$Repo = Join-Path ($env:RUNNER_TEMP ?? $env:TEMP) 'dbdatasync-service-e2e'
$Url = 'https://localhost:5080'   # the default: the service makes a self-signed certificate in $Repo\tls on its first start

function Run([string[]]$cliArgs, [int[]]$ok = @(0)) {
  Write-Host "> dbdatasync $($cliArgs -join ' ')"
  & $Exe @cliArgs
  $code = $LASTEXITCODE
  Write-Host "  exit $code"
  if ($ok -notcontains $code) { throw "dbdatasync $($cliArgs -join ' ') exited $code (expected $($ok -join '/'))." }
}

function WaitHealthy([int]$seconds = 180) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) {
    try {
      # the tool's own check, which trusts the certificate file the server wrote and nothing else
      & $Exe health check --repo $Repo
      if ($LASTEXITCODE -eq 0) { Write-Host "  healthy: $Url"; return }
    } catch { }
    Start-Sleep -Seconds 2
  }
  Write-Host '--- service state ---'; & sc.exe query $ServiceName
  Write-Host '--- recent Application log ---'
  Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -like '*DbDataSync*' -or $_.ProviderName -like '*.NET*' } | Select-Object -First 15 |
    ForEach-Object { Write-Host ("{0} {1}: {2}" -f $_.TimeCreated.ToString('HH:mm:ss'), $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  throw "The service did not answer $Url/api/health within $seconds s."
}

if (-not (Test-Path $Exe)) { throw "No executable at $Exe." }
Remove-Item -Recurse -Force $Repo -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Repo | Out-Null

try {
  Run @('service', 'status') @(1)                       # nothing installed yet
  Run @('service', 'install', '--repo', $Repo)
  Run @('service', 'install', '--repo', $Repo)          # again: harmless
  Run @('service', 'status')
  Run @('service', 'doctor', '--repo', $Repo)
  $qc = & sc.exe qc $ServiceName | Out-String
  Write-Host $qc
  if ($qc -notmatch 'AUTO_START' -or $qc -match 'DELAYED') { throw 'The service should start automatically (not delayed).' }
  if ($qc -notmatch 'LocalSystem') { throw 'The service should run as LocalSystem by default.' }

  Run @('service', 'start')
  WaitHealthy
  Run @('service', 'status')
  Run @('service', 'restart')
  WaitHealthy
  Run @('service', 'stop')
  if ((Get-Service $ServiceName).Status -ne 'Stopped') { throw 'The service is not stopped after `service stop`.' }

  Run @('service', 'uninstall', '--repo', $Repo)
  Run @('service', 'status') @(1)
  if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { throw 'The service still exists after uninstall.' }
  Write-Host 'Service end-to-end: OK'
}
catch {
  # A service that fails to start says why only in the Windows event log (error 1053 from the service manager says nothing else): print it.
  Write-Host "--- failure: $($_.Exception.Message) ---"
  Write-Host '--- service state ---'; & sc.exe query $ServiceName
  Write-Host '--- recent Application and System log ---'
  foreach ($log in 'Application', 'System') {
    Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -like '*DbDataSync*' -or $_.ProviderName -like '*.NET*' -or $_.ProviderName -like 'Service Control Manager' -or $_.ProviderName -like 'Application Error' } |
      Select-Object -First 15 |
      ForEach-Object { Write-Host ("{0} {1} {2}: {3}" -f $_.TimeCreated.ToString('HH:mm:ss'), $log, $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  }
  Write-Host "--- $Repo ---"; Get-ChildItem -Recurse -Force $Repo -ErrorAction SilentlyContinue | Select-Object -First 40 | ForEach-Object { Write-Host $_.FullName }
  throw
}
finally {
  # Leave nothing behind on a failure.
  if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 3
    & sc.exe delete $ServiceName | Out-Null
  }
}

# The last command run was `service status` on a removed service, which correctly exits 1; PowerShell hands the last native
# command's exit code to the caller, so say explicitly that the script as a whole succeeded.
$global:LASTEXITCODE = 0
exit 0
