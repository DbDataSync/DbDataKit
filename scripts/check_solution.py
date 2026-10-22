#!/usr/bin/env python3
"""DbDataKit.slnx lists every project in the repository (plan 13, slice F09), and every product's .slnf filter names only projects
that are in it. Run from anywhere: python3 scripts/check_solution.py [root]
"""
import json
import os
import re
import sys

SKIP_DIRS = {"node_modules", "bin", "obj", ".git"}


def projects(root):
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name.endswith(".csproj"):
                yield os.path.relpath(os.path.join(dirpath, name), root).replace(os.sep, "/")


def check(root):
    problems = []
    listed = re.findall(r'<Project\s+Path="([^"]+)"', open(os.path.join(root, "DbDataKit.slnx"), encoding="utf-8").read())
    for path in sorted(set(projects(root)) - set(listed)):
        problems.append(f"DbDataKit.slnx: {path} is not listed")
    for path in sorted(set(listed) - set(projects(root))):
        problems.append(f"DbDataKit.slnx: {path} does not exist")
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name.endswith(".slnf"):
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, root).replace(os.sep, "/")
                solution = json.load(open(full, encoding="utf-8"))["solution"]
                for path in solution["projects"]:
                    if path not in listed:
                        problems.append(f"{rel}: {path} is not in DbDataKit.slnx")
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for problem in problems:
        print(problem)
    print(f"{len(problems)} problem(s)" if problems else "DbDataKit.slnx lists every project")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
