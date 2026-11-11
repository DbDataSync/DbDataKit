<#
.SYNOPSIS
  Installs the console's MSI and a second product's MSI with ANNOUNCEURL (plan 33 SC10), checks the machine variable the property sets, starts the console with its private
  announce port and the product with that variable as a new terminal would read it, and checks the product waits in `instance pending`, not paired and not trusted.
  Run by .github/workflows/dist-msi-ci.yml. Written without a Windows machine: its first run is its proof. The product is given as an executable built from this commit (as
  msi-pairing-e2e.ps1 does) for the run, and its MSI for the install; the MSI must be of a product whose dist.json has `announces`.
#>
param(
  [Parameter(Mandatory)][string]$ConsoleMsi,
  [Parameter(Mandatory)][string]$ProductMsi,
  [Parameter(Mandatory)][string]$ProductExe,           # built from this commit
  [string]$ProductProduct = 'dbdatabuild',
  [int]$ProductPort = 8470,
  [int]$ConsolePort = 8480,
  [int]$AnnouncePort = 8481,
  [string]$LogDir = $env:RUNNER_TEMP
)
$ErrorActionPreference = 'Stop'
$work = Join-Path $LogDir 'pairing-announce'
New-Item -ItemType Directory -Force $work | Out-Null
$var = 'DBDATAKIT_ANNOUNCE_URL'
$announceUrl = "http://127.0.0.1:$AnnouncePort"

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function Msi([string]$file, [string]$name, [string]$extra) {
  $log = Join-Path $LogDir "pairing-announce-$name.log"
  $p = Start-Process msiexec.exe -ArgumentList "/i `"$file`" INSTALLSERVICE=0 $extra /qn /norestart /l*v `"$log`"" -Wait -PassThru
  return @{ Code = $p.ExitCode; Log = $log }
}
function Run([string]$exe, [string[]]$arguments) {
  $out = & $exe @arguments 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') exited ${LASTEXITCODE}: $out" }
  $out
}

Write-Host '=== a bad address is refused and changes nothing'
[Environment]::SetEnvironmentVariable($var, $null, 'Machine')
$bad = Msi $ProductMsi 'bad' 'ANNOUNCEURL=not-an-address'
Assert ($bad.Code -ne 0 -and $bad.Code -ne 3010) 'the installer refuses an ANNOUNCEURL that is not an http or https address'
Assert (-not [Environment]::GetEnvironmentVariable($var, 'Machine')) 'the refused install set no variable'

Write-Host '=== install both MSIs, the product with the console''s announce address'
$c = Msi $ConsoleMsi 'console' ''
Assert ($c.Code -in 0, 3010, 1638) 'the console is installed (1638: the pairing step of this job installed it already)'
$p = Msi $ProductMsi 'product' "ANNOUNCEURL=$announceUrl"
if ($p.Code -notin 0, 3010) { Get-Content $p.Log -Tail 120 | Write-Host; throw "the product's MSI exited $($p.Code)" }
Assert ([Environment]::GetEnvironmentVariable($var, 'Machine') -eq $announceUrl) "the machine variable $var is $announceUrl"

Write-Host '=== the product announces itself and waits in the pending list'
$console = Join-Path $env:ProgramFiles 'DbDataKit\dbdatakit.exe'
$consoleData = Join-Path $work 'console'; $productData = Join-Path $work 'product'
$consoleUrl = "https://127.0.0.1:$ConsolePort"; $productUrl = "https://127.0.0.1:$ProductPort"
$p2 = Start-Process $console -ArgumentList 'server', 'run', '--repo', "`"$consoleData`"", '--listen', $consoleUrl, '--public-url', $consoleUrl, '--announce-listen', $announceUrl -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $work 'console.out') -RedirectStandardError (Join-Path $work 'console.err')
$p1 = $null
try {
  for ($i = 0; $i -lt 40; $i++) { & $console health check --url "$consoleUrl/health/live" 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Seconds 2 }
  # what a new terminal or the service reads: the machine variable the installer set
  $env:DBDATAKIT_ANNOUNCE_URL = [Environment]::GetEnvironmentVariable($var, 'Machine')
  $env:DBDATAKIT_ADVERTISE_URL = $productUrl
  $p1 = Start-Process $ProductExe -ArgumentList 'server', 'run', '--repo', "`"$productData`"", '--listen', $productUrl -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $work 'product.out') -RedirectStandardError (Join-Path $work 'product.err')
  $waiting = ''
  for ($i = 0; $i -lt 60 -and $waiting -notmatch $ProductProduct; $i++) { Start-Sleep -Seconds 2; $waiting = Run $console @('instance', 'pending', '--repo', $consoleData) }
  Assert ($waiting -match $ProductProduct) "the product is in 'instance pending':`n$waiting"
  $listed = Run $console @('instance', 'list', '--offline', '--repo', $consoleData)
  Assert ($listed -notmatch $ProductProduct) 'the console has not paired it: a human confirms'
}
finally {
  foreach ($p in $p1, $p2) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
  foreach ($f in 'product.out', 'product.err', 'console.out', 'console.err') { $path = Join-Path $work $f; if (Test-Path $path) { Write-Host "--- $f"; Get-Content $path -Tail 40 | Write-Host } }
  [Environment]::SetEnvironmentVariable($var, $null, 'Machine')
}
Write-Host 'All announcement checks passed.'
$global:LASTEXITCODE = 0
exit 0
