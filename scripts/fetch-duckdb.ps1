<#
.SYNOPSIS
Downloads the DuckDB engine named in duckdb.pin (repository root) into .duckdb\<rid>\.

.DESCRIPTION
The same as scripts/fetch-duckdb.sh: both the archive and the library inside it are checked against the SHA-256 recorded in the pin.
Works in Windows PowerShell 5.1 and PowerShell 7. Hashing and the tar reading use .NET, so nothing beyond PowerShell is needed.

.PARAMETER Rid
linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64 or all. Default: the current platform.

Environment (for tests and offline mirrors): DUCKDB_PIN, DUCKDB_RELEASE_BASE (an http(s) address; fetch-duckdb.sh also takes file://), DUCKDB_DEST.
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Rid)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$pinFile = if ($env:DUCKDB_PIN) { $env:DUCKDB_PIN } else { Join-Path $root 'duckdb.pin' }
$destRoot = if ($env:DUCKDB_DEST) { $env:DUCKDB_DEST } else { Join-Path $root '.duckdb' }
$allRids = @('linux-x64', 'linux-arm64', 'win-x64', 'osx-arm64', 'osx-x64')

$pin = @{}
foreach ($line in Get-Content -LiteralPath $pinFile) {
    if ($line -match '^([A-Za-z0-9_]+)=(.*)$') { $pin[$Matches[1]] = $Matches[2].Trim() }
}
function Get-Pin([string]$key) {
    if (-not $pin.ContainsKey($key)) { throw "$pinFile has no $key" }
    return $pin[$key]
}

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Copies the named file out of a .tar.gz: a small ustar reader, so a Windows without tar.exe (Server 2016) still works.
function Expand-TarGzEntry([string]$archive, [string]$name, [string]$target) {
    $file = [System.IO.File]::OpenRead($archive)
    $gzip = New-Object System.IO.Compression.GZipStream($file, [System.IO.Compression.CompressionMode]::Decompress)
    try {
        $header = New-Object byte[] 512
        while ($true) {
            $read = 0
            while ($read -lt 512) {
                $n = $gzip.Read($header, $read, 512 - $read)
                if ($n -le 0) { break }
                $read += $n
            }
            if ($read -lt 512 -or $header[0] -eq 0) { throw "$name is not in $archive" }
            $entry = [System.Text.Encoding]::ASCII.GetString($header, 0, 100).TrimEnd([char]0)
            $sizeText = [System.Text.Encoding]::ASCII.GetString($header, 124, 12).Trim([char]0, ' ')
            $size = [Convert]::ToInt64($sizeText, 8)
            $entry = $entry -replace '^\./', ''
            $out = $null
            if ($entry -eq $name) { $out = [System.IO.File]::Create($target) }
            try {
                $remaining = $size
                $buffer = New-Object byte[] 81920
                while ($remaining -gt 0) {
                    $n = $gzip.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($n -le 0) { throw "$archive ends inside $entry" }
                    if ($out) { $out.Write($buffer, 0, $n) }
                    $remaining -= $n
                }
            }
            finally { if ($out) { $out.Dispose() } }
            if ($out) { return }
            $pad = (512 - ($size % 512)) % 512
            while ($pad -gt 0) {
                $n = $gzip.Read($buffer, 0, [int][Math]::Min($buffer.Length, $pad))
                if ($n -le 0) { break }
                $pad -= $n
            }
        }
    }
    finally { $gzip.Dispose(); $file.Dispose() }
}

$version = Get-Pin 'VERSION'
$base = if ($env:DUCKDB_RELEASE_BASE) { $env:DUCKDB_RELEASE_BASE } else { Get-Pin 'BASE' }

if (-not $Rid -or $Rid.Count -eq 0) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { $Rid = @('win-x64') }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) { $Rid = @(if ($arch -eq 'Arm64') { 'linux-arm64' } else { 'linux-x64' }) }
    else { $Rid = @(if ($arch -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }) }
}
elseif ($Rid[0] -eq 'all') { $Rid = $allRids }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('fetch-duckdb-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    foreach ($id in $Rid) {
        if ($allRids -notcontains $id) { throw "unknown rid $id (known: $($allRids -join ' '))" }
        $key = $id -replace '-', '_'
        $archive = Get-Pin "ARCHIVE_$key"; $archiveSha = Get-Pin "ARCHIVE_SHA256_$key"
        $lib = Get-Pin "LIB_$key"; $libSha = Get-Pin "LIB_SHA256_$key"
        $dest = Join-Path $destRoot $id
        $stamp = Join-Path $dest '.duckdb-version'
        $current = (Join-Path $dest $lib)
        if ((Test-Path $current) -and (Test-Path $stamp) -and ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq "$version $libSha") -and ((Get-Sha256 $current) -eq $libSha)) {
            Write-Host ".duckdb/$id/$lib is current (DuckDB $version)"; continue
        }
        $archivePath = Join-Path $temp $archive
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$archive" -OutFile $archivePath
        if ((Get-Sha256 $archivePath) -ne $archiveSha) { throw "checksum mismatch for $archive (expected $archiveSha)" }
        $extracted = Join-Path $temp "$id-$lib"
        Expand-TarGzEntry $archivePath $lib $extracted
        if ((Get-Sha256 $extracted) -ne $libSha) { throw "checksum mismatch for $lib inside $archive (expected $libSha)" }
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item -LiteralPath $extracted -Destination $current -Force
        [System.IO.File]::WriteAllText($stamp, "$version $libSha")
        Write-Host "fetched .duckdb/$id/$lib (DuckDB $version)"
    }
}
finally { Remove-Item -Recurse -Force -LiteralPath $temp -ErrorAction SilentlyContinue }
