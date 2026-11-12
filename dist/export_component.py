#!/usr/bin/env python3
"""The export component (plan 32 DBR16): the pinned Chromium, the bundled fonts and the print page, as one folder installed beside a host that exports PDFs
(the Gateway, which hosts the `views` module and the subscription task). Standard library only.

  export_component.py stage  --rid linux-x64 --out <dir> [--print-dir <dir>] [--root <repo>] [--zip <file>]
  export_component.py verify <dir> --rid linux-x64

`stage` copies `chromium.pin`, `.chromium/<rid>/`, `fonts.pin`, `.fonts/` (each family with its OFL.txt) and the print page (`print.js`, `print.css`) from a repository
whose fetch scripts have run, writes `export-component.json` (what was staged and the hashes of the print files), verifies the result and optionally zips it.
`verify` checks a folder against the pins and the manifest and refuses a changed file, naming it: the browser executable and every font file and licence against the
SHA-256 in the pin, the print files against the manifest. The Chromium archive's own hash is checked by scripts/fetch-chromium when it is downloaded; here the executable is.
The folder is not the program folder: an upgrade replaces the program files and leaves it, and the host is pointed at it (`views.export.pin`, `.page_assets`,
`.fonts.directory`, `.fonts.pin`; docs/views.md, "The export component").
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
import zipfile

MANIFEST = "export-component.json"
PRINT_FILES = ("print.js", "print.css")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_pin(path):
    """KEY=value lines (comments and blanks skipped); a repeated key (FILE=...) keeps every value."""
    values = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            values.setdefault(key, []).append(value)
    return values


def one(pin, key, path):
    if key not in pin:
        raise ValueError(f"{path} has no {key}")
    return pin[key][0]


def verify(folder, rid):
    """The list of problems with an installed component, empty when it is whole."""
    problems = []
    key = rid.replace("-", "_")
    chromium_pin = os.path.join(folder, "chromium.pin")
    fonts_pin = os.path.join(folder, "fonts.pin")
    for name in ("chromium.pin", "fonts.pin", MANIFEST):
        if not os.path.isfile(os.path.join(folder, name)):
            problems.append(f"{name} is missing from {folder}")
    if problems:
        return problems
    try:
        pin = read_pin(chromium_pin)
        binary = os.path.join(folder, ".chromium", rid, one(pin, f"BIN_{key}", chromium_pin))
        want = one(pin, f"BIN_SHA256_{key}", chromium_pin)
    except (ValueError, OSError) as ex:
        return [f"chromium.pin cannot be used for {rid}: {ex}"]
    if not os.path.isfile(binary):
        problems.append(f"the browser is missing: {binary} (scripts/fetch-chromium.sh {rid})")
    elif sha256(binary) != want:
        problems.append(f"the browser {binary} does not match chromium.pin (its SHA-256 differs)")
    try:
        fonts = read_pin(fonts_pin)
    except OSError as ex:
        return problems + [f"fonts.pin cannot be read: {ex}"]
    for family in one(fonts, "FAMILIES", fonts_pin).split():
        fkey = family.replace("-", "_")
        folder_of = os.path.join(folder, ".fonts", family)
        licence = os.path.join(folder_of, "OFL.txt")
        if not os.path.isfile(licence):
            problems.append(f"the licence of {family} is missing: {licence} (the OFL requires it beside the files)")
        elif sha256(licence) != one(fonts, f"LICENSE_SHA256_{fkey}", fonts_pin):
            problems.append(f"the licence {licence} does not match fonts.pin")
        for entry in fonts.get("FILE", []):
            fam, path, digest = entry.split("|")
            if fam != family:
                continue
            target = os.path.join(folder_of, os.path.basename(path))
            if not os.path.isfile(target):
                problems.append(f"the font {target} is missing")
            elif sha256(target) != digest:
                problems.append(f"the font {target} does not match fonts.pin (its SHA-256 differs)")
    try:
        with open(os.path.join(folder, MANIFEST), encoding="utf-8") as f:
            manifest = json.load(f)
    except (OSError, ValueError) as ex:
        return problems + [f"{MANIFEST} cannot be read: {ex}"]
    for name in PRINT_FILES:
        target = os.path.join(folder, "print", name)
        if not os.path.isfile(target):
            problems.append(f"the print page's {name} is missing: {target} (npm run build:print -w @dbdatakit/visuals)")
        elif sha256(target) != manifest.get("print", {}).get(name):
            problems.append(f"the print file {target} does not match {MANIFEST} (changed after staging)")
    if manifest.get("chromium") != one(pin, "VERSION", chromium_pin):
        problems.append(f"{MANIFEST} names Chromium {manifest.get('chromium')} and chromium.pin names {one(pin, 'VERSION', chromium_pin)}")
    return problems


def stage(root, rid, out, print_dir, zip_path=None):
    if os.path.exists(out) and os.listdir(out):
        raise SystemExit(f"{out} is not empty")
    os.makedirs(out, exist_ok=True)
    for name in ("chromium.pin", "fonts.pin"):
        shutil.copy2(os.path.join(root, name), os.path.join(out, name))
    for source, target in ((os.path.join(root, ".chromium", rid), os.path.join(out, ".chromium", rid)), (os.path.join(root, ".fonts"), os.path.join(out, ".fonts"))):
        if not os.path.isdir(source):
            raise SystemExit(f"{source} does not exist: run scripts/fetch-chromium.sh {rid} and scripts/fetch-fonts.sh first")
        shutil.copytree(source, target, symlinks=True)
    os.makedirs(os.path.join(out, "print"), exist_ok=True)
    for name in PRINT_FILES:
        source = os.path.join(print_dir, name)
        if not os.path.isfile(source):
            raise SystemExit(f"{source} does not exist: run npm run build:print -w @dbdatakit/visuals")
        shutil.copy2(source, os.path.join(out, "print", name))
    pin = read_pin(os.path.join(out, "chromium.pin"))
    fonts = read_pin(os.path.join(out, "fonts.pin"))
    manifest = {
        "component": "export",
        "rid": rid,
        "chromium": one(pin, "VERSION", "chromium.pin"),
        "fonts": one(fonts, "VERSION", "fonts.pin"),
        "print": {name: sha256(os.path.join(out, "print", name)) for name in PRINT_FILES},
    }
    with open(os.path.join(out, MANIFEST), "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    problems = verify(out, rid)
    if problems:
        raise SystemExit("the staged component is not whole:\n  " + "\n  ".join(problems))
    if zip_path:
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for base, dirs, files in os.walk(out):
                dirs.sort()
                for name in sorted(files):
                    full = os.path.join(base, name)
                    info = zipfile.ZipInfo.from_file(full, os.path.relpath(full, out))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    with open(full, "rb") as f:
                        z.writestr(info, f.read())
    return manifest


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("stage")
    s.add_argument("--rid", required=True)
    s.add_argument("--out", required=True)
    s.add_argument("--root", default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    s.add_argument("--print-dir")
    s.add_argument("--zip")
    v = sub.add_parser("verify")
    v.add_argument("folder")
    v.add_argument("--rid", required=True)
    args = parser.parse_args(argv)
    if args.command == "stage":
        manifest = stage(args.root, args.rid, args.out, args.print_dir or os.path.join(args.root, "Shared", "web", "visuals", "dist", "print"), args.zip)
        print(f"staged the export component in {args.out}: Chromium {manifest['chromium']}, fonts {manifest['fonts']}")
        return 0
    problems = verify(args.folder, args.rid)
    for line in problems:
        print(line)
    if not problems:
        print(f"{args.folder} is a whole export component")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
