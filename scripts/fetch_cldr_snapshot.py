#!/usr/bin/env python3
"""Regenerate (or verify) the vendored CLDR snapshot the locale-data tests compare against.

The suite holds hand-written locale data (`Shared/web/visuals/src/views/locales.json`, the currency symbols and the compact suffixes of the
view formatter). `Shared/conformance/locale/cldr-snapshot.json` holds what the Unicode CLDR says about the same fields for the same
locales, and nothing else; the tests of `DbDataKit.Views` (.NET) and `@dbdatakit/visuals` (vitest) compare the hand-written data with it.

    python3 -I scripts/fetch_cldr_snapshot.py            # offline: the snapshot file still has the hash the pin records
    python3 -I scripts/fetch_cldr_snapshot.py --verify   # downloads the two CLDR packages, checks them against the pinned
                                                         # integrity, rebuilds the snapshot and compares it with the file
    python3 -I scripts/fetch_cldr_snapshot.py --write    # the same, and writes the snapshot and the pin's SNAPSHOT_SHA256

To move to a new CLDR release: change CLDR_VERSION and the two PACKAGE_*_INTEGRITY lines of `cldr-snapshot.pin` (the `dist.integrity` the npm
registry shows for `<package>/<version>`), run `--write`, then run the locale tests and read what they report: a difference is either an error in
`locales.json` (fix it) or a deliberate choice (a row with a reason in `Shared/conformance/locale/allowed-deviations.json`).

Source: the npm packages `cldr-numbers-full` and `cldr-dates-full` (the CLDR JSON of the Unicode Consortium), licence Unicode-3.0
(https://www.unicode.org/license.txt). Standard library only.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import sys
import tarfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIR = ROOT / "Shared" / "conformance" / "locale"
SNAPSHOT = DIR / "cldr-snapshot.json"
PIN = DIR / "cldr-snapshot.pin"
LOCALES_JSON = ROOT / "Shared" / "web" / "visuals" / "src" / "views" / "locales.json"

PACKAGES = ("cldr-numbers-full", "cldr-dates-full")
# The fields the hand-written data holds, nothing more. Currency codes the formatter has a symbol for; the English symbol is the reference.
CURRENCIES = ("USD", "EUR", "GBP", "JPY", "CNY", "INR", "KRW")
LICENCE = "Unicode-3.0"
LICENCE_URL = "https://www.unicode.org/license.txt"


def read_pin(path: Path = PIN) -> dict[str, str]:
    pin: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            key, _, value = line.partition("=")
            pin[key.strip()] = value.strip()
    return pin


def sha256_of(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def integrity_of(data: bytes) -> str:
    return "sha512-" + base64.b64encode(hashlib.sha512(data).digest()).decode("ascii")


def download(package: str, version: str, expected: str) -> bytes:
    url = f"https://registry.npmjs.org/{package}/-/{package}-{version}.tgz"
    with urllib.request.urlopen(url, timeout=300) as response:  # noqa: S310 - a fixed https address
        data = response.read()
    actual = integrity_of(data)
    if actual != expected:
        raise SystemExit(f"{package} {version}: the download has the integrity {actual}, the pin says {expected}")
    return data


def member(data: bytes, name: str) -> dict:
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        f = tar.extractfile(f"package/{name}")
        if f is None:
            raise SystemExit(f"{name} is not in the package")
        return json.loads(f.read().decode("utf-8"))


def build(locales: list[str], numbers: bytes, dates: bytes, version: str, pin: dict[str, str]) -> dict:
    out: dict = {
        "source": {
            "name": "Unicode CLDR (JSON)",
            "version": version,
            "licence": LICENCE,
            "licenceUrl": LICENCE_URL,
            "packages": {p: {"version": version, "integrity": pin[f"PACKAGE_{p}_INTEGRITY"]} for p in PACKAGES},
            "generatedBy": "scripts/fetch_cldr_snapshot.py",
            "note": "Only the locales and fields the suite holds by hand. Months are the stand-alone context (a month shown on its own, as an axis label or a column).",
        },
        "locales": {},
    }
    for loc in locales:
        n = member(numbers, f"main/{loc}/numbers.json")["main"][loc]["numbers"]
        if n["defaultNumberingSystem"] != "latn":
            raise SystemExit(f"{loc}: the default numbering system is {n['defaultNumberingSystem']}, not latn")
        symbols = n["symbols-numberSystem-latn"]
        greg = member(dates, f"main/{loc}/ca-gregorian.json")["main"][loc]["dates"]["calendars"]["gregorian"]["months"]["stand-alone"]
        out["locales"][loc] = {
            "decimal": symbols["decimal"],
            "group": symbols["group"],
            "months": [greg["wide"][str(i)] for i in range(1, 13)],
            "short": [greg["abbreviated"][str(i)] for i in range(1, 13)],
        }
    en = member(numbers, "main/en/numbers.json")["main"]["en"]["numbers"]
    cur = member(numbers, "main/en/currencies.json")["main"]["en"]["numbers"]["currencies"]
    out["currencySymbols"] = {"locale": "en", "symbols": {c: cur[c]["symbol"] for c in CURRENCIES}}
    short = en["decimalFormats-numberSystem-latn"]["short"]["decimalFormat"]
    out["compactSuffixes"] = {"locale": "en", "thousand": short["1000-count-other"], "million": short["1000000-count-other"], "billion": short["1000000000-count-other"]}
    return out


def render(snapshot: dict) -> str:
    return json.dumps(snapshot, ensure_ascii=False, indent=2) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verify", action="store_true", help="download, check the integrity and compare with the snapshot file")
    parser.add_argument("--write", action="store_true", help="download, check the integrity, write the snapshot and the pin's SNAPSHOT_SHA256")
    args = parser.parse_args()
    pin = read_pin()
    if not (args.verify or args.write):
        actual = sha256_of(SNAPSHOT)
        if actual != pin["SNAPSHOT_SHA256"]:
            print(f"{SNAPSHOT.relative_to(ROOT)} has the SHA-256 {actual}, the pin says {pin['SNAPSHOT_SHA256']}: regenerate it with --write, never edit it by hand", file=sys.stderr)
            return 1
        print("cldr-snapshot.json matches its pin")
        return 0
    version = pin["CLDR_VERSION"]
    locales = list(json.loads(LOCALES_JSON.read_text(encoding="utf-8")).keys())
    numbers = download("cldr-numbers-full", version, pin["PACKAGE_cldr-numbers-full_INTEGRITY"])
    dates = download("cldr-dates-full", version, pin["PACKAGE_cldr-dates-full_INTEGRITY"])
    text = render(build(locales, numbers, dates, version, pin))
    if args.write:
        SNAPSHOT.write_text(text, encoding="utf-8", newline="\n")
        lines = [ln for ln in PIN.read_text(encoding="utf-8").splitlines() if not ln.startswith("SNAPSHOT_SHA256=")]
        lines.append(f"SNAPSHOT_SHA256={sha256_of(SNAPSHOT)}")
        PIN.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {SNAPSHOT.relative_to(ROOT)} for {', '.join(locales)}")
        return 0
    if text != SNAPSHOT.read_text(encoding="utf-8"):
        print("the snapshot file differs from what the pinned CLDR packages give: run --write and read the difference", file=sys.stderr)
        return 1
    print("cldr-snapshot.json is what the pinned CLDR packages give")
    return 0


if __name__ == "__main__":
    sys.exit(main())
