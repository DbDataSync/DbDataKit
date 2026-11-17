#!/usr/bin/env python3
"""Nothing under planning/wip/ reaches main (planning/ROLES.md).

A subject agent keeps its work-in-progress notes in planning/wip/<SLUG>/<TASK-ID>.md while it works: what is built and what is not, defaults it took,
decisions it needs, things for other subjects. Before its pull request merges it resolves each item (finished work out of the subject file, an open
question into the subject's proposed decisions, a need into "Needs from other subjects") and deletes the file. This check fails while any file exists
under planning/wip/ and lists each file with its open items (its headings and list lines). It ignores nothing: the folder normally does not exist.
CI runs it on pull requests into main and on pushes to main only, so a branch may carry wip files.
Run from anywhere: python3 -I scripts/check_no_wip.py [root]
"""
import os
import re
import sys

ITEM = re.compile(r"^\s*(#{1,6}\s+.+|[-*]\s+.+|\d+\.\s+.+)$")


def wip_files(root):
    folder = os.path.join(root, "planning", "wip")
    found = []
    if not os.path.isdir(folder):
        return found
    for current, _dirs, files in os.walk(folder):
        for name in files:
            found.append(os.path.join(current, name))
    return sorted(found)


def items(path):
    try:
        with open(path, encoding="utf-8") as f:
            lines = f.read().splitlines()
    except (UnicodeDecodeError, OSError):
        return ["(not a text file)"]
    return [line.strip() for line in lines if ITEM.match(line)]


def problems(root):
    out = []
    for path in wip_files(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        out.append(f"{rel}: a work-in-progress file; resolve its items and delete it before merging into main")
        for item in items(path):
            out.append(f"  {item}")
    return out


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    found = problems(root)
    for line in found:
        print(line)
    count = len(wip_files(root))
    print(f"{count} work-in-progress file(s) under planning/wip/" if count else "no work-in-progress files")
    return 1 if count else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
