# Branch cleanup: intent and how to run it

## Why

On 2026-11-07 the remote had 127 branches. A review (read-only, nothing was changed) sorted them:

| Group | Count | Why it can go |
| --- | --- | --- |
| Merged into `main` | 51 (+2 merged since) | their commits are on `main` |
| Older than the history `main` has now | 71 | `main` was re-rooted from an earlier `main`. These were the pull-request branches of that earlier history (the branch `claude/wonderful-dirac-o0g74c` contained 71 of them; their tips are ancestors of `9d267895`, the earlier `main`'s last commit, which has the pull request merges up to #372). A tree comparison from that commit to the present `main` found every file the pull requests added still on `main` except three, which `main`'s own deliberate removals (the Q16 commits `f8f0fc95` and `168487b9`) or the earlier `main` itself explain. Not verified: the 247 modified files were not compared line by line |
| One obsolete note | 1 | `suite/j14-note` changed one row of the old plan 19 (since deleted) that `main` now words differently (J14 "waiting on J28") |
| `claude/wonderful-dirac-o0g74c` | 1 | held the only content not on `main`: the Studio plan and its lane I for plan 13a. Both were kept, unreviewed, in the old plans folder; their certificate design is the subject CERTIFICATES, and the owner dropped the rename (2026-11-17) |

The owner approved deleting all of them on 2026-11-07 and asked for the earlier history not to be kept (no tag was made). Sessions of the AI agent cannot delete remote branches (its git gateway refuses deletion and its GitHub tools have no such call), which is why this is a script and a workflow.

## How

- `scripts/branches-to-delete.txt` is the list (one name per line, `#` comments). Its last group is the branches of work that was squashed into a batch (see `CLAUDE.md`, "Making changes"): run the cleanup **after the batch pull request is merged and the pull requests of those branches are closed**.
- `python3 scripts/delete_branches.py` is a dry run; `--apply` deletes. It needs the GitHub CLI (`gh`) and the right to delete branches. It refuses the default branch, a branch with an open pull request (as head or as base), and a branch that is already gone.
- The workflow **Delete listed branches** (`.github/workflows/delete-listed-branches.yml`, Actions tab, "Run workflow") runs the same script with the repository's own token: dry run first (the default), then with `dry_run` set to `false`.
- The workflow **Delete merged branch** deletes the branch of a pull request when it merges, so the list does not grow again. Turning on the repository setting "Automatically delete head branches" does the same and is worth doing.

To delete another branch later, add it to the list (with a comment saying why) and run the workflow; do not push a copy of the old content under a new name.

## Backup branches

`backup/*` branches hold pending work while it waits for CI (`docs/agent-workflow.md`, "Backups"). They clean themselves: the workflow **Backup branch cleanup** deletes a backup as soon as its tip is contained in `main`, on every push to `main` and hourly (`scripts/backup_branches.py clean`). Do not list them in `scripts/branches-to-delete.txt`.

## Task branches

A worker builds a task on `backup/<slug>-<n>` (`backup/installer-3` for the task `INSTALLER-3`), and the reviewer merges the ready task branches of a
subject into `work/<slug>`, whose pull request goes into `main` (`planning/ROLES.md`, "Branches"). Two things delete a task branch:

- the **Backup branch cleanup** above, once `main` contains its tip (the pull request was merged with a merge commit) or every file it changed;
- the job `task-branches` of the workflow **Delete merged branch**, when a pull request from a `work/*` branch merges: it reads the commits of the pull
  request (from its base to its head), takes each task id that starts a commit message (`INSTALLER-3: ...`), and deletes `backup/installer-3` when the
  pull request's head or `main` carries that branch's tip. This covers a squash merge, after which `main` does not contain the tips. A task branch with
  commits the pull request did not carry (its worker went on after the combine) is kept and listed in the job's log; it goes later, with the pull
  request that carries the rest, or by the cleanup above.

When a `work/<slug>` pull request merges, both of them run and can delete the same task branches; whichever comes second finds them gone.
A branch the remote no longer has is reported as `already deleted` and does not fail the job (in both workflows, and for the head branch the
workflow **Delete merged branch** deletes); any other failure to delete a branch, such as a refused push, still fails it.

By hand, from a clone: `python3 scripts/backup_branches.py tasks --base <base commit> --head <head commit>` is a dry run; `--apply` deletes. `work/<slug>`
itself goes with its pull request, like any merged branch.
