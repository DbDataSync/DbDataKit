#!/usr/bin/env python3
"""Binds the standard distribution files to one product and one release.

Every product is distributed the same way (dist/README.md): per-platform zip files and a SHA256SUMS on its GitHub release, plus three entry
points that all work from the same facts: a Scoop manifest, a PowerShell installer and a bash installer. The facts that differ between
products live in <Product>/dist.json; this script turns the generic templates in this directory into that product's files.

    dist.py bind --meta DbDataBuild/dist.json --version 2026.10.19.1200 --sums SHA256SUMS --out out/

writes out/<product>.json (Scoop), out/install.ps1 and out/install.sh (each only if the product has a platform it applies to). With no
--version/--sums (the installers only), the Scoop manifest is skipped: a manifest pins one release.
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


def _global_env_set(entries):
    """Scoop runs these lines after a global install only: `$global` is Scoop's own flag for `scoop install -g`, which is also elevated, so
    the machine-level variable can be written. A per-user install leaves the program's own defaults alone."""
    _check_global_env(entries)
    lines = ["if ($global) {"]
    for e in entries:
        lines.append(f"  [Environment]::SetEnvironmentVariable({_ps(e['name'])}, {_program_data_ps(e)}, 'Machine')")
    lines.append("}")
    return lines


def _global_env_unset(entries):
    """Removes only a value this manifest set, so one an administrator pointed elsewhere survives an uninstall or an update."""
    lines = ["if ($global) {"]
    for e in entries:
        name = _ps(e["name"])
        lines.append(
            f"  if ([Environment]::GetEnvironmentVariable({name}, 'Machine') -eq {_program_data_ps(e)}) "
            f"{{ [Environment]::SetEnvironmentVariable({name}, $null, 'Machine') }}"
        )
    lines.append("}")
    return lines


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
        manifest["pre_uninstall"] = _global_env_unset(env)
        listed = ", ".join(f"{e['name']}={_program_data_text(e)}" for e in env)
        notes = (notes + "\n" if notes else "") + f"A global install (scoop install -g) also sets, for the machine: {listed}. Open a new terminal to see it."
    if notes:
        manifest["notes"] = notes
    if meta.get("requiresVcRuntime"):
        manifest["suggest"] = {"Visual C++ Redistributable": ["extras/vcredist2022"]}
    return manifest


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
        f"REQUIRES_DOTNET={_sh(meta.get('requiresDotnet', ''))}",
        f"NOTES={_sh(meta.get('notes', ''))}",
    ])
    return _replace_block(template, BEGIN, body)


def bind(args):
    meta = load_meta(args.meta)
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    written = []

    if args.version:
        if not args.sums:
            raise SystemExit("--sums is required with --version")
        sums = parse_sums(pathlib.Path(args.sums).read_text(encoding="utf-8"))
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
    b.add_argument("--meta", required=True, help="path to <Product>/dist.json")
    b.add_argument("--version", help="the release version (no leading v); needed for the Scoop manifest")
    b.add_argument("--sums", help="the release's SHA256SUMS; needed with --version")
    b.add_argument("--repo", default=DEFAULT_REPO)
    b.add_argument("--out", required=True)
    b.set_defaults(func=bind)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    sys.exit(main())
