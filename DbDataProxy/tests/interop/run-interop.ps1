<#
.SYNOPSIS
  Runs standard non-.NET Flight SQL clients (ADBC and pyarrow) against the published proxy.
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
$exe = Join-Path (Resolve-Path $Published) 'dbdataproxy.exe'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('dbproxy-interop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$db = Join-Path $tmp 'interop.duckdb'
& $Python (Join-Path $PSScriptRoot 'seed.py') $db

$hash = (& $exe hash-password interop-pw).Trim()
$env:DBPROXY_Proxy__DataDirectory = $tmp
$env:DBPROXY_Proxy__Listen__Port = "$Port"
$env:DBPROXY_Proxy__Users__0__Name = 'py'
$env:DBPROXY_Proxy__Users__0__PasswordHash = $hash
$env:DBPROXY_Proxy__Users__0__DataSources__0 = 'duck'
$env:DBPROXY_Proxy__DataSources__duck__Provider = 'duckdb'
$env:DBPROXY_Proxy__DataSources__duck__ConnectionString = "Data Source=$db"
$env:DBPROXY_Proxy__DataSources__duck__AllowWrites = 'true'
$proxy = Start-Process $exe -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput (Join-Path $tmp 'out.log') -RedirectStandardError (Join-Path $tmp 'err.log')
try {
  Start-Sleep 5
  & $Python (Join-Path $PSScriptRoot 'interop.py') $Port py interop-pw
  $code = $LASTEXITCODE
} finally {
  Stop-Process -Id $proxy.Id -Force
}
exit $code
