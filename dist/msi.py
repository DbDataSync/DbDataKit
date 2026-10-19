#!/usr/bin/env python3
"""Builds a product's Windows installer (.msi) from its release package, with wixl (msitools), not the WiX toolset.

    msi.py build --meta DbDataProxy/dist.json --version 2026.10.19.509 --payload <unzipped win-x64 package> --out dbdataproxy-v2026.10.19.509-win-x64.msi
    msi.py wxs   (same arguments, no --out: prints the WiX-schema source)

The MSI copies the package to %ProgramFiles%\\<DisplayName>. For a product with a Windows service (`service` in dist.json) it also runs the
product's own `register` command after the files are copied (INSTALLSERVICE=0 skips it) and `unregister` on removal, so the service setup has
one implementation. An upgrade (same UpgradeCode, higher version) replaces the files and keeps the registered service, restarting it if the
installer stopped it. A product with `msi.addToPath` gets its folder on the machine PATH. Settings and data live outside the package and are
never touched. Double-click installs with the standard Windows Installer prompts; `msiexec /i x.msi /qn` is silent.

wixl is the msitools project's builder (LGPL, `apt install wixl`). It reads the WiX schema but is independent of the WiX toolset.
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
from xml.sax.saxutils import quoteattr as _quoteattr

NAMESPACE = "http://schemas.microsoft.com/wix/2006/wi"
MANUFACTURER = "DbDataSync"


def quoteattr(value):
    """An XML attribute value, with `$` doubled: wixl runs its own preprocessor over attribute text and drops a lone `$`, and `$$` is its literal one."""
    return _quoteattr(str(value).replace("$", "$$"))


def msi_version(version):
    """CalVer YYYY.M.D.HHmm (optionally -beta) as an MSI ProductVersion.

    Windows Installer compares three fields, the last of them up to 65535, so the day and time of day share it:
    2026.10.19.509 -> 26.10.27669 (19 * 1440 + 5 * 60 + 9). It sorts in release order, which is all an upgrade needs.
    """
    core = version.split("-", 1)[0]
    m = re.fullmatch(r"(\d{4})\.(\d{1,2})\.(\d{1,2})\.(\d{1,4})", core)
    if not m:
        raise SystemExit(f"'{version}' is not a CalVer version (YYYY.M.D.HHmm)")
    year, month, day, hm = (int(x) for x in m.groups())
    hour, minute = divmod(hm, 100)
    if not (2000 <= year <= 2255 and 1 <= month <= 12 and 1 <= day <= 31 and hour < 24 and minute < 60):
        raise SystemExit(f"'{version}' is not a valid date and time")
    return f"{year - 2000}.{month}.{day * 1440 + hour * 60 + minute}"


def _id(prefix, text):
    return prefix + hashlib.md5(text.encode("utf-8")).hexdigest()[:20]


def _command(template, name):
    return template.replace("{name}", name)


def _ps_quote(text):
    return "'" + text.replace("'", "''") + "'"


def _msi_escape(text):
    """Square brackets start a property reference in an MSI command line; [\\[] and [\\]] are the literal ones."""
    return re.sub(r"[\[\]]", lambda m: "[\\[]" if m.group() == "[" else "[\\]]", text)


def _ps_command(script):
    # Properties ([INSTALLFOLDER]) are written as @{NAME} in the script and become MSI property references after the literal brackets are escaped.
    escaped = _msi_escape(script)
    escaped = re.sub(r"@\{([A-Z]+)\}", lambda m: "[" + m.group(1) + "]", escaped)
    return f'-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "{escaped}"'


def _path_edit(add):
    """Edits the machine PATH without expanding the entries already there (%SystemRoot% stays %SystemRoot%)."""
    return (
        "$ErrorActionPreference = 'Stop'; "
        "$k = Get-Item 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment'; "
        "$d = '@{INSTALLFOLDER}'.TrimEnd('\\'); "
        "$p = $k.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); "
        "$parts = @($p -split ';' | Where-Object { $_ -ne '' -and $_.TrimEnd('\\') -ne $d }); "
        + ("$parts += $d; " if add else "")
        + "Set-ItemProperty -Path $k.PSPath -Name Path -Value ($parts -join ';') -Type ExpandString"
    )


def powershell_sequence_remove(pair):
    action_id, condition = pair
    return f'<Custom Action="{action_id}" Sequence="3499">{condition}</Custom>'


def wxs(meta, version, payload):
    msi = meta.get("msi") or {}
    if not msi.get("upgradeCode"):
        raise SystemExit(f"{meta['product']}: dist.json needs msi.upgradeCode (a fixed GUID, never changed)")
    upgrade_code = str(uuid.UUID(msi["upgradeCode"])).upper()
    service = meta.get("service") or {}
    exe_name = meta["platforms"]["win-x64"]["exe"]
    display = meta["displayName"]
    payload = pathlib.Path(payload)
    if not (payload / exe_name).is_file():
        raise SystemExit(f"{payload} has no {exe_name}: not an unpacked win-x64 package")

    guid_namespace = uuid.UUID(upgrade_code)   # component GUIDs are stable across versions, as upgrades require
    tree = {}
    for base, _dirs, files in os.walk(payload):
        rel = pathlib.PurePath(os.path.relpath(base, payload)).as_posix()
        tree["" if rel == "." else rel] = sorted(files)
    exe_file_id = _id("f_", exe_name)

    components = []

    def directory(rel, depth):
        pad = "  " * depth
        lines = []
        for name in tree.get(rel, []):
            path = f"{rel}/{name}" if rel else name
            comp = _id("c_", path)
            components.append(comp)
            inner = ""
            if path == exe_name:
                if service:
                    # Stops the service before the old files go (upgrade or removal). A service that does not exist is left alone.
                    inner += f'\n{pad}    <ServiceControl Id="StopService" Name={quoteattr(service["name"])} Stop="both" Wait="yes"/>'
            lines.append(
                f'{pad}<Component Id="{comp}" Guid="{str(uuid.uuid5(guid_namespace, path)).upper()}" Win64="yes">'
                f'\n{pad}  <File Id="{_id("f_", path)}" Name={quoteattr(name)} Source={quoteattr(str(payload / path))} KeyPath="yes"/>'
                f'{inner}\n{pad}</Component>')
        children = sorted(d for d in tree if d and d.rpartition("/")[0] == rel)
        for child in children:
            lines.append(f'{pad}<Directory Id="{_id("d_", child)}" Name={quoteattr(child.rpartition("/")[2])}>')
            lines.append(directory(child, depth + 1))
            lines.append(f'{pad}</Directory>')
        return "\n".join(lines)

    body = directory("", 5)
    actions = [
        # PowerShell's path as a property, resolved when the deferred actions below are scheduled (wixl cannot take a Directory for an exe action).
        '<CustomAction Id="SetPowerShell" Property="POWERSHELLEXE" Value="[System64Folder]WindowsPowerShell\\v1.0\\powershell.exe"/>',
    ]
    # Explicit sequence numbers, not Before/After: wixl's relative placement misorders actions (it even moved RemoveExistingProducts after
    # InstallFiles). The standard actions sit at InstallValidate 1400, RemoveExistingProducts 1401, InstallInitialize 1500, StopServices 1900,
    # DeleteServices 2000, RemoveFiles 3500, InstallFiles 4000, StartServices 5900.
    sequence = ['<Custom Action="SetPowerShell" Sequence="1450"/>']
    after_files = []   # run in order after the files are copied, each after the previous one

    def powershell(action_id, script, condition, ignore=True):
        actions.append(
            f'<CustomAction Id="{action_id}" Property="POWERSHELLEXE" ExeCommand={quoteattr(_ps_command(script))} '
            f'Execute="deferred" Impersonate="no" Return="{"ignore" if ignore else "check"}"/>')
        return action_id, condition

    if service:
        register = _command(service["register"], service["name"])
        unregister = _command(service["unregister"], service["name"])
        actions += [
            f'<CustomAction Id="RegisterService" FileKey="{exe_file_id}" ExeCommand={quoteattr(register)} Execute="deferred" Impersonate="no" Return="check"/>',
            f'<CustomAction Id="UnregisterService" FileKey="{exe_file_id}" ExeCommand={quoteattr(unregister)} Execute="deferred" Impersonate="no" Return="ignore"/>',
        ]
        # A fresh install registers the service (stopped, so it can be configured first); an upgrade keeps the registration it has.
        after_files.append(("RegisterService", 'NOT Installed AND NOT WIX_UPGRADE_DETECTED AND INSTALLSERVICE="1"'))
        # The installer stopped the service to replace its files; an upgrade starts it again.
        after_files.append(powershell("StartService", f"Start-Service -Name {_ps_quote(service['name'])} -ErrorAction SilentlyContinue",
                                      'WIX_UPGRADE_DETECTED AND INSTALLSERVICE="1"'))
        sequence.append(f'<Custom Action="UnregisterService" Sequence="1901">REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE</Custom>')
    if msi.get("addToPath"):
        after_files.append(powershell("AddToPath", _path_edit(True), "NOT Installed", ignore=False))
        sequence.append(
            powershell_sequence_remove(powershell("RemoveFromPath", _path_edit(False), 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE')))
    for number, (action_id, condition) in enumerate(after_files, start=4001):
        sequence.append(f'<Custom Action="{action_id}" Sequence="{number}">{condition}</Custom>')
    properties = [
        f'<Property Id="ARPCOMMENTS" Value={quoteattr(f"{display} {version}")}/>',
        f'<Property Id="ARPURLINFOABOUT" Value={quoteattr(meta["homepage"])}/>',
    ]
    if service:
        properties.append('<Property Id="INSTALLSERVICE" Value="1"/>')

    indent = "    "
    parts = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<Wix xmlns="{NAMESPACE}">',
        f'  <Product Id="*" Name={quoteattr(display)} Language="1033" Version="{msi_version(version)}" Manufacturer={quoteattr(MANUFACTURER)} UpgradeCode="{{{upgrade_code}}}">',
        f'{indent}<Package InstallerVersion="500" Compressed="yes" InstallScope="perMachine" Description={quoteattr(f"{display} {version}")}/>',
        f'{indent}<MajorUpgrade DowngradeErrorMessage={quoteattr(f"A newer version of {display} is already installed.")}/>',
        f'{indent}<Media Id="1" Cabinet="payload.cab" EmbedCab="yes"/>',
        *[indent + p for p in properties],
        f'{indent}<Directory Id="TARGETDIR" Name="SourceDir">',
        f'{indent}  <Directory Id="ProgramFiles64Folder">',
        f'{indent}    <Directory Id="INSTALLFOLDER" Name={quoteattr(display)}>',
        body,
        f'{indent}    </Directory>',
        f'{indent}  </Directory>',
        f'{indent}</Directory>',
        f'{indent}<Feature Id="Main" Title={quoteattr(display)} Level="1">',
        *[f'{indent}  <ComponentRef Id="{c}"/>' for c in components],
        f'{indent}</Feature>',
        *[indent + a for a in actions],
    ]
    if sequence:
        parts += [f'{indent}<InstallExecuteSequence>', *[f'{indent}  {s}' for s in sequence], f'{indent}</InstallExecuteSequence>']
    parts += ['  </Product>', '</Wix>', '']
    return "\n".join(parts)


def build(args):
    meta = json.loads(pathlib.Path(args.meta).read_text(encoding="utf-8"))
    source = wxs(meta, args.version, args.payload)
    if args.command == "wxs":
        sys.stdout.write(source)
        return
    wixl = shutil.which(args.wixl)
    if not wixl:
        raise SystemExit(f"{args.wixl} was not found (apt install wixl)")
    with tempfile.TemporaryDirectory() as tmp:
        wxs_path = pathlib.Path(tmp) / f"{meta['product']}.wxs"
        wxs_path.write_text(source, encoding="utf-8", newline="\n")
        out = pathlib.Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([wixl, "-a", "x64", "-o", str(out), str(wxs_path)], check=True)
    print(f"built {out} ({out.stat().st_size} bytes)")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("build", "wxs"):
        p = sub.add_parser(name)
        p.add_argument("--meta", required=True)
        p.add_argument("--version", required=True)
        p.add_argument("--payload", required=True)
        p.add_argument("--wixl", default="wixl")
        if name == "build":
            p.add_argument("--out", required=True)
        p.set_defaults(func=build)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
