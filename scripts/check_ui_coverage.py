#!/usr/bin/env python3
"""docs/ui-coverage.md names every screen file of the web modules, and only files that exist.

The matrix itself (which actions each screen offers) is written by hand; this check only keeps its Screens column from drifting from the code:
  1. every page, tab or panel file under DbDataKit/web/<module>/src/pages (not tests) is named in the document;
  2. every `Something.tsx` the document names exists in one of those folders.
Run from anywhere: python3 scripts/check_ui_coverage.py [root]
"""
import os
import re
import sys

MODULES = ["console", "sync", "proxy", "build", "admin"]
SCREEN = re.compile(r"(Page|Pages|Tab|Panel)\.tsx$|^PendingServers\.tsx$|^ServerModule\.tsx$|^FileTools\.tsx$")


def screens(root):
    found = {}
    for module in MODULES:
        folder = os.path.join(root, "DbDataKit", "web", module, "src", "pages")
        if not os.path.isdir(folder):
            continue
        for name in os.listdir(folder):
            if name.endswith(".tsx") and ".test." not in name and SCREEN.search(name):
                found[name] = module
    return found


def problems(root):
    doc_path = os.path.join(root, "docs", "ui-coverage.md")
    if not os.path.exists(doc_path):
        return ["docs/ui-coverage.md does not exist"]
    with open(doc_path, encoding="utf-8") as f:
        text = f.read()
    named = set(re.findall(r"`([A-Za-z]+\.tsx)`", text))
    existing = screens(root)
    every_tsx = set()
    for module in MODULES:
        folder = os.path.join(root, "DbDataKit", "web", module, "src", "pages")
        if os.path.isdir(folder):
            every_tsx.update(n for n in os.listdir(folder) if n.endswith(".tsx"))
    out = [f"docs/ui-coverage.md does not name {name} ({module})" for name, module in sorted(existing.items()) if name not in named]
    out += [f"docs/ui-coverage.md names {name}, which is not a screen file of any web module" for name in sorted(named - every_tsx)]
    return out


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    found = problems(root)
    for line in found:
        print(line)
    print("OK" if not found else f"{len(found)} problem(s)")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
