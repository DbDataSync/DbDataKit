<#
.SYNOPSIS
  Installs the console's MSI, pairs it with a product server using the pairing code the product prints, and checks the console lists the product as
  ready and keeps its API closed to anyone not signed in. Run by .github/workflows/dist-msi-ci.yml (plan 13a, L04: separate MSIs, pairing is a step
  after both are installed). The product is given as an executable, built from this commit: the newest published package can predate the `server`
  command, and the product's own MSI is tested by the install, upgrade and removal jobs. Pass -ProductMsi to install it first once a release has it. The passkey sign-in itself needs a browser: DbDataConsole's Playwright suite does that.
#>
param(
  [Parameter(Mandatory)][string]$ConsoleMsi,
  [string]$ProductMsi = '',
  [string]$ProductDisplayName = '',                    # DbDataBuild: where -ProductMsi installs it
  [Parameter(Mandatory)][string]$ProductExe,           # the product's executable: the installed one with -ProductMsi (a file name), else a full path
  [int]$ProductPort = 8470,
  [int]$ConsolePort = 8480,
  [string]$LogDir = $env:RUNNER_TEMP
)
$ErrorActionPreference = 'Stop'
$consoleDir = Join-Path $env:ProgramFiles 'DbDataKit'
$work = Join-Path $LogDir 'pairing'
New-Item -ItemType Directory -Force $work | Out-Null

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function Msi([string]$file, [string]$name) {
  $log = Join-Path $LogDir "pairing-$name.log"
  $p = Start-Process msiexec.exe -ArgumentList "/i `"$file`" INSTALLSERVICE=0 /qn /norestart /l*v `"$log`"" -Wait -PassThru
  if ($p.ExitCode -notin 0, 3010) { Get-Content $log -Tail 120 | Write-Host; throw "msiexec /i $file exited $($p.ExitCode)" }
}
function Run([string]$exe, [string[]]$arguments) {
  $out = & $exe @arguments 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') exited ${LASTEXITCODE}: $out" }
  $out
}
function WaitHealthy([string]$exe, [string]$url) {
  for ($i = 0; $i -lt 40; $i++) {
    & $exe health check --url $url 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { return }
    Start-Sleep -Seconds 2
  }
  throw "$url did not become healthy"
}

Write-Host '=== install the MSIs'
Msi $ConsoleMsi 'console'
if ($ProductMsi) { Msi $ProductMsi 'product'; $ProductExe = Join-Path (Join-Path $env:ProgramFiles $ProductDisplayName) $ProductExe }
$console = Join-Path $consoleDir 'dbdatakit.exe'
$product = $ProductExe
Assert (Test-Path $console) "the console is in $consoleDir"
Assert (Test-Path (Join-Path $consoleDir 'wwwroot\index.html')) 'the console has its web app'
Assert (Test-Path $product) "the product is at $product"

Write-Host '=== start the product and the console'
$productDirData = Join-Path $work 'product'; $consoleDirData = Join-Path $work 'console'
$productUrl = "https://127.0.0.1:$ProductPort"; $consoleUrl = "https://127.0.0.1:$ConsolePort"
$made = Run $product @('server', 'pair', '--repo', $productDirData, '--max-role', 'viewer')
$code = [regex]::Match($made, 'Pairing code: (\S+)').Groups[1].Value
Assert ($code.Length -gt 0) 'the product printed a pairing code'
$p1 = Start-Process $product -ArgumentList 'server', 'run', '--repo', "`"$productDirData`"", '--listen', $productUrl -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $work 'product.out') -RedirectStandardError (Join-Path $work 'product.err')
$p2 = Start-Process $console -ArgumentList 'server', 'run', '--repo', "`"$consoleDirData`"", '--listen', $consoleUrl, '--public-url', $consoleUrl -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $work 'console.out') -RedirectStandardError (Join-Path $work 'console.err')
try {
  WaitHealthy $product $productUrl
  WaitHealthy $console $consoleUrl

  Write-Host '=== pair them'
  Run $console @('instance', 'add', $productUrl, '--code', $code, '--repo', $consoleDirData) | Out-Null
  $listed = Run $console @('instance', 'list', '--repo', $consoleDirData)
  Assert ($listed -match 'ready') "the console lists the product as ready:`n$listed"

  Write-Host '=== the console is closed to anyone not signed in, and can invite the first administrator'
  $status = try { (Invoke-WebRequest "$consoleUrl/api/users" -SkipCertificateCheck -SkipHttpErrorCheck).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
  Assert ($status -eq 401) "/api/users answers 401 (got $status)"
  $invite = Run $console @('user', 'invite', '--url', $consoleUrl, '--repo', $consoleDirData)
  Assert ($invite -match '/invite#') 'an invitation link is made'
}
finally {
  foreach ($p in $p1, $p2) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
  foreach ($f in 'product.out', 'product.err', 'console.out', 'console.err') { $path = Join-Path $work $f; if (Test-Path $path) { Write-Host "--- $f"; Get-Content $path -Tail 40 | Write-Host } }
}
Write-Host 'All pairing checks passed.'
$global:LASTEXITCODE = 0
exit 0
