#!/usr/bin/env python3
"""Backup branches: pending work is pushed to `backup/<name>` branches so a lost machine loses nothing, and the backups that `main` has caught up with are deleted.

  push    push local `main` and every local `work/*` branch whose tip is not on origin/main yet to `backup/main` and `backup/<branch>`
          (plain pushes, never forced; the branches only grow, so each push is a fast-forward). Run it after every merge into the local main.
  clean   delete the remote `backup/*` branches whose tip is contained in origin/main (default: at once; --hours N keeps one that is younger than N hours
          as a safety margin). Dry run unless --apply. The workflow `backup-branch-cleanup.yml` runs it with --apply on every push to main and hourly.

Only committed work is backed up: an agent's uncommitted files are not. Intent: docs/agent-workflow.md ("Backups").
Run from a clone of the repository: python3 scripts/backup_branches.py push | clean [--apply] [--hours N] [--remote origin]
"""
import argparse
import subprocess
import sys
import time

PREFIX = "backup/"


def git(*args, check=True):
    result = subprocess.run(["git", *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise SystemExit(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def is_ancestor(commit, of):
    return git("merge-base", "--is-ancestor", commit, of, check=False).returncode == 0


def ref_exists(ref):
    return git("rev-parse", "--verify", "--quiet", ref, check=False).returncode == 0


def local_branches():
    out = git("for-each-ref", "--format=%(refname:short)", "refs/heads/").stdout
    return [b for b in out.split() if b]


def push(remote, dry_run=False):
    main_ref = f"{remote}/main"
    pushed = []
    wanted = ["main"] + [b for b in local_branches() if b.startswith("work/")]
    for branch in wanted:
        if not ref_exists(branch):
            continue
        if branch != "main" and ref_exists(main_ref) and is_ancestor(branch, main_ref):
            continue  # already on main: nothing to back up
        if branch == "main" and ref_exists(main_ref) and is_ancestor("main", main_ref):
            continue
        target = PREFIX + branch
        if dry_run:
            pushed.append((branch, target, "would push"))
            continue
        result = git("push", remote, f"{branch}:refs/heads/{target}", check=False)
        if result.returncode == 0:
            pushed.append((branch, target, "pushed"))
        else:
            pushed.append((branch, target, "FAILED: " + result.stderr.strip().splitlines()[-1] if result.stderr.strip() else "FAILED"))
    return pushed


def remote_backups(remote):
    out = git("for-each-ref", "--format=%(refname:short) %(objectname) %(committerdate:unix)", f"refs/remotes/{remote}/{PREFIX}").stdout
    rows = []
    for line in out.splitlines():
        name, sha, when = line.split()
        rows.append((name[len(remote) + 1:], sha, int(when)))
    return rows


def clean(remote, hours, apply, now=None):
    now = time.time() if now is None else now
    main_ref = f"{remote}/main"
    if not ref_exists(main_ref):
        raise SystemExit(f"{main_ref} does not exist: fetch first")
    deleted = []
    for name, sha, when in remote_backups(remote):
        age_hours = (now - when) / 3600
        if not is_ancestor(sha, main_ref):
            deleted.append((name, f"kept: not on main yet ({age_hours:.0f} h old)"))
            continue
        if age_hours < hours:
            deleted.append((name, f"kept: on main but touched {age_hours:.0f} h ago (under {hours} h)"))
            continue
        if apply:
            result = git("push", remote, "--delete", name, check=False)
            deleted.append((name, "deleted" if result.returncode == 0 else "FAILED: " + result.stderr.strip()))
        else:
            deleted.append((name, f"would delete (on main, {age_hours:.0f} h old)"))
    return deleted


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=["push", "clean"])
    parser.add_argument("--remote", default="origin")
    parser.add_argument("--hours", type=float, default=0)
    parser.add_argument("--apply", action="store_true", help="clean: delete (default is a dry run)")
    parser.add_argument("--dry-run", action="store_true", help="push: list only")
    args = parser.parse_args(argv)
    if args.mode == "push":
        rows = push(args.remote, args.dry_run)
        for branch, target, state in rows:
            print(f"{branch} -> {target}: {state}")
        if not rows:
            print("nothing to back up")
        return 1 if any(state.startswith("FAILED") for _, _, state in rows) else 0
    git("fetch", "--quiet", args.remote, "main", f"+refs/heads/{PREFIX}*:refs/remotes/{args.remote}/{PREFIX}*", check=False)
    rows = clean(args.remote, args.hours, args.apply)
    for name, state in rows:
        print(f"{name}: {state}")
    if not rows:
        print("no backup branches")
    return 1 if any(state.startswith("FAILED") for _, state in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
