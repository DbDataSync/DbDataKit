#!/usr/bin/env python3
"""The source scan of plan 33 SC31a: no product-prefixed environment variable survives. Every variable of the suite starts `DBDATAKIT_` (plan 33
section 6c); the product prefixes `DBDATASTORE_`, `DBDATARUNNER_`, `DBDATABUILD_`, `DBDATAWORKSPACE_`, `DBDATASYNC_`, `DBDATAGATEWAY_`,
`DBDATAPROXY_`, `DBGATEWAY_`, `DBPROXY_`, `DBDATA_` and `DBKIT_` are gone, and a service that finds an old name set refuses to start and names the
new one (`SuiteEnvironment.OldNames` and `OldPrefixes`). This check fails, naming the file and the line, for any tracked file that holds one of the
old names, except:

  * the files that hold the refusal list and the tests of it, the plans that name old and new on purpose, `docs/ci-lessons.md`, `docs/testing-environment.md`, the records of
    earlier renames, and this script (`EXEMPT_FILES`).

The variables of tests, harnesses and CI are `DBDATAKIT_TEST_<NAME>` since plan 33 SC31b (the table is `docs/testing-environment.md`), so no
product-prefixed name is allowed anywhere else.

A name is the run of letters, digits and underscores that starts at a product prefix and is not the end of a longer word, so `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`
(a secret's name inside a variable's) is not found. Binary files are skipped. Tracked files come from `git ls-files`; a folder that is not a
repository is walked.
Run from anywhere: python3 -I scripts/check_env_names.py [root]
"""
import os
import re
import subprocess
import sys

PRODUCTS = "STORE|RUNNER|BUILD|WORKSPACE|SYNC|GATEWAY|PROXY"
OLD_NAME = re.compile(rf"(?<![A-Za-z0-9_])(?:DBDATA(?:{PRODUCTS})_|DBGATEWAY_|DBPROXY_|DBDATA_|DBKIT_)[A-Za-z0-9_]*")

# Files that name old variables on purpose: path (relative, '/') -> why.
EXEMPT_FILES = {
    "Shared/src/DbDataKit.State.Engines/SuiteEnvironment.cs": "the refusal list (OldNames, OldPrefixes)",
    "Shared/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs": "the tests of the refusal list",
    "dist/tests/test_dist.py": "asserts that the machine variables of earlier releases are not set any more",
    "docs/plans/RELEASE-NOTES-PENDING.md": "release notes: old and new names, on purpose",
    "docs/plans/33-fewer-services.md": "the plan of the rename: the old-name column",
    "docs/testing-environment.md": "the table of the old test variable names and the new ones",
    "docs/ci-lessons.md": "CI lessons name the test variables of the day they were learnt",
    "docs/release-notes/dbdatagateway-rename.md": "the release note of the rename DbDataProxy to DbDataGateway",
    "scripts/rename_secret_names.py": "the script of the secret-name rename (it names the old variable forms it rewrites)",
    "scripts/tests/test_rename_secret_names.py": "the test of that script",
    "scripts/rename_proxy_to_gateway.py": "the script of the rename DbDataProxy to DbDataGateway",
    "scripts/tests/test_rename_proxy_to_gateway.py": "the test of that script",
    "scripts/check_env_names.py": "this script holds the patterns",
    "scripts/tests/test_check_env_names.py": "the test of this script",
}

# Each product's tests of the refusal live in a file of this name: they name the old variables they set.
EXEMPT_SUFFIXES = ("/SuiteEnvironmentRefusalTests.cs",)


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


def is_exempt(path):
    return path in EXEMPT_FILES or path.endswith(EXEMPT_SUFFIXES)


def check(root):
    """Returns the list of problems: `path:line: NAME ...`."""
    problems = []
    for path in sorted(tracked_files(root)):
        if is_exempt(path):
            continue
        full = os.path.join(root, *path.split("/"))
        try:
            with open(full, "rb") as f:
                data = f.read()
        except OSError:
            continue
        if b"\0" in data[:8192]:
            continue
        for number, line in enumerate(data.decode("utf-8", "replace").splitlines(), 1):
            if "DBDATA" not in line and "DBGATEWAY" not in line and "DBPROXY" not in line and "DBKIT" not in line:
                continue
            for match in OLD_NAME.finditer(line):
                problems.append(f"{path}:{number}: {match.group(0)} is an environment variable name with a product prefix; every name starts DBDATAKIT_ (plan 33 section 6c)")
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "no product-prefixed environment variable names")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
