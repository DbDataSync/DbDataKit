<#
.SYNOPSIS
  Publishes the proxy as a standalone folder: DbDataProxy.exe (64-bit, self-contained single file) plus the
  32-bit ODBC worker under worker\. No .NET runtime needs to be installed on the target machine.
.EXAMPLE
  ./build/publish.ps1                      # -> artifacts/publish/win-x64
  ./build/publish.ps1 -Runtime win-x86     # whole proxy 32-bit (then no separate worker is needed)
#>
param(
  [string]$Runtime = 'win-x64',
  [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish'),
  [string]$Configuration = 'Release',
  [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$target = Join-Path $Output $Runtime
$versionArgs = @(if ($Version) { "-p:Version=$Version" })

dotnet publish (Join-Path $root 'src\DbDataProxy.Server') -c $Configuration -r $Runtime --self-contained `
  -p:PublishSingleFile=true @versionArgs -o $target
if ($LASTEXITCODE) { throw 'Publishing the proxy failed.' }

if ($Runtime -ne 'win-x86') {
  dotnet publish (Join-Path $root 'src\DbDataProxy.Worker') -c $Configuration -r win-x86 --self-contained `
    -p:PublishSingleFile=true @versionArgs -o (Join-Path $target 'worker')
  if ($LASTEXITCODE) { throw 'Publishing the 32-bit worker failed.' }
}
Write-Host "Published to $target"
