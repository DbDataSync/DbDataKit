# Branch cleanup: intent and how to run it

## Why

On 2026-11-07 the remote had 127 branches. A review (read-only, nothing was changed) sorted them:

| Group | Count | Why it can go |
| --- | --- | --- |
| Merged into `main` | 51 (+2 merged since) | their commits are on `main` |
| Older than the history `main` has now | 71 | `main` was re-rooted from an earlier `main`. These were the pull-request branches of that earlier history (the branch `claude/wonderful-dirac-o0g74c` contained 71 of them; their tips are ancestors of `9d267895`, the earlier `main`'s last commit, which has the pull request merges up to #372). A tree comparison from that commit to the present `main` found every file the pull requests added still on `main` except three, which `main`'s own deliberate removals (the Q16 commits `f8f0fc95` and `168487b9`) or the earlier `main` itself explain. Not verified: the 247 modified files were not compared line by line |
| One obsolete note | 1 | `suite/j14-note` changed one row of the old plan 19 (since deleted) that `main` now words differently (J14 "waiting on J28") |
| `claude/wonderful-dirac-o0g74c` | 1 | held the only content not on `main`: the Studio plan and its lane I for plan 13a. Both are kept, unreviewed, in `docs/plans/needs-review/` before the branch goes |

The owner approved deleting all of them on 2026-11-07 and asked for the earlier history not to be kept (no tag was made). Sessions of the AI agent cannot delete remote branches (its git gateway refuses deletion and its GitHub tools have no such call), which is why this is a script and a workflow.

## How

- `scripts/branches-to-delete.txt` is the list (one name per line, `#` comments). Its last group is the branches of work that was squashed into a batch (see `CLAUDE.md`, "Making changes"): run the cleanup **after the batch pull request is merged and the pull requests of those branches are closed**.
- `python3 scripts/delete_branches.py` is a dry run; `--apply` deletes. It needs the GitHub CLI (`gh`) and the right to delete branches. It refuses the default branch, a branch with an open pull request (as head or as base), and a branch that is already gone.
- The workflow **Delete listed branches** (`.github/workflows/delete-listed-branches.yml`, Actions tab, "Run workflow") runs the same script with the repository's own token: dry run first (the default), then with `dry_run` set to `false`.
- The workflow **Delete merged branch** deletes the branch of a pull request when it merges, so the list does not grow again. Turning on the repository setting "Automatically delete head branches" does the same and is worth doing.

To delete another branch later, add it to the list (with a comment saying why) and run the workflow; do not push a copy of the old content under a new name.

## Backup branches

`backup/*` branches hold pending work while it waits for CI (`docs/agent-workflow.md`, "Backups"). They clean themselves: the hourly workflow **Backup branch cleanup** deletes a backup whose tip is contained in `main` and whose tip commit is older than 24 hours (`scripts/backup_branches.py clean`). Do not list them in `scripts/branches-to-delete.txt`.
