#!/usr/bin/env python3
"""SC31c (plan 33, section 6b): secret names are `dbdatakit:<service>:<kind>:<name>[:<part>]`, mechanically.

    python3 -I scripts/rename_secret_names.py --dry-run     # list the files that would change, touch nothing
    python3 -I scripts/rename_secret_names.py               # do it
    python3 -I scripts/rename_secret_names.py --root PATH   # another repository root (the test uses this)

The environment variables of the suite are `DBDATAKIT_<...>`; the names of its secrets follow: the root `dbdata` becomes `dbdatakit` and the second
segment is the service or module (`server`, `gateway`, `runner`, `sync`, `build`, `store`, `workspace`). Every tracked text file is read once and
these are rewritten, in code, tests, documents, plans, workflows, scripts, TypeScript and YAML examples alike:

* `dbdata:<service>:` and `secret:dbdata:<service>:` become `dbdatakit:<service>:`, and a bare `dbdata:` (the default prefix of `secret list`, a prefix
  concatenated in code, `dbdata:{Product}:`, `dbdata:<product>`) becomes `dbdatakit:`; the placeholder `<product>` after it becomes `<service>`.
* `console` is `server` (`dbdata:console:state:x` is `dbdatakit:server:state:x`); `proxy`, the legacy name from the rename DbDataProxy to DbDataGateway, is
  `gateway` (`dbdata:proxy:datasource` is `dbdatakit:gateway:datasource`); the console's own identity `dbdata:kit:console:signing-key` is
  `dbdatakit:server:identity:signing-key`.
* A name composed from parts in code: the constant `SecretName.Root`, and the segments `new SecretName("console", ...)`, `new("console", ...)`,
  `new("proxy", ...)` and `new("kit", "console", ...)`.
* What a name becomes in other places: the environment variable (`DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS` is
  `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`) and the file of a credentials directory (`dbdata_sync_connection_orders` is `dbdatakit_sync_connection_orders`).

Left alone: this script and its test, `scripts/check_secret_names.py` and its test, `docs/ci-lessons.md` and `docs/plans/33-fewer-services.md` (they tell history),
the records of the rename DbDataProxy to DbDataGateway (`scripts/rename_proxy_to_gateway.py`, its test and `docs/release-notes/dbdatagateway-rename.md`), the tests of
the refusal of old environment variables (`SuiteEnvironmentTests.cs`: the variables, not the secrets), the block of `docs/plans/RELEASE-NOTES-PENDING.md` between
`<!-- secret-names:begin -->` and `<!-- secret-names:end -->` (the table of old and new), and `{dbdata: 'app/1'}`, the message of an app's frame, which is not a secret.

A file of a frozen format in `docs/formats.json` is never edited: when one holds an old name the script stops before changing anything and lists it.

The script refuses to run twice: when no file outside the left-alone ones holds an old name it says so and stops. Python standard library only.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

#: Files and folders whose old names are history or the subject, and are kept as they are.
KEEP = {
    "scripts/rename_secret_names.py",
    "scripts/tests/test_rename_secret_names.py",
    "scripts/check_secret_names.py",
    "scripts/tests/test_check_secret_names.py",
    "docs/ci-lessons.md",
    "docs/plans/33-fewer-services.md",
    "scripts/rename_proxy_to_gateway.py",
    "scripts/tests/test_rename_proxy_to_gateway.py",
    "docs/release-notes/dbdatagateway-rename.md",
}

#: The tests of the refusal of old environment variables: they name variables, and one of them (`DBDATA_SECRET_DBDATA_...`) is an old variable.
KEEP_ENVIRONMENT_RULE = {"Shared/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs"}

BEGIN, END = "<!-- secret-names:begin -->", "<!-- secret-names:end -->"

#: The second segment, old to new. `kit` is the console's identity (`dbdata:kit:console:signing-key`), handled first.
SERVICE = {"console": "server", "proxy": "gateway"}
SERVICES = "sync|build|gateway|console|proxy|workspace|runner|store|kit"

#: `dbdata:` as the root of a secret name: not inside a word, not the key of a message (`{dbdata: 'app/1'}`, a space follows, or `{dbdata:` directly), something follows.
ROOT = re.compile(r"(?<![A-Za-z0-9_\-~{])dbdata:(?:(console|proxy)(?=:|[^A-Za-z0-9_\-]|$))?(?=\S|$)(?!['\"]app/)")
KIT_IDENTITY = re.compile(r"(?<![A-Za-z0-9_\-~{])dbdata:kit:console:")
PLACEHOLDER = re.compile(r"(?<=dbdatakit:)(&lt;|<)product(&gt;|>)")

#: A name composed from parts in code.
ROOT_CONSTANT = re.compile(r'(public const string Root = )"dbdata";')
SEGMENT_IN_CODE = re.compile(r'(new SecretName\(|new\()"(console|proxy)"(?=,\s*")')
KIT_IN_CODE = re.compile(r'(new SecretName\(|new\()"kit",\s*"console"(?=,\s*")')

#: What a name becomes in an environment variable and in a file of a credentials directory.
ENV_NAME = re.compile(r"(?<![A-Za-z0-9_])DBDATAKIT_SECRET_DBDATA_(SYNC|BUILD|GATEWAY|CONSOLE|PROXY|WORKSPACE|RUNNER|STORE|KIT)_")
FILE_NAME = re.compile(r"(?<![A-Za-z0-9_\-~])dbdata_(sync|build|gateway|console|proxy|workspace|runner|store)_")

ENV_SERVICE = {"CONSOLE": "SERVER", "PROXY": "GATEWAY", "KIT": "SERVER"}

def rewrite(text: str, environment_rule: bool = True) -> str:
    """The new text of a file's text."""
    lines = []
    protected = False
    for line in text.split("\n"):
        if BEGIN in line:
            protected = True
        if not protected:
            line = KIT_IDENTITY.sub("dbdatakit:server:identity:", line)
            line = ROOT.sub(lambda m: "dbdatakit:" + (SERVICE[m.group(1)] if m.group(1) else ""), line)
            line = PLACEHOLDER.sub(lambda m: m.group(1) + "service" + m.group(2), line)
            line = ROOT_CONSTANT.sub(r'\1"dbdatakit";', line)
            line = KIT_IN_CODE.sub(lambda m: m.group(1) + '"server", "identity"', line)
            line = SEGMENT_IN_CODE.sub(lambda m: f'{m.group(1)}"{SERVICE[m.group(2)]}"', line)
            if environment_rule:
                line = ENV_NAME.sub(lambda m: "DBDATAKIT_SECRET_DBDATAKIT_" + ENV_SERVICE.get(m.group(1), m.group(1)) + "_", line)
            line = FILE_NAME.sub(lambda m: "dbdatakit_" + SERVICE.get(m.group(1), m.group(1)) + "_", line)
        if END in line:
            protected = False
        lines.append(line)
    return "\n".join(lines)


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout


def tracked(root: Path) -> list[str]:
    return [p for p in git(root, "ls-files", "-z").split("\0") if p]


def frozen_files(root: Path) -> set[str]:
    """The files of the formats docs/formats.json says are frozen: their bytes must not change."""
    registry = root / "docs" / "formats.json"
    if not registry.is_file():
        return set()
    data = json.loads(registry.read_text(encoding="utf-8"))
    return {f["path"] for e in data.get("formats", []) if e.get("status") == "frozen" for f in e.get("files", []) if isinstance(f, dict) and "path" in f}


def plan(root: Path) -> dict[str, str]:
    changes: dict[str, str] = {}
    for p in tracked(root):
        if p in KEEP:
            continue
        try:
            data = (root / p).read_bytes()
            if b"\0" in data[:8192]:
                continue
            text = data.decode("utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        new = rewrite(text, p not in KEEP_ENVIRONMENT_RULE)
        if new != text:
            changes[p] = new
    return changes


def run(root: Path, dry_run: bool) -> int:
    changes = plan(root)
    if not changes:
        print("Nothing to do: no file holds a secret name under `dbdata:` (the rename was done already).")
        return 0
    frozen = sorted(set(changes) & frozen_files(root))
    if frozen:
        print("Stopped, nothing changed: a file of a frozen format holds an old secret name, and a frozen format is never edited. Decide each by hand (the next version of the format):", file=sys.stderr)
        for p in frozen:
            print(f"  {p}", file=sys.stderr)
        return 1
    print(f"{len(changes)} file(s) to change.")
    for p in sorted(changes):
        print(f"  edit {p}")
    if dry_run:
        print("Dry run: nothing was touched.")
        return 0
    for p, text in changes.items():
        (root / p).write_bytes(text.encode("utf-8"))
    print("Done. Review `git diff`, then build, run the checks of CLAUDE.md and `python3 -I scripts/check_secret_names.py`.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(Path(__file__).resolve().parent.parent), help="the repository root (default: this repository)")
    parser.add_argument("--dry-run", action="store_true", help="list what would change, and touch nothing")
    args = parser.parse_args(argv)
    return run(Path(args.root).resolve(), args.dry_run)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
