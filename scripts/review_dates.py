#!/usr/bin/env python3
"""Which docs and planning subjects have been reviewed, and when (planning/ROLES.md, "Review dates").

A reviewed file carries `<!-- reviewed: YYYY-MM-DD -->` on a line near its top, under its title. This lists the files that have no such line
(not reviewed yet) and the date of each that has one. It fails only on a malformed review line, so a typo is caught; a missing date is reported.

Files it reads: every `.md` under `docs/` (not `docs/release-notes/`), every `README.md` outside `node_modules`, `bin`, `obj` and dot folders, and
every file in `planning/` except the index `README.md` (the subjects and the roles; not `planning/wip/`).

Run from anywhere: python3 -I scripts/review_dates.py [root] [--missing]
"""
import os
import re
import sys

LINE = re.compile(r"^<!--\s*reviewed:\s*(?P<date>\S+)\s*-->\s*$")
DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
LOOSE = re.compile(r"<!--\s*reviewed\b", re.IGNORECASE)
SKIP_DIRS = {"node_modules", "bin", "obj", "wip", "release-notes"}
HEAD_LINES = 5


def files(root):
    out = []
    docs = os.path.join(root, "docs")
    for folder, dirs, names in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
        rel = os.path.relpath(folder, root).replace(os.sep, "/")
        for name in sorted(names):
            if not name.endswith(".md"):
                continue
            path = f"{name}" if rel == "." else f"{rel}/{name}"
            under_docs = folder == docs or folder.startswith(docs + os.sep)
            planning = rel == "planning"
            if planning and name == "README.md":
                continue
            if under_docs or planning or name == "README.md":
                out.append(path)
    return out


def review(root, path):
    """(date or None, problem or None) for one file."""
    with open(os.path.join(root, path), encoding="utf-8") as f:
        head = [next(f, "") for _ in range(HEAD_LINES)]
    for line in head:
        line = line.rstrip("\n")
        if not LOOSE.search(line):
            continue
        m = LINE.match(line)
        if not m or not DATE.match(m.group("date")):
            return None, f"{path}: the review line must be exactly <!-- reviewed: YYYY-MM-DD -->, found: {line.strip()}"
        return m.group("date"), None
    return None, None


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    root = os.path.abspath(args[0]) if args else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    missing_only = "--missing" in argv
    problems, dated, missing = [], [], []
    for path in files(root):
        date, problem = review(root, path)
        if problem:
            problems.append(problem)
        elif date:
            dated.append((path, date))
        else:
            missing.append(path)
    if not missing_only:
        for path, date in dated:
            print(f"reviewed {date}  {path}")
    for path in missing:
        print(f"not reviewed      {path}")
    for problem in problems:
        print(problem, file=sys.stderr)
    print(f"{len(dated)} reviewed, {len(missing)} not reviewed", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
