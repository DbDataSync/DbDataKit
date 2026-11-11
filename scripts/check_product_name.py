#!/usr/bin/env python3
"""The product is DbDataKit (plan 33 SC32): the Windows service, the display name, the folder and the words people read are `DbDataKit`.
The names `DbDataConsole` and `DbDataKitConsole` are gone, with no transition from them. This check fails, naming the file and the line, for
any tracked text file that holds either of them, except:

  * the files that name them on purpose and are exempt for good (`EXEMPT_FILES`, `EXEMPT_PREFIXES`): the scripts of earlier renames and their tests,
    the history files (`docs/ci-lessons.md`, `docs/releases.md`, `docs/plans/RELEASE-NOTES-PENDING.md`) and this check with its test;
  * the files in `scripts/product_name_baseline.txt`, one `path: count` per line, up to that many occurrences (a plan that explains the rename
    names the old words; it is deleted when its last slice lands). The baseline only shrinks: a count that is higher than the file's fails the
    check, and so does a count that is higher than what is left, or an entry for a file with none, so delete or lower the line.

Binary files are skipped. Tracked files come from `git ls-files`; a folder that is not a repository is walked.
Run from anywhere: python3 -I scripts/check_product_name.py [root]
"""
import os
import re
import subprocess
import sys

OLD_NAME = re.compile(r"DbDataConsole|DbDataKitConsole")
BASELINE = "scripts/product_name_baseline.txt"

# Files that name the old words on purpose and stay so: path (relative, '/') -> why.
EXEMPT_FILES = {
    "docs/ci-lessons.md": "CI lessons name the workflow and the jobs of the day they were learnt",
    "docs/releases.md": "the release history names the products and workflows of its time",
    "docs/plans/RELEASE-NOTES-PENDING.md": "release notes: old and new names, on purpose",
    "scripts/check_product_name.py": "this script holds the patterns",
    "scripts/tests/test_check_product_name.py": "the test of this script",
}
# The scripts of earlier renames and their tests.
EXEMPT_PREFIXES = ("scripts/rename_", "scripts/tests/test_rename_")


def tracked_files(root):
    """Relative paths ('/'), from git when `root` is a repository, else by walking it."""
    if os.path.exists(os.path.join(root, ".git")):
        try:
            out = subprocess.run(["git", "-C", root, "ls-files", "-z"], capture_output=True, check=True).stdout.decode("utf-8", "replace")
            return [p for p in out.split("\0") if p]
        except (OSError, subprocess.CalledProcessError):
            pass
    files = []
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d != ".git"]
        for name in names:
            files.append(os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/"))
    return files


def load_baseline(root):
    """{path: allowed count}; lines starting with # and blank lines are comments."""
    result = {}
    path = os.path.join(root, BASELINE)
    if not os.path.exists(path):
        return result
    with open(path, encoding="utf-8") as f:
        for number, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            name, _, count = line.rpartition(":")
            if not name or not count.strip().isdigit():
                raise ValueError(f"{BASELINE}:{number}: expected `path: count`, got `{line}`")
            result[name.strip()] = int(count)
    return result


def hits(root):
    """{path: [(line number, shortened line, occurrences)]} for every tracked text file that is not exempt for good."""
    found = {}
    for rel in tracked_files(root):
        if rel in EXEMPT_FILES or rel == BASELINE or rel.startswith(EXEMPT_PREFIXES):
            continue
        try:
            with open(os.path.join(root, rel), "rb") as f:
                data = f.read()
        except OSError:
            continue
        if b"\0" in data:
            continue
        for number, line in enumerate(data.decode("utf-8", "replace").splitlines(), 1):
            if OLD_NAME.search(line):
                found.setdefault(rel, []).append((number, line.strip()[:160], len(OLD_NAME.findall(line))))
    return found


def check(root):
    """Returns (problems, warnings): problems fail the check; warnings are what the baseline still tolerates."""
    baseline = load_baseline(root)
    found = hits(root)
    problems, warnings = [], []
    for rel, lines in sorted(found.items()):
        allowed = baseline.get(rel, 0)
        count = sum(n for _, _, n in lines)
        if allowed == 0:
            for number, text, _ in lines:
                problems.append(f"{rel}:{number}: names DbDataConsole or DbDataKitConsole; the product is DbDataKit: {text}")
        elif count > allowed:
            problems.append(f"{rel}: {count} occurrences, the baseline allows {allowed}; the product is DbDataKit")
        elif count < allowed:
            problems.append(f"{rel}: {count} occurrences left, the baseline says {allowed}; lower it in {BASELINE}")
        else:
            warnings.append(f"{rel}: {count} (baseline)")
    for rel in sorted(set(baseline) - set(found)):
        problems.append(f"{rel}: in {BASELINE} but names neither word any more (or is not tracked); delete the line")
    return problems, warnings


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems, warnings = check(root)
    for w in warnings:
        print(f"warning (baseline): {w}")
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "the product is named DbDataKit everywhere")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
