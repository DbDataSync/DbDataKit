<#
.SYNOPSIS
  Installs the dbdatakit-full Scoop manifest on a real Windows machine from the dry run's packages and checks what each step leaves (plan 33 SC14): a per-user install and an
  install over a service of another folder are refused, a global install registers, starts and pairs every member (and answers each member's health check; each service has the exact name, account, display
  name and description of the suite's table, read back with `sc qc` and `sc qdescription`), a second run changes
  nothing, an update runs the services from the new files and still paired, and an uninstall removes the services and the pairings but not the data.
  Run by the job `suite-install-windows` of .github/workflows/suite-release.yml (a dry run), as an elevated user. Written without a Windows machine: its first run is its proof.
  -Packages is the folder the dry run's artifacts were downloaded to (release-<product>/ folders, each with the zip and SHA256SUMS).
  -WorkstationOwner gives the install a Windows client's owner: an elevated administrator's new objects are owned by that user, not by the Administrators
  group as on Windows Server (the security option "System objects: Default owner for objects created by members of the Administrators group", the value
  nodefaultadminowner=1 under HKLM\SYSTEM\CurrentControlSet\Control\Lsa). Without it the hosted runner keeps the Server default.
#>
param(
  [Parameter(Mandatory)][string]$Packages,
  [Parameter(Mandatory)][string]$Version,
  [string]$Work = (Join-Path $env:RUNNER_TEMP 'suite-scoop'),
  [int]$Port = 8099,
  [switch]$WorkstationOwner
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$suite = Get-Content (Join-Path $repoRoot 'dist\suite.json') -Raw | ConvertFrom-Json
$members = @($suite.members)
$data = Join-Path $env:ProgramData 'DbDataKit'
$env:DBDATAKIT_DATA = $data       # what the machine variable gives a new terminal: the commands run here read the folder the services use
$scoopApp = Join-Path $env:ProgramData 'scoop\apps\dbdatakit-full'
$step = 0
$log = Join-Path $Work 'logs'
New-Item -ItemType Directory -Force $Work, $log | Out-Null

function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw "FAILED: $message" } else { Write-Host "ok: $message" } }
function Heading([string]$text) { Write-Host "=== $text" }
function Exe([string]$product) { Join-Path $scoopApp ("current\{0}\{1}.exe" -f ($members | Where-Object { $_.product -eq $product }).folder, $product) }

# A native command with its output captured to a file and shown; returns the exit code. Never throws for a non-zero exit.
function Native([string]$name, [scriptblock]$command) {
  $script:step++
  $file = Join-Path $log ("{0:00}-{1}.txt" -f $script:step, $name)
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $global:LASTEXITCODE = 0
  try {
    # Scoop is PowerShell and runs a manifest's hook in this process: a hook that throws (the refusals of dbdatakit-full do) ends the command by an exception, not an exit code. It is the command's
    # answer here, written to the file and counted as a failure of the command, so the assertions after it can read what it said.
    try { & $command *>&1 | Tee-Object -FilePath $file | Out-Host; $code = $LASTEXITCODE }
    catch { $said = ($_ | Out-String); Add-Content -Path $file -Value $said; Write-Host $said; $code = 1 }
  } finally { $ErrorActionPreference = $old }
  if ($null -eq $code) { $code = 0 }
  # a command that prints nothing leaves Tee-Object no file to write
  if (-not (Test-Path -LiteralPath $file)) { New-Item -ItemType File -Path $file | Out-Null }
  return [pscustomobject]@{ Code = $code; File = $file; Text = (Get-Content $file -Raw) }
}
function ServiceExists([string]$name) {
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
  & sc.exe query $name 2>&1 | Out-Null
  return ($LASTEXITCODE -ne 1060)
}
# The suite's table of services (SuiteServices.cs, read by dist/suite_services.py): the Windows name, account and description each service is registered with
# (ForEach-Object unrolls the array, which Windows PowerShell's ConvertFrom-Json writes as one object)
$table = @((& python -I (Join-Path $repoRoot 'dist\suite_services.py') | Out-String) | ConvertFrom-Json | ForEach-Object { $_ })
if ($LASTEXITCODE -ne 0 -or $table.Count -eq 0) { throw "dist/suite_services.py could not read the suite's table of services (exit $LASTEXITCODE)" }
function Services {
  foreach ($m in $members) {
    $meta = Get-Content (Join-Path $repoRoot $m.meta) -Raw | ConvertFrom-Json
    $entry = $table | Where-Object { $_.program -eq $m.product }
    if ($meta.service -and $entry) { [pscustomobject]@{ Product = $m.product; Name = $entry.windowsName; Account = $entry.account; DisplayName = $entry.displayName; Description = $entry.description; DataFolder = $entry.dataFolder } }
  }
}
# Each service as the service manager has it: the exact name, the display name and account (`sc qc`) and the description (`sc qdescription`), all as the table says
function AssertServicesAreNamedAsTheTableSays([string]$why) {
  foreach ($s in Services) {
    $registered = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
    Assert ($null -ne $registered -and $registered.Name -ceq $s.Name) "$why : the service is registered as exactly $($s.Name) (found '$($registered.Name)')"
    $qc = Native "sc-qc-$($s.Product)" { & sc.exe qc $s.Name }
    Assert ($qc.Code -eq 0) "$why : sc qc $($s.Name) succeeds"
    $account = [regex]::Match($qc.Text, '(?m)^\s*SERVICE_START_NAME\s*:\s*(.+?)\s*$').Groups[1].Value
    Assert ($account -eq $s.Account) "$why : $($s.Name) runs as $($s.Account) (sc qc says '$account')"
    $display = [regex]::Match($qc.Text, '(?m)^\s*DISPLAY_NAME\s*:\s*(.+?)\s*$').Groups[1].Value
    Assert ($display -ceq $s.DisplayName) "$why : $($s.Name) is listed as $($s.DisplayName) (sc qc says '$display')"
    $qd = Native "sc-qdescription-$($s.Product)" { & sc.exe qdescription $s.Name 8192 }
    Assert ($qd.Code -eq 0) "$why : sc qdescription $($s.Name) succeeds"
    $description = [regex]::Match($qd.Text, '(?m)^\s*DESCRIPTION\s*:\s*(.+?)\s*$').Groups[1].Value
    Assert ($description -ceq $s.Description) "$why : $($s.Name) has the description '$($s.Description)' (sc qdescription says '$description')"
  }
}
function Diagnose {
  Write-Host '--- services'
  foreach ($s in Services) { & sc.exe qc $s.Name 2>&1 | Out-Host; & sc.exe query $s.Name 2>&1 | Out-Host }
  Write-Host "--- DBDATAKIT_DATA: machine '$([Environment]::GetEnvironmentVariable('DBDATAKIT_DATA', 'Machine'))', this process '$env:DBDATAKIT_DATA'"
  Write-Host '--- recent System and Application events (the members'' own logs, .NET Runtime crashes, the Service Control Manager)'
  foreach ($l in 'System', 'Application') {
    Get-WinEvent -FilterHashtable @{ LogName = $l; StartTime = (Get-Date).AddMinutes(-90) } -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -like '*DbData*' -or $_.ProviderName -like '*.NET*' -or $_.ProviderName -eq 'Application Error' -or $_.ProviderName -eq 'Service Control Manager' } | Select-Object -First 150 |
      ForEach-Object { Write-Host ("{0} {1} {2}: {3}" -f $_.TimeCreated.ToString('HH:mm:ss'), $l, $_.ProviderName, ($_.Message -replace '\s+', ' ')) }
  }
  Write-Host '--- the access lists of the data folders and the key rings'
  foreach ($f in @(Get-ChildItem -LiteralPath $data -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'bin' })) {
    & icacls $f.FullName 2>&1 | Out-Host
    foreach ($k in @(Get-ChildItem -LiteralPath (Join-Path $f.FullName 'secrets\keys') -ErrorAction SilentlyContinue)) { & icacls $k.FullName 2>&1 | Out-Host }
    Get-ChildItem -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("   {0} {1} {2}" -f $_.LastWriteTime.ToString('HH:mm:ss'), $_.Length, $_.Name) }
  }
}
function AssertEveryMemberRunsPairedAndHealthy([string]$why) {
  foreach ($s in Services) {
    $svc = Get-Service -Name $s.Name -ErrorAction SilentlyContinue
    Assert ($null -ne $svc -and $svc.Status -eq 'Running') "$why : the service $($s.Name) is running"
    $binary = ([string](Get-CimInstance Win32_Service -Filter "Name='$($s.Name)'").PathName).Trim('"')
    Assert ($binary.StartsWith((Join-Path $env:ProgramData 'DbDataKit\bin') + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $binary.StartsWith($scoopApp, [StringComparison]::OrdinalIgnoreCase)) "$why : the service $($s.Name) runs from the copy under %ProgramData%\DbDataKit\bin, not from Scoop's folder"
    $account = (Get-CimInstance Win32_Service -Filter "Name='$($s.Name)'").StartName
    Write-Host "   $($s.Name) runs as $account"
    $health = Native "health-$($s.Product)" { & (Exe $s.Product) health check }
    Assert ($health.Code -eq 0) "$why : $($s.Product) answers its health check"
  }
  $listed = Native 'instance-list' { & (Exe 'dbdatakit') instance list }
  foreach ($s in Services) {
    if ($s.Product -eq 'dbdatakit') { continue }
    Assert ($listed.Text -match ("(?m)^{0}\s.*\s\sready(\s|$)" -f [regex]::Escape($s.Product))) "$why : the console lists $($s.Product) as ready"
  }
}

# The commands an administrator runs on the machine while the services are up (the install runs some of them, a person the rest): each answers, none ends in an unhandled exception.
# An elevated command line opens the files the services made, the key rings first, and must accept the services' accounts on them.
function AssertAdminCommandsWork([string]$why) {
  $commands = @(
    @{ Product = 'dbdatakit'; Arguments = @('health', 'check') },
    @{ Product = 'dbdatakit'; Arguments = @('instance', 'list', '--offline') },
    @{ Product = 'dbdatagateway'; Arguments = @('health', 'check') },
    @{ Product = 'dbdatagateway'; Arguments = @('server', 'pair', '--max-role', 'viewer,operator,admin') },
    @{ Product = 'dbdatagateway'; Arguments = @('server', 'clients') },
    @{ Product = 'dbdatagateway'; Arguments = @('secret', 'list') },
    @{ Product = 'dbdatarunner'; Arguments = @('server', 'clients') },
    @{ Product = 'dbdataworkspace'; Arguments = @('server', 'clients') },
    @{ Product = 'dbdataworkspace'; Arguments = @('secret', 'list') }
  )
  foreach ($c in $commands) {
    $exe = Exe $c.Product
    $arguments = $c.Arguments
    $said = "$($c.Product) $($arguments -join ' ')"
    # a pairing code is not written to the log
    $r = Native ("admin-{0}-{1}" -f $c.Product, ($arguments[0..1] -join '-')) { & $exe @arguments 2>&1 | ForEach-Object { ([string]$_) -replace 'Pairing code: \S+', 'Pairing code: ***' } }
    Assert ($r.Text -notmatch 'Unhandled exception|KeyPermissionException|SQLite Error') "$why : '$said' run by the administrator ends without a crash"
    Assert ($r.Code -eq 0) "$why : '$said' run by the administrator succeeds"
  }
}

# A service key only SYSTEM may read, as security software keeps its own on a workstation (the administrator may still delete it). The elevated command line reads the
# registrations of the services to know whose accounts may be on the key files; one key it cannot read must not hide the others.
$protectedKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\zzDbDataKitE2eProtected'
function AddUnreadableServiceKey {
  New-Item -Path $protectedKey -Force | Out-Null
  New-ItemProperty -Path $protectedKey -Name ImagePath -Value 'C:\Windows\System32\cmd.exe' -PropertyType ExpandString -Force | Out-Null
  $acl = New-Object System.Security.AccessControl.RegistrySecurity
  $acl.SetAccessRuleProtection($true, $false)
  $acl.AddAccessRule((New-Object System.Security.AccessControl.RegistryAccessRule((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')), 'FullControl', 'Allow')))
  $acl.AddAccessRule((New-Object System.Security.AccessControl.RegistryAccessRule((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')), 'Delete, ChangePermissions', 'Allow')))
  Set-Acl -Path $protectedKey -AclObject $acl
  $readable = $true
  try { Get-ItemProperty $protectedKey -ErrorAction Stop | Out-Null } catch { $readable = $false }
  Assert (-not $readable) 'a service key the administrator cannot read is in place'
}
function RemoveUnreadableServiceKey { if (Test-Path $protectedKey) { Remove-Item -Path $protectedKey -Force -ErrorAction SilentlyContinue } }

# The owner Windows gives an elevated administrator's new objects. Windows Server gives them the Administrators group; a Windows client gives them the user who
# made them, and a service that then opens a folder the install made sees another account's folder. Windows reads nodefaultadminowner when an account logs
# on, and this process logged on before the value is set, so the same default owner is also put on this process's token: every process it starts (Scoop,
# the install hooks, `service install`) inherits it, as on a workstation. The services themselves run as their own accounts and are not affected.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class DefaultOwner {
  [StructLayout(LayoutKind.Sequential)] struct TOKEN_OWNER { public IntPtr Owner; }
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref TOKEN_OWNER info, int length);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  const uint TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_QUERY = 0x0008;
  const int TokenOwner = 4;
  // Makes the current user the default owner of the objects this process and its children create.
  public static void SetToCurrentUser() {
    SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
    byte[] sid = new byte[user.BinaryLength];
    user.GetBinaryForm(sid, 0);
    IntPtr token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_DEFAULT | TOKEN_QUERY, out token)) throw new System.ComponentModel.Win32Exception();
    GCHandle pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
    try {
      TOKEN_OWNER owner = new TOKEN_OWNER { Owner = pinned.AddrOfPinnedObject() };
      if (!SetTokenInformation(token, TokenOwner, ref owner, Marshal.SizeOf(typeof(TOKEN_OWNER)))) throw new System.ComponentModel.Win32Exception();
    } finally { pinned.Free(); CloseHandle(token); }
  }
}
'@
$lsa = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
function NewObjectOwner {
  $probe = Join-Path $Work ("owner-probe-{0}" -f [guid]::NewGuid().ToString('n'))
  New-Item -ItemType Directory -Path $probe | Out-Null
  $owner = (Get-Acl -LiteralPath $probe).Owner
  Remove-Item -LiteralPath $probe -Force
  return $owner
}
# Every folder and file under the data folder with its owner, so a run shows who made what (the install's folders against the services' files)
function ReportOwners([string]$why) {
  Write-Host "--- owners under $data ($why)"
  foreach ($i in @(Get-ChildItem -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notlike (Join-Path $data 'bin') + '*' })) {
    $owner = try { (Get-Acl -LiteralPath $i.FullName -ErrorAction Stop).Owner } catch { '(cannot read)' }
    Write-Host ("   {0}  {1}" -f $owner, $i.FullName.Substring($data.Length + 1))
  }
}
Heading 'The owner of an elevated administrator''s new objects'
if ($WorkstationOwner) {
  New-ItemProperty -Path $lsa -Name nodefaultadminowner -Value 1 -PropertyType DWord -Force | Out-Null
  [DefaultOwner]::SetToCurrentUser()
  $owner = NewObjectOwner
  Assert ($owner -eq $me) "as on a Windows client, a folder this administrator makes is owned by $me (found $owner)"
  # git settings of the machine that allow a repository despite its owner (safe.directory, which libgit2 reads too) are removed, as on a Windows client that has
  # none: the hosted runner has safe.directory=* in C:\Program Files\Git\etc\gitconfig, which would hide a configuration repository the service does not own
  $allowed = (& git config --show-origin --get-all safe.directory 2>&1 | Out-String).Trim()
  Write-Host "   safe.directory before: $allowed"
  foreach ($level in '--system', '--global') { & git config $level --unset-all safe.directory 2>&1 | Out-Null }
  $programDataGit = Join-Path $env:ProgramData 'Git\config'
  if (Test-Path $programDataGit) { & git config --file $programDataGit --unset-all safe.directory 2>&1 | Out-Null }
  $left = (& git config --show-origin --get-all safe.directory 2>&1 | Out-String).Trim()
  Assert ($left -eq '') "no git settings of the machine allow a repository despite its owner (before: $allowed; now: $left)"
} else {
  $owner = NewObjectOwner
  Write-Host "   as on Windows Server, a folder this administrator makes is owned by $owner"
}

# --- Scoop, and the packages served on this machine as the manifest's urls
Heading 'Scoop'
if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {
  Invoke-Expression "& {$(Invoke-RestMethod https://get.scoop.sh)} -RunAsAdmin"
  $env:Path = "$env:USERPROFILE\scoop\shims;" + $env:Path
}
Assert ($null -ne (Get-Command scoop -ErrorAction SilentlyContinue)) 'scoop is installed'

Heading 'Serve every member''s zip and write the manifest'
$serve = Join-Path $Work 'serve'
$sums = @()
foreach ($m in $members) {
  $from = Join-Path $Packages "release-$($m.product)"
  $zip = Join-Path $from "$($m.product)-v$Version-win-x64.zip"
  Assert (Test-Path $zip) "$($m.product) has its win-x64 zip in the dry run"
  $to = Join-Path $serve "$($m.product)\v$Version"
  New-Item -ItemType Directory -Force $to | Out-Null
  Copy-Item $zip $to
  $sums += (Join-Path $from 'SHA256SUMS')
}
# (PowerShell names are not case-sensitive: a parameter called $version would hide the script's $Version, so this one is $forVersion)
function Bind([string]$forVersion, [string]$out) {
  # a second version: the same files under the second version's names, so the manifest of the update differs only by its version and its urls
  if ($forVersion -ne $Version) {
    foreach ($m in $members) {
      $to = Join-Path $serve "$($m.product)\v$forVersion"
      New-Item -ItemType Directory -Force $to | Out-Null
      Copy-Item (Join-Path $serve "$($m.product)\v$Version\$($m.product)-v$Version-win-x64.zip") (Join-Path $to "$($m.product)-v$forVersion-win-x64.zip")
      $hash = (Get-FileHash (Join-Path $to "$($m.product)-v$forVersion-win-x64.zip") -Algorithm SHA256).Hash.ToLower()
      Add-Content -Path (Join-Path $Work 'SHA256SUMS.second') -Value "$hash  $($m.product)-v$forVersion-win-x64.zip"
    }
    $sumFiles = @((Join-Path $Work 'SHA256SUMS.second'))
  } else { $sumFiles = $sums }
  & python (Join-Path $repoRoot 'dist\dist.py') bind --suite (Join-Path $repoRoot 'dist\suite.json') --root $repoRoot --version $forVersion --sums @sumFiles --base-url "http://127.0.0.1:$Port" --out $out
  if ($LASTEXITCODE -ne 0) { throw "dist.py bind --suite exited $LASTEXITCODE" }
}
$manifestDir = Join-Path $Work 'manifest'
Bind $Version $manifestDir
$manifest = Join-Path $manifestDir 'dbdatakit-full.json'
$server = Start-Process python -ArgumentList '-m', 'http.server', $Port, '--bind', '127.0.0.1', '--directory', $serve -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

try {
  Heading 'A per-user install is refused, and leaves no service'
  $r = Native 'user-install' { scoop install $manifest }
  Assert ($r.Text -match 'scoop install -g') 'the refusal says to use scoop install -g'
  foreach ($s in Services) { Assert (-not (ServiceExists $s.Name)) "$($s.Name) is not registered after the per-user attempt" }
  Native 'user-uninstall' { scoop uninstall dbdatakit-full } | Out-Null

  Heading 'An install over a service of another folder is refused before anything changes'
  $other = Join-Path $Work 'other-route'
  New-Item -ItemType Directory -Force $other | Out-Null
  Expand-Archive (Join-Path $serve "dbdatarunner\v$Version\dbdatarunner-v$Version-win-x64.zip") $other -Force
  $otherExe = Join-Path $other 'dbdatarunner.exe'
  $r = Native 'other-route-register' { & $otherExe service install }
  Assert ($r.Code -eq 0) 'a runner is registered from another folder (as a product''s own install would)'
  $r = Native 'other-route-install' { scoop install -g $manifest }
  Assert ($r.Text -match 'registered from') 'the install names the service registered from another folder'
  Assert (-not (ServiceExists 'DbDataKit')) 'nothing else was registered by the refused install'
  Native 'other-route-uninstall' { scoop uninstall -g dbdatakit-full } | Out-Null
  Native 'other-route-unregister' { & $otherExe service uninstall } | Out-Null
  Assert (-not (ServiceExists 'DbDataRunner')) 'the other route''s service is removed again'

  Heading 'Each service creates and owns its data: the install makes only its top folder'
  # each member registered from its own zip, as the MSI and install.ps1 -InstallService do (`service install`), and started once, before any administrator's
  # command writes in its folder (docs/install.md, "A service owns its data")
  $own = Join-Path $Work 'own'
  Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
  foreach ($s in Services) {
    $to = Join-Path $own $s.Product
    New-Item -ItemType Directory -Force $to | Out-Null
    Expand-Archive (Join-Path $serve "$($s.Product)\v$Version\$($s.Product)-v$Version-win-x64.zip") $to -Force
    $r = Native "own-register-$($s.Product)" { & (Join-Path $to "$($s.Product).exe") service install }
    Assert ($r.Code -eq 0) "$($s.Product) service install succeeds"
  }
  $tops = @(Get-ChildItem -LiteralPath $data -Force)
  Assert ($tops.Count -eq @(Services).Count) "after the install the data root holds one top folder per service and nothing else (found: $(($tops | ForEach-Object Name) -join ', '))"
  foreach ($top in $tops) {
    Assert ($top.PSIsContainer -and @(Get-ChildItem -LiteralPath $top.FullName -Force).Count -eq 0) "after the install $($top.Name) is an empty top folder"
    $owner = (Get-Acl -LiteralPath $top.FullName).Owner
    Assert ($owner -eq 'BUILTIN\Administrators') "the install gives $($top.Name) the Administrators group as owner (found $owner)"
  }
  Assert (-not (Test-Path (Join-Path $data 'logs'))) 'the install makes no logs folder'
  foreach ($s in Services) {
    Start-Service -Name $s.Name
    (Get-Service -Name $s.Name).WaitForStatus('Running', '00:01:00')
    $healthy = $false
    for ($i = 0; $i -lt 60 -and -not $healthy; $i++) { if ((Native "own-health-$($s.Product)" { & (Join-Path (Join-Path $own $s.Product) "$($s.Product).exe") health check }).Code -eq 0) { $healthy = $true } else { Start-Sleep -Seconds 2 } }
    Assert $healthy "$($s.Product) started on the folder the install made and answers its health check"
  }
  $logs = Join-Path $data 'logs'
  $logsAcl = Get-Acl -LiteralPath $logs
  Assert ($logsAcl.AreAccessRulesProtected -and @($logsAcl.Access | Where-Object { $_.IdentityReference -eq 'CREATOR OWNER' }).Count -gt 0) 'the first service to start made logs with its access list (inheritance cut, CREATOR OWNER)'
  ReportOwners 'after each service''s first start'
  foreach ($s in Services) {
    $folder = Join-Path $data $s.DataFolder
    $made = @(Get-ChildItem -LiteralPath $folder -Recurse -Force) + @(Get-ChildItem -LiteralPath (Join-Path $logs $s.Product) -Recurse -Force -ErrorAction SilentlyContinue) + @(Get-Item -LiteralPath (Join-Path $logs $s.Product) -Force -ErrorAction SilentlyContinue)
    Assert ($made.Count -gt 0) "$($s.Product) made its data in $folder"
    $others = @($made | Where-Object { (Get-Acl -LiteralPath $_.FullName).Owner -ne $s.Account } | ForEach-Object { "$($_.FullName) ($((Get-Acl -LiteralPath $_.FullName).Owner))" })
    Assert ($others.Count -eq 0) "every file $($s.Product) made is owned by $($s.Account) ($($made.Count) entries); not: $($others -join ', ')"
  }
  foreach ($s in @(Services)[-1..-4]) {
    Native "own-unregister-$($s.Product)" { & (Join-Path (Join-Path $own $s.Product) "$($s.Product).exe") service uninstall } | Out-Null
    Assert (-not (ServiceExists $s.Name)) "$($s.Name) is removed again"
  }
  Remove-Item -LiteralPath $data -Recurse -Force

  Heading 'The global install registers, starts and pairs every member'
  $install = Native 'install' { scoop install -g $manifest }
  Assert ($install.Code -eq 0) 'scoop install -g succeeded'
  ReportOwners 'after install'
  # the Gateway made its configuration repository itself at its first start and never writes git settings of its own (docs/install.md, "The Gateway's configuration repository")
  Assert (-not (Test-Path (Join-Path $data 'gateway\git\.gitconfig'))) 'the Gateway wrote no git settings file'
  $configOwner = (Get-Acl -LiteralPath (Join-Path $data 'gateway\config')).Owner
  Assert ($configOwner -eq 'NT SERVICE\DbDataGateway') "the Gateway's configuration repository is owned by NT SERVICE\DbDataGateway (found $configOwner)"
  AssertEveryMemberRunsPairedAndHealthy 'after install'
  AssertServicesAreNamedAsTheTableSays 'after install'
  AssertAdminCommandsWork 'after install'
  Assert ($install.Text -notmatch 'Pairing code:') 'no pairing code is in the install output'
  Assert (Test-Path (Join-Path $data 'server\FIRST-RUN.txt')) 'the console wrote its first-run invitation'
  $before = (Native 'instance-ids' { & (Exe 'dbdatakit') instance list --offline }).Text

  Heading 'Running the install script again changes nothing: nothing is registered or paired twice'
  # Scoop does not run post_install twice for one version, so the script is run the way Scoop runs it (its variables $dir, $global and $cmd set), as a repeat after a failure would
  $text = Get-Content $manifest -Raw | ConvertFrom-Json
  $repeat = Native 'repeat-post-install' {
    $dir = Join-Path $scoopApp $Version; $global = $true; $cmd = 'install'
    Invoke-Expression ($text.post_install -join "`n")
  }
  Assert ($repeat.Text -match 'is registered already') 'the repeat found the services registered'
  Assert ($repeat.Text -match 'is listed by the console already') 'the repeat found the members paired'
  $after = (Native 'instance-ids-again' { & (Exe 'dbdatakit') instance list --offline }).Text
  Assert ($before -eq $after) 'the console lists the same servers, with the same ids'
  AssertEveryMemberRunsPairedAndHealthy 'after the repeat'

  Heading 'An update runs the services from the new files, still paired'
  $second = "$Version.1"
  Bind $second $manifestDir
  $update = Native 'update' { scoop update dbdatakit-full -g }
  Assert ($update.Code -eq 0) 'scoop update -g succeeded'
  Assert (Test-Path (Join-Path $scoopApp $second)) "the new version folder $second exists"
  Assert (Test-Path (Join-Path $env:ProgramData "DbDataKit\bin\$second")) "the services' files of version $second are copied under %ProgramData%"
  Assert (-not (Test-Path (Join-Path $env:ProgramData "DbDataKit\bin\$Version"))) "the copy of the old version $Version is removed"
  $current = (Get-Item (Join-Path $scoopApp 'current')).Target
  Write-Host "   current -> $current"
  AssertEveryMemberRunsPairedAndHealthy 'after the update'
  AssertServicesAreNamedAsTheTableSays 'after the update'
  AssertAdminCommandsWork 'after the update'
  $afterUpdate = (Native 'instance-ids-update' { & (Exe 'dbdatakit') instance list --offline }).Text
  Assert ($before -eq $afterUpdate) 'the update did not pair anything again'

  Heading 'An uninstall removes the services and the pairings, and keeps the data'
  $copy = Join-Path $Work 'console-copy'
  Copy-Item (Join-Path $scoopApp 'current\console') $copy -Recurse     # the console's files go with the uninstall; its list is read from a copy
  $remove = Native 'uninstall' { scoop uninstall -g dbdatakit-full }
  Assert ($remove.Code -eq 0) 'scoop uninstall -g succeeded'
  foreach ($s in Services) { Assert (-not (ServiceExists $s.Name)) "$($s.Name) no longer exists" }
  $listed = Native 'instance-list-after' { & (Join-Path $copy 'dbdatakit.exe') instance list --offline }
  # the data stays, and so do the key files the service made: the administrator still opens them once the service is gone
  Assert ($listed.Code -eq 0 -and $listed.Text -notmatch 'Unhandled exception|KeyPermissionException') 'the administrator reads the console''s list after the uninstall'
  foreach ($s in Services) { if ($s.Product -ne 'dbdatakit') { Assert ($listed.Text -notmatch [regex]::Escape($s.Product)) "the console no longer lists $($s.Product)" } }
  Assert (-not (Test-Path (Join-Path $env:ProgramData 'DbDataKit\bin'))) 'the copy of the program files is removed'
  Assert (Test-Path $data) 'the settings and data under %ProgramData% remain'

  Heading 'A clean install on a machine that had the suite: DBDATAKIT_DATA is a machine variable before it starts, and one service key cannot be read'
  # as on a workstation where the suite was removed by hand (services, data and package) and installed again: the machine variable stays from the earlier install, so the
  # services see it from their first start; security software keeps a service key the administrator cannot read
  Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
  Assert (-not (Test-Path $data)) 'the data folder is removed'
  [Environment]::SetEnvironmentVariable('DBDATAKIT_DATA', $data, 'Machine')
  AddUnreadableServiceKey
  $again = Native 'install-again' { scoop install -g $manifest }
  Assert ($again.Code -eq 0) 'scoop install -g succeeded on the machine that had the suite'
  AssertEveryMemberRunsPairedAndHealthy 'after the second install'
  AssertAdminCommandsWork 'after the second install'
  $removeAgain = Native 'uninstall-again' { scoop uninstall -g dbdatakit-full }
  Assert ($removeAgain.Code -eq 0) 'scoop uninstall -g succeeded again'
  foreach ($s in Services) { Assert (-not (ServiceExists $s.Name)) "$($s.Name) no longer exists after the second uninstall" }
  RemoveUnreadableServiceKey
  Write-Host 'Suite install end-to-end: OK'
}
catch {
  Diagnose
  ReportOwners 'at the failure'
  Write-Host '--- startup-failure files'
  foreach ($f in @(Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'startup-failure-*.txt' -ErrorAction SilentlyContinue)) { Write-Host "## $($f.Name)"; Get-Content -LiteralPath $f.FullName | Out-Host }
  throw
}
finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
  # leave nothing behind on a failure
  RemoveUnreadableServiceKey
  if ($WorkstationOwner) { Remove-ItemProperty -Path $lsa -Name nodefaultadminowner -ErrorAction SilentlyContinue }
  & scoop uninstall -g dbdatakit-full 2>&1 | Out-Null
  foreach ($s in Services) { if (ServiceExists $s.Name) { & sc.exe stop $s.Name | Out-Null; Start-Sleep -Seconds 2; & sc.exe delete $s.Name | Out-Null } }
}
$global:LASTEXITCODE = 0
exit 0
