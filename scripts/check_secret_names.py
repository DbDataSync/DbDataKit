#!/usr/bin/env python3
"""The source scan of plan 33 SC31c: no secret is named `dbdata:...` any more. Every secret of the suite is `dbdatakit:<service>:<kind>:<name>[:<part>]`
(plan 33 section 6b; `server`, `gateway`, `runner`, `sync`, `build`, `store`, `workspace`), the way every environment variable starts `DBDATAKIT_`. This
check fails, naming the file and the line, for any tracked text file that names a secret as `dbdata:<...>` or `secret:dbdata:<...>`, that is the old
root `dbdata:` (the default prefix of `secret list`, `dbdata:{Product}:` composed in code and the placeholder `dbdata:<product>` included), or the old name
as it shows in an environment variable (`DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_X`) or in a file of a credentials directory (`dbdata_sync_connection_x`), or
`SecretName.Root` set to `dbdata`, except:

  * the files that tell history or are the subject (`EXEMPT_FILES`): the release notes, the plan of the rename, the CI lessons, the records of the earlier rename
    DbDataProxy to DbDataGateway, the tests of the refusal of old environment variables, and this script, the script that renamed the names and their tests.

`{dbdata: 'app/1'}`, the message between an app and its shell, is not a secret and is not found (it has a space after the colon, or sits right after `{`).
Binary files are skipped. Tracked files come from `git ls-files`; a folder that is not a repository is walked.
Run from anywhere: python3 -I scripts/check_secret_names.py [root]
"""
import os
import re
import subprocess
import sys

#: `dbdata:` as the root of a secret name: not inside a word, not the key of a message, something follows it.
OLD_ROOT = re.compile(r"(?<![A-Za-z0-9_\-~{])dbdata:(?=\S)(?!['\"]app/)")
#: What an old name becomes in an environment variable and in a file of a credentials directory.
OLD_ENV = re.compile(r"(?<![A-Za-z0-9_])DBDATAKIT_SECRET_DBDATA_(?:SYNC|BUILD|GATEWAY|CONSOLE|PROXY|WORKSPACE|RUNNER|STORE|KIT)_")
OLD_FILE = re.compile(r"(?<![A-Za-z0-9_\-~])dbdata_(?:sync|build|gateway|console|proxy|workspace|runner|store)_")
OLD_CONSTANT = re.compile(r'\bRoot = "dbdata";')

# Files that name old secrets on purpose: path (relative, '/') -> why.
EXEMPT_FILES = {
    "docs/plans/RELEASE-NOTES-PENDING.md": "release notes: old and new names, on purpose",
    "docs/plans/33-fewer-services.md": "the plan of the rename: the old-name column",
    "docs/ci-lessons.md": "CI lessons name the secrets of the day they were learnt",
    "docs/release-notes/dbdatagateway-rename.md": "the release note of the rename DbDataProxy to DbDataGateway",
    "scripts/rename_proxy_to_gateway.py": "the script of the rename DbDataProxy to DbDataGateway",
    "scripts/tests/test_rename_proxy_to_gateway.py": "the test of that script",
    "scripts/rename_secret_names.py": "the script that renamed the secrets: it holds the patterns",
    "scripts/tests/test_rename_secret_names.py": "the test of that script",
    "scripts/check_secret_names.py": "this script holds the patterns",
    "scripts/tests/test_check_secret_names.py": "the test of this script",
    "Shared/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs": "the tests of the refusal of old environment variables: it names an old variable and the one it is refused for",
}


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


def check(root):
    """Returns the list of problems: `path:line: text ...`."""
    problems = []
    for path in sorted(tracked_files(root)):
        if path in EXEMPT_FILES:
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
            if "dbdata" not in line.lower():
                continue
            for pattern in (OLD_ROOT, OLD_ENV, OLD_FILE, OLD_CONSTANT):
                for match in pattern.finditer(line):
                    problems.append(f"{path}:{number}: {match.group(0)} is the old name of a secret; every secret is dbdatakit:<service>:<kind>:<name>[:<part>] (plan 33 section 6b)")
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "no secret named dbdata:")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
