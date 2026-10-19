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
    if meta.get("notes"):
        manifest["notes"] = meta["notes"]
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
