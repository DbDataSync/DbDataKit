#!/usr/bin/env python3
"""Binds the standard distribution files to one product and one release.

Every product is distributed the same way (dist/README.md): per-platform zip files and a SHA256SUMS on its GitHub release, plus three entry
points that all work from the same facts: a Scoop manifest, a PowerShell installer and a bash installer. The facts that differ between
products live in <Product>/dist.json; this script turns the generic templates in this directory into that product's files.

    dist.py bind --meta DbDataBuild/dist.json --version 2026.10.19.1200 --sums SHA256SUMS --out out/

writes out/<product>.json (Scoop), out/install.ps1 and out/install.sh (each only if the product has a platform it applies to). With no
--version/--sums (the installers only), the Scoop manifest is skipped: a manifest pins one release.

    dist.py bind --suite dist/suite.json --version 2026.10.19.1200 --sums out/*/SHA256SUMS --out out/

writes out/dbdatakit-full.json, one Scoop manifest for every member product of a suite release (plan 33 section 6a): the members' zips as
`url`/`hash` lists, one `extract_to` folder and one `bin` alias each. Its post_install registers and pairs the services, its pre_uninstall removes them (dist/suite-scripts.ps1).
`--sums` takes one SHA256SUMS per member, or one file that covers all of them.
"""
import argparse
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
DEFAULT_REPO = "DbDataSync/DbDataKit"
BEGIN = ">>> bound by dist/dist.py"
END = "<<< end bound"


def zip_name(product, version, rid):
    return f"{product}-v{version}-{rid}.zip"


def load_meta(path):
    meta = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    for key in ("product", "displayName", "description", "homepage", "license", "platforms"):
        if not meta.get(key):
            raise SystemExit(f"{path}: '{key}' is required")
    if not re.fullmatch(r"[a-z][a-z0-9]*", meta["product"]):
        raise SystemExit(f"{path}: 'product' must be a lowercase name (it is the tag prefix)")
    for rid, p in meta["platforms"].items():
        if not p.get("exe"):
            raise SystemExit(f"{path}: platform {rid} needs an 'exe'")
    return meta


def parse_sums(text):
    sums = {}
    for line in text.splitlines():
        parts = line.split()
        if len(parts) >= 2:
            sums[parts[-1].lstrip("*")] = parts[0].lower()
    return sums


def _check_global_env(entries):
    for e in entries:
        name, folder = e.get("name"), e.get("programData")
        if not (isinstance(name, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", name)):
            raise SystemExit(f"globalEnv: '{name}' is not an environment variable name")
        if not (isinstance(folder, str) and folder and not folder.startswith(("\\", "/")) and ":" not in folder):
            raise SystemExit(f"globalEnv: '{folder}' must be a folder under %ProgramData%, written without a drive or a leading slash")


def _program_data_text(entry):
    return "%ProgramData%\\" + entry["programData"]


def _program_data_ps(entry):
    return f"(Join-Path $env:ProgramData {_ps(entry['programData'])})"


# Machine variables every product's manifest sets to the same value and no uninstall removes: another product still installed reads it (plan 33 SC31).
SHARED_GLOBAL_ENV = ("DBDATAKIT_DATA",)


def _distinct_env(entries):
    """One entry per variable name, in first-seen order; the same name with two different folders is an error naming it (the manifests of the members must agree)."""
    seen = {}
    for e in entries:
        if e["name"] in seen and seen[e["name"]]["programData"] != e["programData"]:
            raise SystemExit(f"globalEnv: '{e['name']}' is set to both '{seen[e['name']]['programData']}' and '{e['programData']}'")
        seen.setdefault(e["name"], e)
    return list(seen.values())


def _global_env_set(entries):
    """Scoop runs these lines after a global install only: `$global` is Scoop's own flag for `scoop install -g`, which is also elevated, so
    the machine-level variable can be written. A per-user install leaves the program's own defaults alone."""
    _check_global_env(entries)
    lines = ["if ($global) {"]
    for e in _distinct_env(entries):
        lines.append(f"  [Environment]::SetEnvironmentVariable({_ps(e['name'])}, {_program_data_ps(e)}, 'Machine')")
    lines.append("}")
    return lines


def _global_env_unset(entries):
    """Removes only a value this manifest set, so one an administrator pointed elsewhere survives an uninstall or an update. The shared variables
    (`SHARED_GLOBAL_ENV`) are never removed: every product's manifest sets the same one, and uninstalling one product must not take it from another.
    Returns no lines when nothing is left to remove (the manifest then has no `pre_uninstall`)."""
    lines = ["if ($global) {"]
    for e in _distinct_env(entries):
        if e["name"] in SHARED_GLOBAL_ENV:
            continue
        name = _ps(e["name"])
        lines.append(
            f"  if ([Environment]::GetEnvironmentVariable({name}, 'Machine') -eq {_program_data_ps(e)}) "
            f"{{ [Environment]::SetEnvironmentVariable({name}, $null, 'Machine') }}"
        )
    lines.append("}")
    return lines if len(lines) > 2 else []


def _global_env_notes(entries):
    listed = ", ".join(f"{e['name']}={_program_data_text(e)}" for e in _distinct_env(entries))
    kept = [e["name"] for e in _distinct_env(entries) if e["name"] in SHARED_GLOBAL_ENV]
    text = f"A global install (scoop install -g) also sets, for the machine: {listed}."
    if kept:
        text += f" {', '.join(kept)} is left in place when a product is uninstalled, because the other DbDataKit products read it too; remove it by hand when the last one is gone."
    return text


def scoop_manifest(meta, version, repo, sums):
    if "win-x64" not in meta["platforms"]:
        return None
    name = zip_name(meta["product"], version, "win-x64")
    if name not in sums:
        raise SystemExit(f"SHA256SUMS does not list {name}")
    manifest = {
        "version": version,
        "description": meta["description"],
        "homepage": meta["homepage"],
        "license": meta["license"],
        "architecture": {
            "64bit": {
                "url": f"https://github.com/{repo}/releases/download/{meta['product']}/v{version}/{name}",
                "hash": sums[name],
            }
        },
        "bin": meta["platforms"]["win-x64"]["exe"],
    }
    if meta.get("preserve"):
        manifest["persist"] = meta["preserve"]
    notes = meta.get("notes", "")
    env = meta.get("globalEnv") or []
    if env:
        manifest["post_install"] = _global_env_set(env)
        if unset := _global_env_unset(env):
            manifest["pre_uninstall"] = unset
        notes = (notes + "\n" if notes else "") + _global_env_notes(env) + " Open a new terminal to see it."
    if notes:
        manifest["notes"] = notes
    if meta.get("requiresVcRuntime"):
        manifest["suggest"] = {"Visual C++ Redistributable": ["extras/vcredist2022"]}
    return manifest


SUITE_PRODUCT = "dbdatakit-full"


def load_suite(path, root):
    """The members in start order: [(meta, folder)]. A member's dist.json that is missing, or has no win-x64 platform, is an error naming it."""
    suite = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    members = []
    seen = set()
    for m in suite.get("members") or []:
        product, rel, folder = m.get("product"), m.get("meta"), m.get("folder")
        if not (product and rel and folder):
            raise SystemExit(f"{path}: every member needs 'product', 'meta' and 'folder' ({m})")
        if folder in seen or not re.fullmatch(r"[a-z][a-z0-9]*", folder):
            raise SystemExit(f"{path}: folder '{folder}' of {product} is repeated or not a lowercase name")
        seen.add(folder)
        meta_path = pathlib.Path(root) / rel
        if not meta_path.is_file():
            raise SystemExit(f"suite member {product}: {rel} does not exist")
        meta = load_meta(meta_path)
        if meta["product"] != product:
            raise SystemExit(f"suite member {product}: {rel} describes '{meta['product']}'")
        if "win-x64" not in meta["platforms"]:
            raise SystemExit(f"suite member {product} has no win-x64 platform in {rel}")
        members.append((meta, folder))
    if not members:
        raise SystemExit(f"{path}: no members")
    return members


def _alias(meta):
    return meta["platforms"]["win-x64"]["exe"].removesuffix(".exe")


# The ceiling a console administrator may use on each server (plans 13 W12, 28a R19): what the pairing allows, never what anyone holds.
_PAIR_ROLES = {"dbdatastore": "viewer,operator,author,approver,admin", "dbdataworkspace": "viewer,operator,author,admin"}
_PAIR_ROLES_DEFAULT = "viewer,operator,admin"
# The address a console pairs with, as each product's README gives its default listen address (the Gateway's is its management API, on by default and on this machine only, plan 33 D15).
# A product not listed has no default this file can name, so the notes stay generic for it and the install script leaves it unpaired.
_MANAGEMENT_ADDRESS = {
    "dbdatakit": "https://localhost:8480",
    "dbdatasync": "https://localhost:5080",
    "dbdatagateway": "https://localhost:8816",
    "dbdatarunner": "https://127.0.0.1:8495",
    "dbdatabuild": "https://127.0.0.1:8470",
    "dbdatastore": "https://127.0.0.1:8490",
    "dbdataworkspace": "https://127.0.0.1:8471",
}


def suite_notes(members):
    """What the install does, and the commands by hand that finish or repeat each step (the scripts name the failing member and stop, so an administrator can run the rest)."""
    lines = [
        "A global install (scoop install -g, from an elevated prompt) registers each member's service, starts the console, pairs every other member with it (a single-use code, made and used by the install and never stored) and starts them. A per-user install is refused.",
        "Updating (scoop update) stops the services, replaces the files and starts them again; nothing is registered or paired twice. Uninstalling (scoop uninstall -g) removes the pairings and the services; settings and data stay under %ProgramData%\\DbDataKit.",
        "Use one install route per machine: do not mix it with a product's own MSI, install.ps1 or Scoop manifest (they share the program folders and the service names); the install is refused when a member's service comes from another folder.",
        "If a step failed, the commands are, in an elevated prompt and in this order:",
    ]
    n = 0
    for meta, _ in members:
        service = meta.get("service")
        if service:
            n += 1
            register = service["register"].replace("{name}", service["name"])
            lines.append(f"{n}. {_alias(meta)} {register}")
    console = _alias(members[0][0])
    n += 1
    lines.append(f"{n}. Start the console: {console} server run (or start its service).")
    for meta, _ in members[1:]:
        n += 1
        address = _MANAGEMENT_ADDRESS.get(meta["product"])
        target = address or "<https address of that server>"
        note = " (its default management address; change it with the server's own setting and give that address instead)" if address else ""
        lines.append(
            f"{n}. Pair {_alias(meta)}: run '{_alias(meta)} server pair --max-role {_PAIR_ROLES.get(meta['product'], _PAIR_ROLES_DEFAULT)}' (it prints a single-use code), then "
            f"'{console} instance add {target} --code <code> --name {meta['product']}'{note}."
        )
    lines.append("The first administrator opens the setup link the console wrote to %ProgramData%\\DbDataKit\\server\\FIRST-RUN.txt; 'dbdatakit setup link' on the machine prints a new one.")
    return "\n".join(lines)


def _suite_regions():
    """suite-scripts.ps1 cut into its regions: {name: [lines]}; the lines outside every region are the 'bound' block, which this file writes."""
    regions, current = {}, None
    for line in (HERE / "suite-scripts.ps1").read_text(encoding="utf-8").splitlines():
        if line.startswith("#region "):
            current = line[len("#region "):].strip()
            regions[current] = []
        elif line.startswith("#endregion"):
            current = None
        elif current:
            regions[current].append(line)
    return regions


def _code_lines(lines):
    return [line for line in lines if line.strip() and not line.lstrip().startswith("#")]


def _suite_bound(members, env):
    lines = ["$Members = @("]
    for meta, folder in members:
        service = meta.get("service") or {}
        roles = _PAIR_ROLES.get(meta["product"], _PAIR_ROLES_DEFAULT)
        lines.append("  @{ " + "; ".join([
            f"Product = {_ps(meta['product'])}",
            f"Folder = {_ps(folder)}",
            f"Exe = {_ps(meta['platforms']['win-x64']['exe'])}",
            f"Service = {_ps(service.get('name', ''))}",
            f"Register = {_ps(service.get('register', '').replace('{name}', service.get('name', '')))}",
            f"Unregister = {_ps(service.get('unregister', '').replace('{name}', service.get('name', '')))}",
            f"Roles = {_ps(roles)}",
            f"Address = {_ps(_MANAGEMENT_ADDRESS.get(meta['product'], ''))}",
        ]) + " }")
    lines.append(")")
    lines.append("$MachineVars = @(" + ", ".join(f"@{{ Name = {_ps(e['name'])}; Folder = {_ps(e['programData'])} }}" for e in env) + ")")
    return lines


def suite_scripts(members, env):
    """(post_install, pre_uninstall) as the lines of the manifest's two scripts (plan 33 SC12, SC13)."""
    regions = _suite_regions()
    bound = _suite_bound(members, env)
    common = bound + _code_lines(regions["common"])
    post = common + _code_lines(regions["post_install_check"]) + (_global_env_set(env) if env else []) + _code_lines(regions["post_install"])
    undo = common + _code_lines(regions["pre_uninstall"]) + (_global_env_unset(env) if env else [])
    return post, undo


def suite_manifest(members, version, repo, sums, base_url=None):
    """One manifest for all members: url, hash, extract_to and bin are lists in member order, and two scripts (suite-scripts.ps1): `post_install` registers each member's
    service, starts the console, pairs and starts the others (a global install only, refused when a member's service comes from another folder), and `pre_uninstall`
    unpairs and unregisters on a real uninstall and only stops the services on an update. The machine variables of every member's `globalEnv` are set between the checks and
    the install, and removed (all but the shared data root) on uninstall."""
    urls, hashes, folders, bins = [], [], [], []
    missing = []
    for meta, folder in members:
        name = zip_name(meta["product"], version, "win-x64")
        if name not in sums:
            missing.append(f"{meta['product']} ({name})")
            continue
        urls.append(f"{base_url.rstrip('/')}/{meta['product']}/v{version}/{name}" if base_url else f"https://github.com/{repo}/releases/download/{meta['product']}/v{version}/{name}")
        hashes.append(sums[name])
        folders.append(folder)
        bins.append([f"{folder}\\{meta['platforms']['win-x64']['exe']}", _alias(meta)])
    if missing:
        raise SystemExit("SHA256SUMS does not list the win-x64 zip of suite member " + ", ".join(missing))
    if not members[0][0].get("service"):
        raise SystemExit(f"suite member {members[0][0]['product']} is the console (the first member) and must register a service")
    env = _distinct_env([e for m, _ in members for e in (m.get("globalEnv") or [])])
    if env:
        _check_global_env(env)
    post, undo = suite_scripts(members, env)
    manifest = {
        "version": version,
        "description": "DbDataKit, every member product on one server: " + ", ".join(m["product"] for m, _ in members) + ". Registers each service and pairs it with the console.",
        "homepage": members[0][0]["homepage"],
        "license": members[0][0]["license"],
        "architecture": {"64bit": {"url": urls, "hash": hashes}},
        "extract_to": folders,
        "bin": bins,
        "notes": suite_notes(members),
        "post_install": post,
        "pre_uninstall": undo,
    }
    if env:
        manifest["notes"] += "\n" + _global_env_notes(env) + " Open a new terminal to see it, so the commands above read the folders the services use."
    if any(m.get("requiresVcRuntime") for m, _ in members):
        manifest["suggest"] = {"Visual C++ Redistributable": ["extras/vcredist2022"]}
    return manifest


def bind_suite(args):
    if not (args.version and args.sums):
        raise SystemExit("--suite needs --version and --sums")
    members = load_suite(args.suite, args.root)
    sums = {}
    for path in args.sums:
        sums.update(parse_sums(pathlib.Path(path).read_text(encoding="utf-8")))
    manifest = suite_manifest(members, args.version, args.repo, sums, args.base_url)
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    target = out / f"{SUITE_PRODUCT}.json"
    target.write_text(json.dumps(manifest, indent=4) + "\n", encoding="utf-8", newline="\n")
    print(f"bound {SUITE_PRODUCT}: {target.name} ({len(members)} members)")


def _replace_block(template, begin_comment, body):
    """Swaps everything between the BEGIN and END marker lines (the markers stay)."""
    lines = template.splitlines()
    start = next(i for i, line in enumerate(lines) if BEGIN in line)
    end = next(i for i, line in enumerate(lines) if END in line and i > start)
    return "\n".join(lines[: start + 1] + body.splitlines() + lines[end:]) + "\n"


def _ps(value):
    return "'" + str(value).replace("'", "''") + "'"


def bind_ps1(template, meta, repo):
    service = meta.get("service") or {}
    body = "\n".join([
        "$Bound = @{",
        f"  Product = {_ps(meta['product'])}",
        f"  DisplayName = {_ps(meta['displayName'])}",
        f"  Repo = {_ps(repo)}",
        f"  Exe = {_ps(meta['platforms']['win-x64']['exe'])}",
        f"  WindowsScope = {_ps(meta.get('windowsScope', 'user'))}",
        f"  Preserve = @({', '.join(_ps(p) for p in meta.get('preserve', []))})",
        f"  ServiceName = {_ps(service.get('name', ''))}",
        f"  ServiceRegister = {_ps(service.get('register', ''))}",
        f"  RequiresDotnet = {_ps(meta.get('requiresDotnet', ''))}",
        f"  RequiresVcRuntime = ${'true' if meta.get('requiresVcRuntime') else 'false'}",
        f"  Announces = ${'true' if meta.get('announces') else 'false'}",
        f"  ExportComponent = ${'true' if meta.get('exportComponent') else 'false'}",
        f"  Notes = {_ps(meta.get('notes', ''))}",
        "}",
    ])
    return _replace_block(template, BEGIN, body)


def _sh(value):
    return "'" + str(value).replace("'", "'\\''") + "'"


def bind_sh(template, meta, repo):
    unix = {rid: p for rid, p in meta["platforms"].items() if not rid.startswith("win-")}
    body = "\n".join([
        f"PRODUCT={_sh(meta['product'])}",
        f"DISPLAY_NAME={_sh(meta['displayName'])}",
        f"DEFAULT_REPO={_sh(repo)}",
        f"PLATFORMS={_sh(' '.join(f'{rid}={p['exe']}' for rid, p in unix.items()))}",
        f"PRESERVE={_sh(' '.join(meta.get('preserve', [])))}",
        f"REQUIRES_DOTNET={_sh(meta.get('requiresDotnet') or next((p['requiresDotnet'] for p in unix.values() if p.get('requiresDotnet')), ''))}",
        f"NOTES={_sh(meta.get('notes', ''))}",
        f"ANNOUNCES={'1' if meta.get('announces') else '0'}",
        f"EXPORT_COMPONENT={'1' if meta.get('exportComponent') else '0'}",
    ])
    return _replace_block(template, BEGIN, body)


def bind(args):
    if args.suite:
        return bind_suite(args)
    if not args.meta:
        raise SystemExit("--meta (or --suite) is required")
    meta = load_meta(args.meta)
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    written = []

    if args.version:
        if not args.sums:
            raise SystemExit("--sums is required with --version")
        sums = parse_sums(pathlib.Path(args.sums[0]).read_text(encoding="utf-8"))
        manifest = scoop_manifest(meta, args.version, args.repo, sums)
        if manifest:
            target = out / f"{meta['product']}.json"
            target.write_text(json.dumps(manifest, indent=4) + "\n", encoding="utf-8", newline="\n")
            written.append(target.name)

    if "win-x64" in meta["platforms"]:
        text = bind_ps1((HERE / "install.ps1").read_text(encoding="utf-8"), meta, args.repo)
        (out / "install.ps1").write_text(text, encoding="utf-8", newline="\n")
        written.append("install.ps1")
    if any(not rid.startswith("win-") for rid in meta["platforms"]):
        text = bind_sh((HERE / "install.sh").read_text(encoding="utf-8"), meta, args.repo)
        target = out / "install.sh"
        target.write_text(text, encoding="utf-8", newline="\n")
        target.chmod(0o755)
        written.append("install.sh")

    print("bound " + meta["product"] + ": " + ", ".join(written))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("bind", help="write a product's Scoop manifest and installers")
    b.add_argument("--meta", help="path to <Product>/dist.json")
    b.add_argument("--suite", help="path to dist/suite.json: bind the suite manifest dbdatakit-full.json instead of one product")
    b.add_argument("--root", default=str(HERE.parent), help="with --suite: the folder the members' dist.json paths are relative to (the repository root)")
    b.add_argument("--version", help="the release version (no leading v); needed for the Scoop manifest")
    b.add_argument("--sums", nargs="+", help="the release's SHA256SUMS; needed with --version (with --suite, one per member or one for all)")
    b.add_argument("--repo", default=DEFAULT_REPO)
    b.add_argument("--base-url", help="with --suite: serve the zips from <base-url>/<product>/v<version>/<zip> instead of the GitHub releases (the Windows install test serves them from this machine)")
    b.add_argument("--out", required=True)
    b.set_defaults(func=bind)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
