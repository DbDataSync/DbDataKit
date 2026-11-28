#!/usr/bin/env python3
"""Backup branches: pending work is pushed to `backup/<name>` branches so a lost machine loses nothing, and the backups that `main` has caught up with are deleted.

  push    push local `main` and every local `work/*` branch whose tip is not on origin/main yet to `backup/main` and `backup/<branch>`
          (plain pushes, never forced). When a branch was rewritten (squashed, rebased) and the plain push is refused, it goes to the next free
          `backup/<branch>-N` instead. Run it after every merge into the local main.
  clean   delete the remote `backup/*` branches that origin/main has caught up with: the tip is contained in origin/main, or every file the backup changed since
          the merge-base is identical in origin/main (a squashed or rebased branch) (default: at once; --hours N keeps one younger than N hours as a margin). Dry run unless --apply. The workflow `backup-branch-cleanup.yml` runs it with --apply on every push to main and hourly.
  tasks   delete the task branches `backup/<slug>-<n>` of a merged pull request: every task id `<SLUG>-<n>:` that starts a commit message in
          BASE..HEAD names one, and it is deleted when HEAD (the pull request's head) or origin/main carries its tip; one with commits the pull request
          did not carry is kept and listed. Dry run unless --apply. The workflow `delete-merged-branch.yml` runs it when a `work/<slug>` pull request merges.

In clean and tasks, a delete that fails on a branch the remote no longer has (another workflow deleted it first) is reported as already deleted, not
as a failure; whether it is gone is asked of the remote after the failure, whatever the error said.
Only committed work is backed up: an agent's uncommitted files are not. Intent: docs/agent-workflow.md ("Backups").
Run from a clone of the repository: python3 scripts/backup_branches.py push | clean [--apply] [--hours N] | tasks --base BASE --head HEAD [--apply] [--remote origin]
"""
import argparse
import re
import subprocess
import sys
import time

PREFIX = "backup/"
# A task id at the start of a commit message: `INSTALLER-3: ...` or `SEO-3: ...` (a slug is capitals and digits starting with a letter,
# scripts/check_plans.py).
TASK_ID = re.compile(r"^([A-Z][A-Z0-9]*)-(\d+):")


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
    # `backup/main` is already a backup (of a local main that holds merges not yet pushed): it is pushed under its own name.
    wanted = ["main", "backup/main"] + [b for b in local_branches() if b.startswith("work/")]
    for branch in wanted:
        if not ref_exists(branch):
            continue
        if branch != "main" and ref_exists(main_ref) and is_ancestor(branch, main_ref):
            continue  # already on main: nothing to back up
        if branch in ("main", "backup/main") and ref_exists(main_ref) and is_ancestor(branch, main_ref):
            continue
        target = branch if branch.startswith(PREFIX) else PREFIX + branch
        if dry_run:
            pushed.append((branch, target, "would push"))
            continue
        result = git("push", remote, f"{branch}:refs/heads/{target}", check=False)
        if result.returncode == 0:
            pushed.append((branch, target, "pushed"))
            continue
        if "rejected" in result.stderr or "fast-forward" in result.stderr:
            alt = next_free_name(remote, branch, target)
            retry = git("push", remote, f"{branch}:refs/heads/{alt}", check=False)
            if retry.returncode == 0:
                pushed.append((branch, alt, f"pushed (the branch was rewritten: {target} is out of step)"))
                continue
        lines = result.stderr.strip().splitlines()
        pushed.append((branch, target, "FAILED: " + (lines[-1] if lines else "")))
    return pushed


def next_free_name(remote, branch, target):
    """The first `<target>-N` (N from 2) that does not exist on the remote, or the highest existing one when the branch is a fast-forward of it."""
    out = git("ls-remote", "--heads", remote, f"refs/heads/{target}-*", check=False).stdout
    existing = {}
    for line in out.splitlines():
        sha, ref = line.split()
        suffix = ref.rsplit("-", 1)[-1]
        if suffix.isdigit():
            existing[int(suffix)] = sha
    if existing:
        top = max(existing)
        if ref_exists(existing[top]) and is_ancestor(existing[top], branch):
            return f"{target}-{top}"
        return f"{target}-{top + 1}"
    return f"{target}-2"


def content_in_main(sha, main_ref):
    """True when every file the backup changed since the merge-base is identical in main (a squashed or rebased branch whose work main has)."""
    base = git("merge-base", main_ref, sha, check=False).stdout.strip()
    if not base:
        return False
    files = git("diff", "--name-only", base, sha, check=False).stdout.split("\n")
    files = [f for f in files if f]
    if not files:
        return True
    return git("diff", "--quiet", main_ref, sha, "--", *files, check=False).returncode == 0


def remote_backups(remote):
    out = git("for-each-ref", "--format=%(refname:short) %(objectname) %(committerdate:unix)", f"refs/remotes/{remote}/{PREFIX}").stdout
    rows = []
    for line in out.splitlines():
        name, sha, when = line.split()
        rows.append((name[len(remote) + 1:], sha, int(when)))
    return rows


def delete_remote(remote, name):
    """Delete a remote branch and say how it went. A branch that is already gone counts as deleted: on a merge of a `work/<slug>` pull request,
    `delete-merged-branch.yml` (tasks) and `backup-branch-cleanup.yml` (clean) both delete its task branches, and the second finds them gone.
    After a failed delete the remote is asked whether the branch is still there, because the error's wording for a branch that is gone differs
    between servers and versions (`remote ref does not exist`, `cannot lock ref ...: unable to resolve reference`); only a branch the remote
    still has, or a remote that cannot be asked, is a failure, and it names the delete's error."""
    result = git("push", remote, "--delete", name, check=False)
    if result.returncode == 0:
        return "deleted"
    error = result.stderr.strip()
    listed = git("ls-remote", "--heads", remote, f"refs/heads/{name}", check=False)
    if listed.returncode != 0:
        return f"FAILED: {error} (and asking {remote} whether the branch is still there failed: {listed.stderr.strip()})"
    if not listed.stdout.strip():
        return "already deleted"
    return "FAILED: " + error


def clean(remote, hours, apply, now=None):
    now = time.time() if now is None else now
    main_ref = f"{remote}/main"
    if not ref_exists(main_ref):
        raise SystemExit(f"{main_ref} does not exist: fetch first")
    deleted = []
    for name, sha, when in remote_backups(remote):
        age_hours = (now - when) / 3600
        if not (is_ancestor(sha, main_ref) or content_in_main(sha, main_ref)):
            deleted.append((name, f"kept: not on main yet ({age_hours:.0f} h old)"))
            continue
        if age_hours < hours:
            deleted.append((name, f"kept: on main but touched {age_hours:.0f} h ago (under {hours} h)"))
            continue
        if apply:
            deleted.append((name, delete_remote(remote, name)))
        else:
            deleted.append((name, f"would delete (on main, {age_hours:.0f} h old)"))
    return deleted


def task_ids(messages):
    """The task ids (`INSTALLER-3`) that start the given commit messages, in order of first appearance, each once."""
    found = []
    for message in messages:
        match = TASK_ID.match(message.lstrip())
        if match:
            ident = f"{match.group(1)}-{match.group(2)}"
            if ident not in found:
                found.append(ident)
    return found


def task_branch(task_id):
    """The branch of a task: `INSTALLER-3` -> `backup/installer-3`."""
    return PREFIX + task_id.lower()


def commit_messages(base, head):
    out = git("log", "--format=%B%x00", f"{base}..{head}").stdout
    return [m.strip() for m in out.split("\0") if m.strip()]


def clean_tasks(remote, base, head, apply):
    """Delete the `backup/<slug>-<n>` branches of the task ids that start the commit messages of base..head, when head or main carries their tip."""
    main_ref = f"{remote}/main"
    rows = []
    for ident in task_ids(commit_messages(base, head)):
        name = task_branch(ident)
        ref = f"refs/remotes/{remote}/{name}"
        if not ref_exists(ref):
            continue  # no such branch (already deleted, or the task was never pushed on its own branch)
        sha = git("rev-parse", ref).stdout.strip()
        carried = is_ancestor(sha, head) or (ref_exists(main_ref) and (is_ancestor(sha, main_ref) or content_in_main(sha, main_ref)))
        if not carried:
            rows.append((name, "kept: it has commits the pull request did not carry"))
            continue
        if apply:
            rows.append((name, delete_remote(remote, name)))
        else:
            rows.append((name, f"would delete ({ident} is in the pull request)"))
    return rows


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", choices=["push", "clean", "tasks"])
    parser.add_argument("--remote", default="origin")
    parser.add_argument("--hours", type=float, default=0)
    parser.add_argument("--apply", action="store_true", help="clean: delete (default is a dry run)")
    parser.add_argument("--dry-run", action="store_true", help="push: list only")
    parser.add_argument("--base", help="tasks: the pull request's base commit")
    parser.add_argument("--head", help="tasks: the pull request's head commit (fetched)")
    args = parser.parse_args(argv)
    if args.mode == "push":
        rows = push(args.remote, args.dry_run)
        for branch, target, state in rows:
            print(f"{branch} -> {target}: {state}")
        if not rows:
            print("nothing to back up")
        return 1 if any(state.startswith("FAILED") for _, _, state in rows) else 0
    git("fetch", "--quiet", args.remote, "main", f"+refs/heads/{PREFIX}*:refs/remotes/{args.remote}/{PREFIX}*", check=False)
    if args.mode == "tasks":
        if not args.base or not args.head:
            parser.error("tasks needs --base and --head")
        rows = clean_tasks(args.remote, args.base, args.head, args.apply)
        for name, state in rows:
            print(f"{name}: {state}")
        if not rows:
            print("no task branches named by the pull request")
        return 1 if any(state.startswith("FAILED") for _, state in rows) else 0
    rows = clean(args.remote, args.hours, args.apply)
    for name, state in rows:
        print(f"{name}: {state}")
    if not rows:
        print("no backup branches")
    return 1 if any(state.startswith("FAILED") for _, state in rows) else 0


if __name__ == "__main__":
    sys.exit(main())
