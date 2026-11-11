#!/usr/bin/env python3
"""SC30 (plan 33): rename the repository folders, mechanically.

    python3 -I scripts/rename_to_shared_and_dbdatakit.py --dry-run     # list what would move and change, touch nothing
    python3 -I scripts/rename_to_shared_and_dbdatakit.py               # do it (git mv for every path, then the file contents)
    python3 -I scripts/rename_to_shared_and_dbdatakit.py --root PATH   # another repository root (the test uses this)

The two moves, in this order and in one run, so the second never meets an existing `DbDataKit/`:

1. `DbDataKit/` (the shared libraries) becomes `Shared/`.
2. `DbDataConsole/` (the console product) becomes `DbDataKit/`; its projects `DbDataConsole.Server` becomes `DbDataKit.Server`, and its test projects
   `DbDataConsole.Tests`, `.Servers.Tests` and `.Web.Tests` become `DbDataKit.Server.Tests`, `.Server.Servers.Tests` and `.Server.Web.Tests`
   (the namespaces follow); `DbDataConsole.slnf` becomes `DbDataKit.slnf`.

Every tracked text file is read once, and its new text is computed from the old text with both passes at once (so nothing is rewritten twice, and the
`DbDataKit/` that the second pass writes is never read by the first):

* pass 1, the old library folder as a PATH: `DbDataKit/` or `DbDataKit\\` followed by `src`, `tests`, `web`, `conformance`, `tools`, `benchmarks`, `build`
  (with a slash, or after `..\\`), `global.json`, `Directory.Build.props`, `README.md` or the shared solution; the bare folder (`DbDataKit/`, `DbDataKit/**`);
  the quoted segments of a path built in code (`"DbDataKit", "src"`, `"..", "DbDataKit"`); `working-directory: DbDataKit`. What is NOT a folder keeps its
  name: the GitHub repository (`DbDataSync/DbDataKit`, `.../DbDataKit/tree/...`), the machine folder (`%ProgramData%\\DbDataKit\\gateway`, `"DbDataKit", name`),
  the product name, the font families and the `DbDataKit.*` package, namespace and project names.
* pass 2, the console: the project names above (in every file type), the folder `DbDataConsole/` and `DbDataConsole\\`, and, in files that are code or
  configuration (not prose), the bare name `DbDataConsole` (a product key in a script, `product: DbDataConsole` of a workflow step). Prose in
  documents keeps the word `DbDataConsole`: the words are SC32's (plan 33), which adds the check that finds what is left.

Never touched (their content): this script and its test, the older rename script and its test, plan 33 (it says what was renamed; its SC30 row is
edited by hand), and the files of a frozen format in `docs/formats.json` (a changed byte would break the recorded hash; the moved file keeps its hash and
the registry's own paths are rewritten). The workflow `name:` lines (`DbDataConsole CI`) are not touched either: `suite-release.yml` selects runs by that name.

The script refuses to run twice: once `Shared/` exists and `DbDataConsole/` does not, it says so and stops (a second pass would read the new
`DbDataKit/` as the old one). Python standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

# ---- what is left alone ------------------------------------------------------------------------------------------------------------------

KEEP = {
    "scripts/rename_to_shared_and_dbdatakit.py",
    "scripts/tests/test_rename_to_shared_and_dbdatakit.py",
    "scripts/rename_proxy_to_gateway.py",
    "scripts/tests/test_rename_proxy_to_gateway.py",
    "docs/plans/33-fewer-services.md",
}

#: File types whose bare `DbDataConsole` is a name in code or configuration (the rest of the files are prose, which keeps the word).
CODE_SUFFIXES = (".py", ".sh", ".ps1", ".yml", ".yaml", ".json", ".ts", ".mjs", ".js", ".csproj", ".props", ".targets", ".slnf", ".slnx",
                 ".wxs", ".nsi", ".dockerignore", ".txt")
CODE_NAMES = ("Dockerfile",)

# ---- paths -------------------------------------------------------------------------------------------------------------------------------

#: Component renames inside the console's folder (applied to a path component of a file that was under DbDataConsole/).
COMPONENT_RULES = [
    (re.compile(r"DbDataConsole\.slnf"), "DbDataKit.slnf"),
    (re.compile(r"DbDataConsole\.Server(?![A-Za-z0-9_])"), "DbDataKit.Server"),
    (re.compile(r"DbDataConsole\.(?=[A-Z])"), "DbDataKit.Server."),
]


def new_path(path: str) -> str:
    if path.startswith("DbDataKit/"):
        return "Shared/" + path[len("DbDataKit/"):]
    if path.startswith("DbDataConsole/"):
        parts = ["DbDataKit"] + path.split("/")[1:]
        for i, part in enumerate(parts[1:], 1):
            for pattern, replacement in COMPONENT_RULES:
                part = pattern.sub(replacement, part)
            parts[i] = part
        return "/".join(parts)
    return path


# ---- contents ----------------------------------------------------------------------------------------------------------------------------

_SUBS = r"src|tests|web|conformance|tools|benchmarks|global\.json|Directory\.Build\.props|DbDataKit\.Shared\.slnx|README\.md"
#: `DbDataKit/src`, `DbDataKit\tests`, `DbDataKit\\web` (a JSON string) and the rest of the folders of the libraries.
FOLDER_PATH = re.compile(rf"(?<![A-Za-z0-9_.\-])DbDataKit(?P<sep>/|\\\\|\\)(?P<sub>{_SUBS})(?![A-Za-z0-9_])")
#: `build` is also a machine folder (`%ProgramData%\DbDataKit\build`): a folder of the libraries only with a slash or after `..\`.
BUILD_PATH = re.compile(r"(?<![A-Za-z0-9_.\-])DbDataKit/build(?![A-Za-z0-9_])|(?<=\.\.\\)DbDataKit(?=\\build(?![A-Za-z0-9_]))|(?<=\.\.\\\\)DbDataKit(?=\\\\build(?![A-Za-z0-9_]))")
#: The bare folder: `DbDataKit/`, `DbDataKit/**`, `(DbDataBuild/|DbDataKit/|testenv/)`. Not after a slash (the GitHub repository).
BARE_FOLDER = re.compile(r"(?<![A-Za-z0-9_./\\\-])DbDataKit/(?![A-Za-z0-9_.{<-])")
#: A path built from quoted segments in code: "DbDataKit", "src" and "..", "DbDataKit".
QUOTED_SEGMENT = re.compile(r"""(?<=["'])DbDataKit(?=["']\s*,\s*["'](?:src|tests|web|conformance|tools|benchmarks|build|global\.json|DbDataKit\.Shared\.slnx)["'])""")
QUOTED_AFTER_PARENT = re.compile(r"""(["']\.\.["']\s*,\s*["'])DbDataKit(["'])""")
WORKING_DIRECTORY = re.compile(r"(working-directory:\s*)DbDataKit[ \t]*$", re.M)
#: "the shared DbDataKit/ code" reads "the Shared/ code" and not "the shared Shared/ code".
SHARED_PROSE = re.compile(r"\bshared (?=Shared/)")


def library_paths(text: str) -> str:
    text = FOLDER_PATH.sub(lambda m: "Shared" + m.group("sep") + m.group("sub"), text)
    text = BUILD_PATH.sub(lambda m: "Shared/build" if m.group(0).startswith("DbDataKit/") else "Shared", text)
    text = BARE_FOLDER.sub("Shared/", text)
    text = QUOTED_SEGMENT.sub("Shared", text)
    text = QUOTED_AFTER_PARENT.sub(r"\1Shared\2", text)
    text = WORKING_DIRECTORY.sub(r"\1Shared", text)
    return SHARED_PROSE.sub("", text)


CONSOLE_NAMES = [
    (re.compile(r"DbDataConsole\.slnf"), "DbDataKit.slnf"),
    (re.compile(r"DbDataConsole\.Server(?![A-Za-z0-9_])"), "DbDataKit.Server"),
    (re.compile(r"DbDataConsole\.(?=[A-Z])"), "DbDataKit.Server."),
    (re.compile(r"DbDataConsole(?=[/\\])"), "DbDataKit"),
]
#: Bare `DbDataConsole` in code and configuration; `DbDataConsole CI` is the name of a workflow that other workflows select by name.
BARE_CONSOLE = re.compile(r"DbDataConsole(?![A-Za-z0-9_]| CI)")


def is_code(path: str) -> bool:
    name = path.rsplit("/", 1)[-1]
    return path.endswith(CODE_SUFFIXES) or name.startswith(CODE_NAMES)


def transform(path: str, text: str) -> str:
    """The new text of one file (the same text when nothing applies). `path` is the old path."""
    text = library_paths(text)
    for pattern, replacement in CONSOLE_NAMES:
        text = pattern.sub(replacement, text)
    if is_code(path):
        text = BARE_CONSOLE.sub("DbDataKit", text)
    return text


# ---- the run -----------------------------------------------------------------------------------------------------------------------------

def git(root: Path, *args: str) -> str:
    # `-C root` names the repository; a GIT_DIR or the like inherited from a hook or `git rebase -x` would override it.
    env = {k: v for k, v in os.environ.items() if k not in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_PREFIX", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE")}
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True, env=env).stdout


def tracked(root: Path) -> list[str]:
    return [p for p in git(root, "ls-files", "-z").split("\0") if p]


def frozen_files(root: Path) -> set[str]:
    """The files of the formats docs/formats.json says are frozen: their bytes must not change."""
    registry = root / "docs" / "formats.json"
    if not registry.is_file():
        return set()
    data = json.loads(registry.read_text(encoding="utf-8"))
    return {f["path"] for e in data.get("formats", []) if e.get("status") == "frozen" for f in e.get("files", []) if isinstance(f, dict) and "path" in f}


def already_done(root: Path) -> bool:
    return (root / "Shared").is_dir() and not (root / "DbDataConsole").exists()


def plan(root: Path) -> tuple[list[tuple[str, str]], dict[str, str]]:
    files = tracked(root)
    skip = KEEP | frozen_files(root)
    changes: dict[str, str] = {}
    for p in files:
        if p in skip:
            continue
        try:
            text = (root / p).read_bytes().decode("utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        new = transform(p, text)
        if new != text:
            changes[p] = new
    return [(p, new_path(p)) for p in files if new_path(p) != p], changes


def move_steps(moves: list[tuple[str, str]]) -> list[tuple[str, str]]:
    """The `git mv` calls that carry out the file moves: the two folders first, then each renamed component inside (a folder at a time)."""
    steps = [("DbDataKit", "Shared"), ("DbDataConsole", "DbDataKit")]
    current = {}
    for old, new in moves:
        parts = old.split("/")
        parts[0] = {"DbDataKit": "Shared", "DbDataConsole": "DbDataKit"}[parts[0]]
        current[old] = "/".join(parts)
    pending = {cur: new_path(old) for old, cur in current.items() if cur != new_path(old)}
    inner: list[tuple[str, str]] = []
    for cur, target in pending.items():
        a, b = cur.split("/"), target.split("/")
        for i in range(len(a)):
            if a[i] != b[i]:
                step = ("/".join(a[:i + 1]), "/".join(a[:i] + [b[i]]))
                if step not in inner:
                    inner.append(step)
    # every renamed component is a step, the deepest first: a step's source and target keep the names of the components above it
    inner.sort(key=lambda s: -s[0].count("/"))
    return steps + inner


def run(root: Path, dry_run: bool) -> int:
    if already_done(root):
        print("Nothing to do: Shared/ exists and DbDataConsole/ does not. (A second run would read the new DbDataKit/ as the old one.)")
        return 0
    if not (root / "DbDataConsole").is_dir() or not (root / "DbDataKit").is_dir():
        print("Both DbDataKit/ and DbDataConsole/ must exist.", file=sys.stderr)
        return 1
    moves, changes = plan(root)
    steps = move_steps(moves)
    print(f"{len(moves)} path(s) move in {len(steps)} git mv step(s), {len(changes)} file(s) to change.")
    for old, new in steps:
        print(f"  mv   {old} -> {new}")
    for p in sorted(changes):
        print(f"  edit {p}")
    if dry_run:
        print("Dry run: nothing was touched.")
        return 0
    for old, new in steps:
        git(root, "mv", old, new)
    moved = dict(moves)
    for p, text in changes.items():
        (root / moved.get(p, p)).write_bytes(text.encode("utf-8"))
    print("Done. Review `git status`, then build and run the checks of CLAUDE.md.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(Path(__file__).resolve().parent.parent), help="the repository root (default: this repository)")
    parser.add_argument("--dry-run", action="store_true", help="list what would move and change, and touch nothing")
    args = parser.parse_args(argv)
    return run(Path(args.root).resolve(), args.dry_run)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
