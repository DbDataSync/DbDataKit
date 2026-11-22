#!/usr/bin/env python3
"""Which changes start a product's CI, and the release guard that follows from it.

A product's CI workflow (`.github/workflows/<product>-ci.yml`) does not start for a push or a pull request that changes only planning, documentation or
Markdown: its `push` and `pull_request` triggers carry the same `paths-ignore` list, PRODUCT_IGNORE below (a test in scripts/tests keeps the workflows and
this list equal). What a build reads from those files is checked without a build by scripts/check_doc_inputs.py, in Repository checks.

Such a commit on main then has no run of the product's CI, so a release of it cannot ask for "a green run of this exact commit". The guard asks instead:

  python3 -I scripts/ci_paths.py require-green --workflow .github/workflows/dbdatagateway-ci.yml --sha <commit> [--repo owner/name]

It passes when the commit has a successful run of that workflow, or when the nearest commit before it (first parents) that has one is followed only by
changes the workflow ignores, and the commit itself has a successful "Repository checks" run (the standalone checks of what the build reads). It needs
`git` (the history, fetch-depth 0) and `gh` (GH_TOKEN). Design: docs/ci-lessons.md#which-changes-start-which-workflow
"""
import argparse
import json
import os
import re
import subprocess
import sys

# The paths whose change alone does not start a product's CI. `**` crosses folders, `*` does not; `**.md` is every Markdown file, at the root too.
PRODUCT_IGNORE = [
    "planning/**",
    ".claude/**",
    "docs/**",
    "**.md",
    "DbDataKit/docs/**",
    "DbDataSync/docs/**",
    "DbDataBuild/docs/**",
    "DbDataGateway/docs/**",
]

# The product CI workflows that carry PRODUCT_IGNORE on both triggers.
PRODUCT_WORKFLOWS = [
    "dbdatabuild-ci.yml",
    "dbdatagateway-ci.yml",
    "dbdatakit-ci.yml",
    "dbdatarunner-ci.yml",
    "dbdatastore-ci.yml",
    "dbdatasync-ci.yml",
    "dbdataworkspace-ci.yml",
]

CHECKS_WORKFLOW = "Repository checks"
WALK_LIMIT = 200   # first-parent commits searched for the nearest green run


def glob_regex(pattern):
    """A GitHub Actions path pattern as a regular expression over a whole path: `**/` is any folders (or none), `**` anything, `*` and `?` stay within a folder."""
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif pattern[i] == "*":
            out.append("[^/]*")
            i += 1
        elif pattern[i] == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(pattern[i]))
            i += 1
    return re.compile("".join(out) + r"\Z")


def matches(patterns, path):
    return any(glob_regex(p).match(path) for p in patterns)


def would_run(paths=None, ignore=None, changed=()):
    """Whether GitHub starts a workflow for these changed files, given its `paths` (with `!` negations, the last match wins) or its `paths-ignore`."""
    changed = list(changed)
    if ignore is not None:
        return any(not matches(ignore, f) for f in changed)
    if paths is not None:
        def included(f):
            result = False
            for p in paths:
                if p.startswith("!"):
                    if glob_regex(p[1:]).match(f):
                        result = False
                elif glob_regex(p).match(f):
                    result = True
            return result
        return any(included(f) for f in changed)
    return True


def _unquote(value):
    """A YAML scalar as these workflows write it: quoted or plain, with an optional `# comment` after it."""
    value = value.strip()
    if value[:1] in ("'", '"'):
        end = value.find(value[0], 1)
        return value[1:end] if end > 0 else value[1:]
    return re.sub(r"\s+#.*$", "", value)


def workflow_name(text):
    for line in text.splitlines():
        if line.startswith("name:"):
            return _unquote(line[len("name:"):])
    raise ValueError("the workflow has no name")


def trigger_filter(text, event, key):
    """The list under `on: <event>: <key>:` (key is `paths` or `paths-ignore`), or None when the trigger has none. Reads the block layout these workflows use."""
    lines = text.splitlines()
    in_on = in_event = in_key = False
    event_indent = key_indent = child_indent = None
    items = None
    for line in lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            if in_on and not line.startswith("on:"):
                break
            in_on = line.startswith("on:")
            continue
        if not in_on:
            continue
        if in_key and indent > key_indent and stripped.startswith("- "):
            items.append(_unquote(stripped[2:]))
            continue
        in_key = False
        if event_indent is None or indent <= event_indent:
            in_event = stripped == f"{event}:"
            event_indent, child_indent = indent, None
            continue
        child_indent = indent if child_indent is None else child_indent
        if in_event and indent == child_indent and stripped == f"{key}:":
            in_key, key_indent, items = True, indent, []
    return items


def _git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def _green(repo, sha, name):
    out = subprocess.run(["gh", "api", f"repos/{repo}/actions/runs?head_sha={sha}&status=success&per_page=100",
                          "--jq", f'[.workflow_runs[] | select(.name == {json.dumps(name)})] | length'],
                         check=True, capture_output=True, text=True).stdout
    return int(out.strip() or "0") > 0


def find_green(sha, ignore, is_green, changed_between, parent, limit=WALK_LIMIT):
    """(commit, None) for the commit whose green run covers `sha`, or (None, reason). Walks first parents back from `sha` to the nearest commit with a green
    run; every file changed since it must be one the workflow ignores, or the run proves nothing about `sha`."""
    commit = sha
    for _ in range(limit):
        if is_green(commit):
            if commit == sha:
                return commit, None
            unignored = [f for f in changed_between(commit, sha) if not matches(ignore, f)]
            if unignored:
                return None, f"{commit} has the nearest green run, but {unignored[0]} changed since and the workflow does not ignore it"
            return commit, None
        if commit != sha:
            unignored = [f for f in changed_between(commit, sha) if not matches(ignore, f)]
            if unignored:
                return None, f"{unignored[0]} changed after the nearest green run and has not been tested (no successful run since {commit})"
        commit = parent(commit)
        if commit is None:
            return None, "no commit before it has a successful run"
    return None, f"no successful run in the {limit} commits before it"


def require_green(workflow_path, sha, repo):
    with open(workflow_path, encoding="utf-8") as f:
        text = f.read()
    name = workflow_name(text)
    ignore = trigger_filter(text, "push", "paths-ignore") or []

    def parent(c):
        try:
            return _git("rev-parse", "--verify", "--quiet", f"{c}^1").strip() or None
        except subprocess.CalledProcessError:
            return None

    def changed_between(a, b):
        return [line for line in _git("diff", "--name-only", a, b).splitlines() if line]

    found, reason = find_green(sha, ignore, lambda c: _green(repo, c, name), changed_between, parent)
    if found is None:
        print(f"::error::No successful {name} run covers {sha}: {reason}. Dispatch '{name}' on main, or pick a commit that has a run.")
        return 1
    if found == sha:
        print(f"{sha} has a successful {name} run.")
        return 0
    if not _green(repo, sha, CHECKS_WORKFLOW):
        print(f"::error::{sha} changes only what {name} ignores since {found} (green), but has no successful '{CHECKS_WORKFLOW}' run, which checks those files without a build. Wait for it or dispatch it.")
        return 1
    print(f"{sha} changes only what {name} ignores since {found}, which has a successful run; its '{CHECKS_WORKFLOW}' run is green.")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    green = sub.add_parser("require-green", help="fail unless a successful run of the workflow covers the commit")
    green.add_argument("--workflow", required=True)
    green.add_argument("--sha", required=True)
    green.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    args = parser.parse_args(argv)
    if not args.repo:
        parser.error("--repo (or GITHUB_REPOSITORY) is required")
    return require_green(args.workflow, args.sha, args.repo)


if __name__ == "__main__":
    sys.exit(main())
