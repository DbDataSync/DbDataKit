# The scripts of the dbdatakit-full Scoop manifest (plan 33 section 6a, SC12 and SC13). dist/dist.py bind --suite binds the member list below, cuts the file
# into the manifest's `post_install` and `pre_uninstall` (comment and blank lines are dropped), and puts the machine variables of `globalEnv` between
# the checks and the install. Scoop runs them in Windows PowerShell 5.1 or PowerShell 7 with its own variables in scope: $dir (this version's folder), $global
# (scoop install -g) and $cmd (install, update or uninstall). The code stays plain on purpose (no ?? or ternary, native commands run through Run).
# No pairing code is ever written to a file, the log or the notes: it is read from the program's output into a variable, used once and scrubbed from any message.

# >>> bound by dist/dist.py
$Members = @()
$MachineVars = @()
# <<< end bound

#region common
function Fail([string]$message) { throw "dbdatakit-full: $message" }
function Say([string]$message) { Write-Host "dbdatakit-full: $message" }
function Run([string]$exe, [string[]]$arguments) {
  $old = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { $text = (& $exe @arguments 2>&1 | Out-String); $exitCode = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
  return [pscustomobject]@{ Code = $exitCode; Output = $text }
}
function Scrub([string]$text, [string]$secret) { if ($secret) { return $text.Replace($secret, '***') } else { return $text } }
function ServicePath([string]$name) {
  $service = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
  if (-not $service) { return $null }
  $path = ([string]$service.PathName).Trim()
  if ($path.StartsWith('"')) { return $path.Substring(1, $path.IndexOf('"', 1) - 1) }
  return ($path -split ' ')[0]
}
function Stopped([string]$name) {
  $service = Get-Service -Name $name -ErrorAction SilentlyContinue
  if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction Stop; $service.WaitForStatus('Stopped', '00:01:00') }
}
$appDir = Split-Path $dir -Parent
$current = Join-Path $appDir 'current'
if (Test-Path $current) { $base = $current } else { $base = $dir }
function ExeOf($member, [string]$root) { return (Join-Path (Join-Path $root $member.Folder) $member.Exe) }
$consoleMember = $Members[0]
#endregion

#region post_install_check
if (-not $global) { Fail "this package registers services, which belong to the machine: install it with 'scoop install -g dbdatakit-full' (remove this per-user install with 'scoop uninstall dbdatakit-full')." }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail 'run the install from an elevated prompt: it registers services.' }
foreach ($m in $Members) {
  if (-not $m.Service) { continue }
  $registered = ServicePath $m.Service
  if ($registered -and -not $registered.StartsWith($appDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
    Fail "the service $($m.Service) is registered from '$registered', not from this package. Use one install route per machine: remove that one first ($($m.Product) service uninstall, then uninstall the product)."
  }
}
#endregion

#region post_install
foreach ($v in $MachineVars) { Set-Item -Path "Env:$($v.Name)" -Value (Join-Path $env:ProgramData $v.Folder) }
$failed = New-Object System.Collections.ArrayList
$consoleExe = ExeOf $consoleMember $base
function WaitHealthy($member) {
  $exe = ExeOf $member $base
  for ($i = 0; $i -lt 60; $i++) {
    if ((Run $exe @('health', 'check')).Code -eq 0) { return }
    Start-Sleep -Seconds 2
  }
  Fail "$($member.Product) did not answer its health check in two minutes (the service is $((Get-Service -Name $member.Service).Status)); look at the Application event log and run: $($member.Product) service doctor"
}
function Registered($member) {
  $exe = ExeOf $member $base
  $path = ServicePath $member.Service
  if ($path -and $path.Equals($exe, [StringComparison]::OrdinalIgnoreCase)) { Say "$($member.Service) is registered already"; return }
  $result = Run $exe (@($member.Register -split ' ') | Where-Object { $_ })
  if ($result.Code -ne 0) { Fail "$($member.Product) $($member.Register) exited $($result.Code): $($result.Output.Trim())" }
  Say "registered $($member.Service)"
}
function Started($member) {
  $service = Get-Service -Name $member.Service
  if ($service.Status -ne 'Running') { Start-Service -Name $member.Service; $service.WaitForStatus('Running', '00:01:00') }
  WaitHealthy $member
}
function Listed($member) {
  $r = Run $consoleExe @('instance', 'list', '--offline')
  return ($r.Code -eq 0 -and $r.Output -match ('(^|\s)' + [regex]::Escape($member.Address) + '/?(\s|$)'))
}
function Paired($member) {
  if (Listed $member) { Say "$($member.Product) is listed by the console already"; return }
  $exe = ExeOf $member $base
  $made = Run $exe @('server', 'pair', '--max-role', $member.Roles)
  $found = [regex]::Match($made.Output, 'Pairing code: (\S+)')
  if ($made.Code -ne 0 -or -not $found.Success) { Fail "$($member.Product) server pair failed (exit $($made.Code))." }
  $code = $found.Groups[1].Value
  Started $member
  $added = Run $consoleExe @('instance', 'add', $member.Address, '--code', $code, '--name', $member.Product)
  if ($added.Code -ne 0) { Fail "the console could not pair $($member.Product) at $($member.Address): $((Scrub $added.Output $code).Trim()). Make a new code with '$($member.Product) server pair --max-role $($member.Roles)' and run: dbdatakit instance add $($member.Address) --code <code>" }
  $code = $null
  Say "paired $($member.Product) with the console"
}
foreach ($m in $Members) { if ($m.Service) { try { Registered $m } catch { [void]$failed.Add("$($m.Product): $($_.Exception.Message)") } } }
if ($failed.Count -gt 0) { Fail ("registering the services failed, nothing was started or paired. " + ($failed -join ' | ')) }
Started $consoleMember
foreach ($m in $Members) {
  if ($m.Product -eq $consoleMember.Product -or -not $m.Service) { continue }
  if (-not $m.Address) { Say "$($m.Product) has no default address to pair: pair it by hand (see the notes)."; continue }
  try { Paired $m; Started $m } catch { [void]$failed.Add("$($m.Product): $($_.Exception.Message)") }
}
if ($failed.Count -gt 0) { Fail ("some members are not ready, the others are. Run this again after fixing: scoop update dbdatakit-full (or the commands named here). " + ($failed -join ' | ')) }
Say 'every service is registered, running and paired with the console.'
Say "The console is on $($consoleMember.Address). The first-run invitation is in $(Join-Path $env:ProgramData 'DbDataKit\server\FIRST-RUN.txt')."
Say 'Settings and data stay under %ProgramData%\DbDataKit when the package is uninstalled.'
#endregion

#region pre_uninstall
if ($global) {
  $reverse = @($Members); [array]::Reverse($reverse)
  foreach ($v in $MachineVars) { Set-Item -Path "Env:$($v.Name)" -Value (Join-Path $env:ProgramData $v.Folder) }
  $exeDir = $dir
  # Is Scoop removing the package or replacing this version? Scoop's $cmd says so ('uninstall' or 'update'), but it was never seen from a pre_uninstall script, so when it says
  # anything else (or nothing) the folder of the app decides: an update has extracted the next version beside this one before it removes this one, a real uninstall has no newer version.
  function NewerThan([string]$name, [string]$than) {
    $a = $null; $b = $null
    if ([version]::TryParse(($name -replace '-.*$', ''), [ref]$a) -and [version]::TryParse(($than -replace '-.*$', ''), [ref]$b) -and $a -ne $b) { return ($a -gt $b) }
    return ((Get-Item -LiteralPath (Join-Path $appDir $name)).CreationTimeUtc -gt (Get-Item -LiteralPath (Join-Path $appDir $than)).CreationTimeUtc)
  }
  $thisVersion = Split-Path $dir -Leaf
  $newer = @(Get-ChildItem -LiteralPath $appDir -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'current' -and $_.Name -ne $thisVersion -and (NewerThan $_.Name $thisVersion) })
  if ($cmd -eq 'uninstall') { $removing = $true } elseif ($cmd -eq 'update') { $removing = $false } else { $removing = ($newer.Count -eq 0) }
  if ($removing) {
    foreach ($m in $reverse) {
      if (-not $m.Service) { continue }
      $exe = ExeOf $m $exeDir
      try {
        if ($m.Product -ne $consoleMember.Product) {
          [void](Run (ExeOf $consoleMember $exeDir) @('instance', 'remove', $m.Product))
          $listed = Run $exe @('server', 'clients', '--format', 'json')
          if ($listed.Code -eq 0) {
            foreach ($c in @(($listed.Output | ConvertFrom-Json).data.clients | Where-Object { $_.kind -eq 'console' })) { [void](Run $exe @('server', 'unpair', $c.id)) }
          }
        }
        Stopped $m.Service
        if (ServicePath $m.Service) {
          $removed = Run $exe (@($m.Unregister -split ' ') | Where-Object { $_ })
          if ($removed.Code -ne 0) { Write-Warning "dbdatakit-full: $($m.Product) $($m.Unregister) exited $($removed.Code): $($removed.Output.Trim())" }
        }
      } catch { Write-Warning "dbdatakit-full: $($m.Product): $($_.Exception.Message)" }
    }
  } else {
    foreach ($m in $reverse) { if ($m.Service) { try { Stopped $m.Service } catch { Write-Warning "dbdatakit-full: $($m.Product): $($_.Exception.Message)" } } }
  }
}
#endregion
