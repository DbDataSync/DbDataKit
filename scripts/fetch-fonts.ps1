<#
.SYNOPSIS
Downloads the bundled font set named in fonts.pin (repository root) into .fonts\<family>\.

.DESCRIPTION
The same as scripts/fetch-fonts.sh: each archive, every font file taken from it and the licence text are checked against the SHA-256 recorded in the pin;
the licence is installed beside the files as OFL.txt. Works in Windows PowerShell 5.1 and PowerShell 7; hashing and the tar reading use .NET.

.PARAMETER Family
Families named by FAMILIES in the pin, or all. Default: all.

Environment (for tests and offline mirrors): FONTS_PIN, FONTS_RELEASE_BASE (an http(s) address; fetch-fonts.sh also takes file://), FONTS_DEST.
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Family)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$pinFile = if ($env:FONTS_PIN) { $env:FONTS_PIN } else { Join-Path $root 'fonts.pin' }
$destRoot = if ($env:FONTS_DEST) { $env:FONTS_DEST } else { Join-Path $root '.fonts' }

$pin = @{}
$files = @{}   # family -> list of @(path, sha256)
foreach ($line in Get-Content -LiteralPath $pinFile) {
    if ($line -match '^FILE=([^|]+)\|([^|]+)\|([0-9a-f]+)\s*$') {
        if (-not $files.ContainsKey($Matches[1])) { $files[$Matches[1]] = @() }
        $files[$Matches[1]] += , @($Matches[2], $Matches[3])
    }
    elseif ($line -match '^([A-Za-z0-9_]+)=(.*)$') { if (-not $pin.ContainsKey($Matches[1])) { $pin[$Matches[1]] = $Matches[2].Trim() } }
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

# Extracts every regular file of a .tar.gz under $target (a small ustar reader, so a Windows without tar.exe still works).
function Expand-TarGz([string]$archive, [string]$target) {
    $file = [System.IO.File]::OpenRead($archive)
    $gzip = New-Object System.IO.Compression.GZipStream($file, [System.IO.Compression.CompressionMode]::Decompress)
    try {
        $header = New-Object byte[] 512
        $buffer = New-Object byte[] 81920
        while ($true) {
            $read = 0
            while ($read -lt 512) {
                $n = $gzip.Read($header, $read, 512 - $read)
                if ($n -le 0) { break }
                $read += $n
            }
            if ($read -lt 512 -or $header[0] -eq 0) { return }
            $name = [System.Text.Encoding]::ASCII.GetString($header, 0, 100).TrimEnd([char]0)
            $prefix = [System.Text.Encoding]::ASCII.GetString($header, 345, 155).TrimEnd([char]0)
            if ($prefix) { $name = "$prefix/$name" }
            $size = [Convert]::ToInt64([System.Text.Encoding]::ASCII.GetString($header, 124, 12).Trim([char]0, ' '), 8)
            $type = [char]$header[156]
            $out = $null
            if (($type -eq '0' -or $type -eq [char]0) -and $name -notmatch '(^|/)\.\.(/|$)') {
                $path = Join-Path $target ($name -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
                $out = [System.IO.File]::Create($path)
            }
            try {
                $remaining = $size
                while ($remaining -gt 0) {
                    $n = $gzip.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($n -le 0) { throw "$archive ends inside $name" }
                    if ($out) { $out.Write($buffer, 0, $n) }
                    $remaining -= $n
                }
            }
            finally { if ($out) { $out.Dispose() } }
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
$base = if ($env:FONTS_RELEASE_BASE) { $env:FONTS_RELEASE_BASE } else { Get-Pin 'BASE' }
$allFamilies = (Get-Pin 'FAMILIES') -split '\s+' | Where-Object { $_ }
if (-not $Family -or $Family.Count -eq 0 -or $Family[0] -eq 'all') { $Family = $allFamilies }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('fetch-fonts-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    foreach ($name in $Family) {
        if ($allFamilies -notcontains $name) { throw "unknown family $name (known: $($allFamilies -join ' '))" }
        $key = $name -replace '-', '_'
        $archive = Get-Pin "ARCHIVE_$key"; $archiveSha = Get-Pin "ARCHIVE_SHA256_$key"
        $licensePath = Get-Pin "LICENSE_FILE_$key"; $licenseSha = Get-Pin "LICENSE_SHA256_$key"
        $dest = Join-Path $destRoot $name
        $stampText = "$version $archiveSha"
        $stamp = Join-Path $dest '.fonts-version'
        $licenseFile = Join-Path $dest 'OFL.txt'
        $current = (Test-Path $stamp) -and ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq $stampText) -and (Test-Path $licenseFile) -and ((Get-Sha256 $licenseFile) -eq $licenseSha)
        if ($current) {
            foreach ($f in $files[$name]) {
                $installed = Join-Path $dest ([System.IO.Path]::GetFileName($f[0]))
                if (-not (Test-Path $installed) -or ((Get-Sha256 $installed) -ne $f[1])) { $current = $false; break }
            }
        }
        if ($current) { Write-Host ".fonts/$name is current (fonts $version)"; continue }
        $archivePath = Join-Path $temp "$name.tgz"
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$archive" -OutFile $archivePath
        if ((Get-Sha256 $archivePath) -ne $archiveSha) { throw "checksum mismatch for $archive (expected $archiveSha)" }
        $extracted = Join-Path $temp $name
        Expand-TarGz $archivePath $extracted
        Remove-Item -LiteralPath $archivePath -Force
        $licenseSource = Join-Path $extracted ($licensePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $licenseSource) -or ((Get-Sha256 $licenseSource) -ne $licenseSha)) { throw "checksum mismatch for $licensePath inside $archive (expected $licenseSha)" }
        $staged = Join-Path $temp "$name.out"
        New-Item -ItemType Directory -Path $staged | Out-Null
        $count = 0
        foreach ($f in $files[$name]) {
            $source = Join-Path $extracted ($f[0] -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path $source) -or ((Get-Sha256 $source) -ne $f[1])) { throw "checksum mismatch for $($f[0]) inside $archive (expected $($f[1]))" }
            Copy-Item -LiteralPath $source -Destination (Join-Path $staged ([System.IO.Path]::GetFileName($f[0])))
            $count++
        }
        if ($count -eq 0) { throw "$pinFile lists no files for $name" }
        Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $staged 'OFL.txt')
        [System.IO.File]::WriteAllText((Join-Path $staged '.fonts-version'), $stampText)
        if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
        New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
        Move-Item -LiteralPath $staged -Destination $dest
        Write-Host "fetched .fonts/$name ($count files, fonts $version)"
    }
}
finally { Remove-Item -Recurse -Force -LiteralPath $temp -ErrorAction SilentlyContinue }
