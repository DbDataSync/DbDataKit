<#
.SYNOPSIS
Downloads the Chromium (Chrome for Testing) named in chromium.pin (repository root) into .chromium\<rid>\.

.DESCRIPTION
The same as scripts/fetch-chromium.sh: both the archive and the executable inside it are checked against the SHA-256 recorded in the pin, and the
whole archive is installed. Works in Windows PowerShell 5.1 and PowerShell 7; hashing and unzipping use .NET.

.PARAMETER Rid
linux-x64, win-x64 or all. Default: the current platform.

Environment (for tests and offline mirrors): CHROMIUM_PIN, CHROMIUM_RELEASE_BASE (an http(s) address; fetch-chromium.sh also takes file://), CHROMIUM_DEST.
#>
[CmdletBinding()]
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Rid)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest's progress bar makes a 200 MB download minutes slower in Windows PowerShell 5.1
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$pinFile = if ($env:CHROMIUM_PIN) { $env:CHROMIUM_PIN } else { Join-Path $root 'chromium.pin' }
$destRoot = if ($env:CHROMIUM_DEST) { $env:CHROMIUM_DEST } else { Join-Path $root '.chromium' }
$allRids = @('linux-x64', 'win-x64')

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

$version = Get-Pin 'VERSION'
$base = if ($env:CHROMIUM_RELEASE_BASE) { $env:CHROMIUM_RELEASE_BASE } else { Get-Pin 'BASE' }

if (-not $Rid -or $Rid.Count -eq 0) {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { $Rid = @('win-x64') }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) { $Rid = @('linux-x64') }
    else { throw "no Chromium is pinned for this platform; name a rid: $($allRids -join ' ')" }
}
elseif ($Rid[0] -eq 'all') { $Rid = $allRids }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('fetch-chromium-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    foreach ($id in $Rid) {
        if ($allRids -notcontains $id) { throw "unknown rid $id (known: $($allRids -join ' '))" }
        $key = $id -replace '-', '_'
        $archive = Get-Pin "ARCHIVE_$key"; $archiveSha = Get-Pin "ARCHIVE_SHA256_$key"
        $bin = Get-Pin "BIN_$key"; $binSha = Get-Pin "BIN_SHA256_$key"
        $dest = Join-Path $destRoot $id
        $binPath = Join-Path $dest ($bin -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        $stamp = Join-Path $dest '.chromium-version'
        if ((Test-Path $binPath) -and (Test-Path $stamp) -and ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq "$version $binSha") -and ((Get-Sha256 $binPath) -eq $binSha)) {
            Write-Host ".chromium/$id/$bin is current (Chromium $version)"; continue
        }
        $archivePath = Join-Path $temp ([System.IO.Path]::GetFileName($archive))
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$archive" -OutFile $archivePath
        if ((Get-Sha256 $archivePath) -ne $archiveSha) { throw "checksum mismatch for $archive (expected $archiveSha)" }
        $extracted = Join-Path $temp $id
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extracted)
        Remove-Item -LiteralPath $archivePath -Force
        $extractedBin = Join-Path $extracted ($bin -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $extractedBin) -or ((Get-Sha256 $extractedBin) -ne $binSha)) { throw "checksum mismatch for $bin inside $archive (expected $binSha)" }
        if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
        New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
        Move-Item -LiteralPath $extracted -Destination $dest
        [System.IO.File]::WriteAllText($stamp, "$version $binSha")
        Write-Host "fetched .chromium/$id/$bin (Chromium $version)"
    }
}
finally { Remove-Item -Recurse -Force -LiteralPath $temp -ErrorAction SilentlyContinue }
