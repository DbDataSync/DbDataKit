#!/usr/bin/env python3
"""Deletes the remote branches named in scripts/branches-to-delete.txt (intent and history: docs/branch-cleanup.md).

A dry run is the default; `--apply` deletes. It never deletes the default branch, a branch that is the head of an open pull request or the base of one,
or a branch that is not there any more. It needs the GitHub CLI (`gh`) with the right to delete branches; the workflow
.github/workflows/delete-listed-branches.yml runs it with the repository's own token, for sessions that have no such right.
Standard library only.
"""
import argparse
import os
import subprocess
import sys

LIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "branches-to-delete.txt")


def load(path):
    names = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            name = line.split("#", 1)[0].strip()
            if name:
                names.append(name)
    return list(dict.fromkeys(names))


def decide(names, default_branch, open_heads, open_bases, existing):
    """What to do for each name: ("delete" | "skip", reason). Pure, so a test can read the rules."""
    result = []
    for name in names:
        if name == default_branch:
            result.append((name, "skip", "the default branch"))
        elif name in open_heads:
            result.append((name, "skip", "the head of an open pull request"))
        elif name in open_bases:
            result.append((name, "skip", "the base of an open pull request"))
        elif name not in existing:
            result.append((name, "skip", "not there any more"))
        else:
            result.append((name, "delete", ""))
    return result


def gh(*args):
    done = subprocess.run(["gh", *args], capture_output=True, text=True)
    return done.returncode, done.stdout, done.stderr


def lines(code_out_err, what):
    code, out, err = code_out_err
    if code != 0:
        raise SystemExit(f"{what} failed: {err.strip() or out.strip()}")
    return [x for x in out.splitlines() if x.strip()]


def main(argv=None, runner=gh):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--apply", action="store_true", help="delete (the default is a dry run that only says what it would do)")
    parser.add_argument("--list", default=LIST, help="the file of branch names, one per line, # starts a comment")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"), help="owner/name (default: GITHUB_REPOSITORY, else the repository gh is in)")
    args = parser.parse_args(argv)

    repo = args.repo or lines(runner("repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"), "gh repo view")[0]
    names = load(args.list)
    default_branch = lines(runner("api", f"repos/{repo}", "--jq", ".default_branch"), "reading the repository")[0]
    open_heads = set(lines(runner("api", f"repos/{repo}/pulls?state=open&per_page=100", "--paginate", "--jq", ".[].head.ref"), "listing pull requests"))
    open_bases = set(lines(runner("api", f"repos/{repo}/pulls?state=open&per_page=100", "--paginate", "--jq", ".[].base.ref"), "listing pull requests"))
    existing = set(lines(runner("api", f"repos/{repo}/branches?per_page=100", "--paginate", "--jq", ".[].name"), "listing branches"))

    failures = 0
    for name, action, reason in decide(names, default_branch, open_heads, open_bases, existing):
        if action == "skip":
            print(f"skip    {name}: {reason}")
        elif not args.apply:
            print(f"would delete {name}")
        else:
            code, out, err = runner("api", "--method", "DELETE", f"repos/{repo}/git/refs/heads/{name}", "--silent")
            if code == 0:
                print(f"deleted {name}")
            else:
                failures += 1
                print(f"FAILED  {name}: {err.strip() or out.strip()}")
    if not args.apply:
        print("dry run: nothing deleted (add --apply)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
