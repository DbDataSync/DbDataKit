<#
.SYNOPSIS
  Installs, upgrades and removes a product's MSI on a real Windows machine and checks what each step left behind. Run by .github/workflows/dist-msi-ci.yml.
#>
param(
  [Parameter(Mandatory)][string]$Msi1,          # the older version
  [Parameter(Mandatory)][string]$Msi2,          # a newer version of the same product (its package also holds UPGRADED.txt)
  [Parameter(Mandatory)][string]$DisplayName,   # the Program Files folder and the Add/Remove Programs name
  [Parameter(Mandatory)][string]$Exe,
  [string]$ServiceName = '',
  [switch]$OnPath,
  [switch]$ServiceRuns,                         # the service starts with no configuration at all
  [string]$LogDir = $env:RUNNER_TEMP
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:ProgramFiles $DisplayName
$step = 0

function Msi([string]$arguments) {
  $script:step++
  $log = Join-Path $LogDir ("{0}-step{1}.log" -f $DisplayName, $script:step)
  $p = Start-Process msiexec.exe -ArgumentList "$arguments /qn /norestart /l*v `"$log`"" -Wait -PassThru
  if ($p.ExitCode -notin 0, 3010) {
    Get-Content $log -Tail 120 | Write-Host
    throw "msiexec $arguments exited $($p.ExitCode) (log: $log)"
  }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function MachinePath { [Environment]::GetEnvironmentVariable('Path', 'Machine') }
function OnMachinePath { (MachinePath -split ';' | ForEach-Object { $_.TrimEnd('\') }) -contains $dir }
function ArpEntries { @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq $DisplayName }) }
function Svc { if ($ServiceName) { Get-Service -Name $ServiceName -ErrorAction SilentlyContinue } }
function EventsAbout([int]$minutes = 10) {
  $since = (Get-Date).AddMinutes(-$minutes)
  foreach ($log in 'System', 'Application') {
    Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $since } -ErrorAction SilentlyContinue |
      Where-Object { $_.Message -like "*$ServiceName*" -or $_.ProviderName -like "*$ServiceName*" -or $_.ProviderName -in '.NET Runtime', 'Application Error' } |
      Select-Object -First 15 | ForEach-Object { Write-Host ("[{0}] {1} {2} {3}" -f $log, $_.TimeCreated.ToString('HH:mm:ss'), $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  }
}
# Waits for the service to be running; when it is not, says why (a manual start's error, and the events from the last minutes) before failing.
function AssertRunning([string]$why) {
  try { (Get-Service $ServiceName).WaitForStatus('Running', '00:00:30') } catch { }
  if ((Get-Service $ServiceName).Status -ne 'Running') {
    Write-Host "--- $why : not running, status $((Get-Service $ServiceName).Status). Starting it by hand:"
    try { Start-Service $ServiceName; Write-Host 'started by hand' } catch { Write-Host "Start-Service failed: $($_.Exception.Message)" }
    EventsAbout
  }
  Assert ((Get-Service $ServiceName).Status -eq 'Running') $why
}
function ServicePath { (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName }

Write-Host "=== INSTALLSERVICE=0 installs the files and no service"
Msi "/i `"$Msi1`" INSTALLSERVICE=0"
Assert (Test-Path (Join-Path $dir $Exe)) "$Exe is in $dir"
if ($ServiceName) { Assert ($null -eq (Svc)) "no service was registered" }
Msi "/x `"$Msi1`""
Assert (-not (Test-Path $dir)) "the folder is gone after removal"

Write-Host "=== fresh install"
Msi "/i `"$Msi1`""
Assert (Test-Path (Join-Path $dir $Exe)) "$Exe is in $dir"
Assert ((ArpEntries).Count -eq 1) "one Add/Remove Programs entry"
if ($OnPath) { Assert (OnMachinePath) "$dir is on the machine PATH" }
if ($ServiceName) {
  $svc = Svc
  Assert ($null -ne $svc) "the $ServiceName service is registered"
  Assert ($svc.StartType -eq 'Automatic') "it starts automatically"
  Assert ($svc.Status -eq 'Stopped') "it is left stopped so it can be configured first"
  Assert ((ServicePath) -like "*$Exe*") "it runs $Exe"
}

if ($ServiceName -and $ServiceRuns) {
  Write-Host "=== the service starts with no configuration"
  Start-Service $ServiceName
  AssertRunning "it is running"
}
$wasRunning = $ServiceName -and ((Svc).Status -eq 'Running')
$pathBefore = if ($ServiceName) { ServicePath } else { '' }

Write-Host "=== upgrade over the running install"
Msi "/i `"$Msi2`""
Assert (Test-Path (Join-Path $dir 'UPGRADED.txt')) "the newer package's files are in place"
Assert ((ArpEntries).Count -eq 1) "still one Add/Remove Programs entry"
if ($OnPath) { Assert (OnMachinePath) "still on the machine PATH" ; Assert (((MachinePath -split ';') | Where-Object { $_.TrimEnd('\') -eq $dir }).Count -eq 1) "and only once" }
if ($ServiceName) {
  Assert ($null -ne (Svc)) "the service is still registered"
  Assert ((ServicePath) -eq $pathBefore) "with the same command line (an upgrade keeps the registration)"
  if ($wasRunning) { AssertRunning "it was running before, so it is running again" }
}

Write-Host "=== a downgrade is refused"
$p = Start-Process msiexec.exe -ArgumentList "/i `"$Msi1`" /qn /norestart" -Wait -PassThru
Assert ($p.ExitCode -ne 0) "installing the older version over the newer one fails (exit $($p.ExitCode))"
Assert (Test-Path (Join-Path $dir 'UPGRADED.txt')) "and the newer files stay"

Write-Host "=== removal"
Msi "/x `"$Msi2`""
Assert (-not (Test-Path $dir)) "the program folder is gone"
Assert ((ArpEntries).Count -eq 0) "no Add/Remove Programs entry"
if ($ServiceName) { Assert ($null -eq (Svc)) "the service is unregistered" }
if ($OnPath) { Assert (-not (OnMachinePath)) "the folder is off the machine PATH" }
Write-Host "All checks passed for $DisplayName."
