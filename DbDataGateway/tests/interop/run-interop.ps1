<#
.SYNOPSIS
  Runs standard non-.NET Flight SQL clients (ADBC and pyarrow) against the published gateway.
.DESCRIPTION
  Needs Python 3.10-3.13 with: pip install pyarrow duckdb adbc-driver-flightsql adbc-driver-manager
  (very new Python versions may lack wheels for duckdb/adbc). Build first with ./build/publish.ps1.
.EXAMPLE
  ./tests/interop/run-interop.ps1 -Python C:\path\to\venv\Scripts\python.exe
#>
param(
  [Parameter(Mandatory)][string]$Python,
  [string]$Published = (Join-Path $PSScriptRoot '..\..\artifacts\publish\win-x64'),
  [int]$Port = 18817
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path $Published) 'dbdatagateway.exe'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbgateway-interop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$db = Join-Path $tmp 'interop.duckdb'
& $Python (Join-Path $PSScriptRoot 'seed.py') $db

$env:DBGATEWAY_Gateway__DataDirectory = $tmp
$env:DBGATEWAY_Gateway__Listen__Port = "$Port"
$env:DBGATEWAY_Gateway__DataSources__duck__Provider = 'duckdb'
$env:DBGATEWAY_Gateway__DataSources__duck__ConnectionString = "Data Source=$db"
$env:DBGATEWAY_Gateway__DataSources__duck__AllowWrites = 'true'
# who may use a data source is a matter of the identity home's groups (P13); the client is a service account of the home, so the source is open to everyone
$env:DBGATEWAY_Gateway__DataSources__duck__Everyone = 'true'
& $exe identity service-account-add --name py --repo $tmp | Out-Null
$made = (& $exe identity token-create --account py --name interop --repo $tmp | Out-String)
$token = [regex]::Match($made, 'ddk_[A-Za-z0-9_-]+').Value
if (-not $token) { throw 'no token was made for the interop service account' }
$gateway = Start-Process $exe -ArgumentList 'server','run' -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $tmp 'out.log') -RedirectStandardError (Join-Path $tmp 'err.log')
try {
  Start-Sleep 5
  & $Python (Join-Path $PSScriptRoot 'interop.py') $Port py $token
  $code = $LASTEXITCODE
} finally {
  Stop-Process -Id $gateway.Id -Force
}
exit $code
