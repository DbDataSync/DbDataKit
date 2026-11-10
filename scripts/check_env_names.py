#!/usr/bin/env python3
"""The source scan of plan 33 SC31a: no product-prefixed environment variable survives. Every variable of the suite starts `DBDATAKIT_` (plan 33
section 6c); the product prefixes `DBDATASTORE_`, `DBDATARUNNER_`, `DBDATABUILD_`, `DBDATAWORKSPACE_`, `DBDATASYNC_`, `DBDATAGATEWAY_`,
`DBDATAPROXY_`, `DBGATEWAY_`, `DBPROXY_`, `DBDATA_` and `DBKIT_` are gone, and a service that finds an old name set refuses to start and names the
new one (`SuiteEnvironment.OldNames` and `OldPrefixes`). This check fails, naming the file and the line, for any tracked file that holds one of the
old names, except:

  * the files that hold the refusal list and the tests of it, the plans that name old and new on purpose, `docs/ci-lessons.md`, the records of
    earlier renames, and this script (`EXEMPT_FILES`);
  * the variables of tests, harnesses and CI that plan 33 SC31b renames (`SC31B_ALLOWED`: a list of name patterns; SC31b empties it).

A name is the run of letters, digits and underscores that starts at a product prefix and is not the end of a longer word, so `DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS`
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
    "DbDataKit/src/DbDataKit.State.Engines/SuiteEnvironment.cs": "the refusal list (OldNames, OldPrefixes)",
    "DbDataKit/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs": "the tests of the refusal list",
    "dist/tests/test_dist.py": "asserts that the machine variables of earlier releases are not set any more",
    "docs/plans/RELEASE-NOTES-PENDING.md": "release notes: old and new names, on purpose",
    "docs/plans/33-fewer-services.md": "the plan of the rename: the old-name column",
    "docs/ci-lessons.md": "CI lessons name the test variables of the day they were learnt",
    "docs/release-notes/dbdatagateway-rename.md": "the release note of the rename DbDataProxy to DbDataGateway",
    "scripts/rename_proxy_to_gateway.py": "the script of the rename DbDataProxy to DbDataGateway",
    "scripts/tests/test_rename_proxy_to_gateway.py": "the test of that script",
    "scripts/check_env_names.py": "this script holds the patterns",
    "scripts/tests/test_check_env_names.py": "the test of this script",
    # SC31b renames the variables of this test (they are test variables)
    "DbDataKit/tests/DbDataKit.Odbc.Metadata.Tests/SqlServerCatalogTests.cs": "SC31b: builds its test variables from the old prefixes",
}

# Each product's tests of the refusal live in a file of this name: they name the old variables they set.
EXEMPT_SUFFIXES = ("/SuiteEnvironmentRefusalTests.cs",)

# The variables of tests, harnesses and CI that plan 33 SC31b renames (`DBDATAKIT_TEST_*`): until then they keep their names. Remove each
# pattern when its variables are renamed; SC31b ends with this list empty.
SC31B_ALLOWED = [re.compile(p) for p in (
    r"DBDATA_TEST_", r"DBKIT_TEST_", r"DBDATASYNC_TEST_", r"DBDATABUILD_TEST_", r"DBPROXY_TEST_", r"DBGATEWAY_TEST_",
    r"DBDATA_E2E_", r"DBDATASYNC_E2E_", r"DBGATEWAY_E2E_",
    r"DBDATA_CONFORMANCE_",
    r"DBDATA_REQUIRE", r"DBGATEWAY_REQUIRE_", r"DBPROXY_REQUIRE_",
    r"DBDATA_BENCH", r"DBDATA_XMLA_RECORDING_DIR", r"DBDATA_METADATA_", r"DBDATA_APP_STORE",
    r"DBDATA_ADMIN_TESTS", r"DBGATEWAY_ADMIN_TESTS", r"DBGATEWAY_LIVE_",
    r"DBDATA_E23_ERP_PASSWORD", r"DBDATA_E12E_KEEP",
    r"DBDATA_UPDATE_GOLDEN", r"DBDATA_UPDATE_SNAPSHOTS", r"DBDATA_SNAPSHOT_FILE", r"DBGATEWAY_UPDATE_GOLDEN",
    r"DBKIT_UPDATE_PROTOCOL_SNAPSHOT", r"DBDATA_REGENERATE_WIDGET_RESULTS",
    r"DBDATASYNC_MATRIX_UPDATE", r"DBDATASYNC_HARNESS_", r"DBDATASYNC_MSSQL_SA_PASSWORD", r"DBDATASYNC_POSTGRES_PASSWORD", r"DBDATASYNC_DUCKDB_ENGINE_SOURCE",
)]


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


def is_allowed(name):
    return any(p.match(name) for p in SC31B_ALLOWED)


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
                if not is_allowed(match.group(0)):
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
