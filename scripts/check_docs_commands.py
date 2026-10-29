#!/usr/bin/env python3
"""No product document, script or workflow uses a command name that plan 13 retired (slice X03). The retired forms are listed here with what replaced
them; history (plans, progress logs, research, phase documents, release notes) and test fixtures are not checked, because they say what was true then.
Run from anywhere: python3 scripts/check_docs_commands.py [root]
"""
import os
import re
import sys

SKIP_DIRS = {"node_modules", "bin", "obj", ".git", "__pycache__", "tests", "architecture", "progress", "research", "plans", "review"}
SKIP_FILES = {"RELEASE-NOTES-PENDING.md", "CHANGELOG.md", "review.md"}
OK_MARK = "retired-name-ok"   # a line that must name the old form on purpose (a transitional step) says so, on itself or the line above
EXTENSIONS = (".md", ".yml", ".yaml", ".ps1", ".sh", ".service", ".wxs", ".json", ".py", ".txt")

# (pattern, what to say instead). A pattern matches a command as typed or written in a unit file, a script or a workflow.
RETIRED = [
    (r"\bdbdatasync(?:\.exe)?\s+serve\b", "dbdatasync server run"),
    (r"\bdbdatasync(?:\.exe)?\s+setup\b", "dbdatasync server setup"),
    (r"\bdbdatasync(?:\.exe)?\s+config\s+(cert|secret|library|driver)\b", "dbdatasync <cert|secret|library|driver> ... (no longer under config)"),
    (r"\bdbdataproxy(?:\.exe)?\s+hash-password\b", "dbdataproxy secret hash"),
    (r"\bdbdataproxy(?:\.exe)?\s+protect-secret\b", "dbdataproxy secret protect"),
    (r"\bdbdataproxy(?:\.exe)?\s+(install|uninstall)\b", "dbdataproxy service install|uninstall"),
    (r"\bdbdataproxy(?:\.exe)?\s+query(?!\s+run)\b", "dbdataproxy query run"),
    (r"\bdbdataproxy(?:\.exe)?\s+--healthcheck\b", "dbdataproxy health check"),
    (r"\bDbDataProxy(?:\.Worker)?\.exe\b", "dbdataproxy.exe / dbdataproxy-worker.exe (lower case)"),
]


def files(root):
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name in SKIP_FILES or not name.endswith(EXTENSIONS):
                continue
            yield os.path.join(dirpath, name)


def check(root):
    problems = []
    compiled = [(re.compile(pattern), instead) for pattern, instead in RETIRED]
    for path in files(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        try:
            lines = open(path, encoding="utf-8").read().splitlines()
        except (UnicodeDecodeError, OSError):
            continue
        for number, line in enumerate(lines, 1):
            if OK_MARK in line or (number > 1 and OK_MARK in lines[number - 2]):
                continue
            for pattern, instead in compiled:
                if pattern.search(line):
                    problems.append(f"{rel}:{number}: a retired command name; write `{instead}`")
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for problem in problems:
        print(problem)
    print(f"{len(problems)} problem(s)" if problems else "no document uses a retired command name")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
