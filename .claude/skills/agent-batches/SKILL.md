---
name: agent-batches
description: >-
  Coordinate several subagents on the DbDataKit monorepo without git, CI or machine-resource trouble: one worktree and branch per agent,
  one commit per plan slice, the coordinator merges into local main, a single CI agent pushes when no run is in progress, builds are
  serialised with a lock. Use this whenever the user asks to run agents in parallel, "keep agents busy", work overnight, start more
  subagents, merge agent branches, push the batch, or explains the work pattern ("it works well, document it"). Also use it when a stop
  hook asks to push while CI is running.
---

# Running many agents on DbDataKit

Full explanation: `docs/agent-workflow.md`. This is the checklist.

**Roles.** The coordinator (you) briefs, merges and answers the owner. Slice agents work in their own worktree on a local branch `work/<name>`.
One CI agent is the only one that pushes and watches CI. A verifier builds combinations that were written without a build.

**Starting an agent.** One area per agent, named areas the others hold, a goal in dependency order, "audit each row against the code first".
Rules in the brief: one commit per slice with the plan row status in the same commit; no push, no pull request; frozen formats are never edited;
new formats get a `docs/formats.json` entry and a `docs/ui-editors.md` row; screens for new capabilities; `scripts/check_*.py` pass; `git merge main`
first if the worktree base is old. Use the brief template in `docs/agent-workflow.md`. Keep 5 to 7 agents running; start the next as one reports.

**Machine rules (put them in every brief).**
- Every build or test: `scripts/build-slot.sh <command>` (two dotnet slots and two node slots, chosen by the command; refuses under 3 GB free; `flock -o /tmp/build.lock <command>` is dotnet slot 1 and still works) with `-p:UseSharedCompilation=false` and `MSBUILDDISABLENODEREUSE=1` (without `-o` the compiler server holds the lock for ten minutes).
- Delete `bin`, `obj`, `node_modules`, `.duckdb`, `DbDataBuild/native` right after each step; stop building under 3 GB free (`df -h /`).
- Sync only with `DBDATAKIT_TEST_SKIP_JDBC=true`; never restore IKVM.
- Prune unused docker images (`docker image prune -a -f`) when disk is low; pull again through the mirror when engines are needed.

**Merging a finished branch** (main checkout only, you are the only writer there):
1. `git fetch -q origin main && git merge origin/main`, then `git merge work/<name>`.
2. Conflicts: `docs/ui-editors.md` counts line (take the numbers `check_ui_editors.py` prints), plan rows (keep the side that matches the code), `ci-lessons.md` / `ui-coverage.md` (keep both). In real code keep both sides in a sensible order and tell the CI agent.
3. Run every `python3 -I scripts/check_*.py` (`check_solution.py` / `check_docs_commands.py` can fail only because worktrees exist under `.claude/worktrees`).
4. `git worktree remove -f -f .claude/worktrees/agent-<id>` (the branch stays), then start the next agent.

**Backups.** After every merge into the local main run `python3 scripts/backup_branches.py push` (local main and unmerged `work/*` branches go to `backup/<name>`; plain pushes, no CI run). The workflow `backup-branch-cleanup.yml` deletes a backup as soon as main contains its tip (on every push to main, hourly as a net); merge agent branches with `git merge`, not squash or cherry-pick, so their tips stay contained. Only committed work is covered, and agents must never rewrite their branch (no amend/squash/rebase/reset): a rewritten branch's next backup goes to `backup/<branch>-2`, and cleanup compares content as well as history. Recovery: `git fetch origin 'refs/heads/backup/*:refs/remotes/origin/backup/*'`.

**Pushing.** A push cancels the run set in progress. Check first:
`gh api "repos/DbDataSync/DbDataKit/actions/runs?branch=main&per_page=14" --jq '.workflow_runs[]|select(.status!="completed")|"\(.name) \(.status) \(.head_sha[0:8])"'`
and push only when it prints nothing; fast-forward only; if rejected, `git fetch`, merge `origin/main`, recheck. Tell the CI agent what was pushed and which parts were never built
together. When a stop hook asks to push while runs are in progress, answer with the run state and wait.

**Reporting to the owner.** For each merged branch: what was built, tests with counts, what was not run, caveats, decisions for the owner (never decide a design question silently;
routine defaults are fine and are named as such). Say plainly what is unverified.

**Decisions of the owner that bind this pattern (2026-11-11).** Finished work goes to `main` without pull requests; handover rows (retiring old screens) wait for the owner's go after a
snapshot-release test; Windows and CI problems are the CI agent's.
