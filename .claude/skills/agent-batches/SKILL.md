---
name: agent-batches
description: >-
  Run several subagents on the DbDataKit monorepo without git, CI or machine-resource trouble, under planning/ROLES.md: the owner decides, a session
  plans and starts workers (one subject each, on backup/<slug>-<n>, with a wip file in planning/wip/) and the reviewer (combines ready task branches
  into work/<slug> and merges one pull request at a time), builds serialised with build slots, backups of unpushed work. Use this whenever the user
  asks to run agents in parallel, "keep agents busy", work overnight, start more subagents, merge subject work, or explains the work pattern ("it works
  well, document it").
---

# Running many workers on DbDataKit

Roles, branches and the merge rules: `planning/ROLES.md`. Machine side (worktrees, backups, build slots, pacing, traps): `docs/agent-workflow.md`. The
subagents: `.claude/agents/worker.md` and `.claude/agents/reviewer.md`. This is the checklist.

**Roles.** The owner answers proposed decisions, sets the order, status and assignment of subjects, and decides concurrency (no two sessions running
workers on one subject, no two reviewers at once). A session talks with the owner, plans, starts workers and reviewers and brings back their results;
it does no product code work (no code, no fixing tests, no merge conflicts). A worker builds one subject's tasks and never decides. The reviewer
combines and merges and builds no features.

**Starting a worker.** Use the `worker` subagent. Name the subject, the task ids in order and what the owner said about them. One task per branch
`backup/<slug>-<n>`; several workers on one subject only on tasks that do not change the same code (otherwise one after the other, or one worker for
both). Resuming after a lost session or machine: start a worker on the existing `backup/<slug>-<n>`; its wip file says where it stopped.

**Machine rules (put them in every brief).**
- Every build or test: `scripts/build-slot.sh <command>` with `-p:UseSharedCompilation=false` and `MSBUILDDISABLENODEREUSE=1`.
- Delete `bin`, `obj`, `node_modules`, `.duckdb`, `DbDataBuild/native` right after each step; stop building under 3 GB free (`df -h /`).
- Sync only with `DBDATAKIT_TEST_SKIP_JDBC=true`; never restore IKVM.
- Prune unused docker images when disk is low; pull again through the mirror when engines are needed.

**Merging.** Only when the owner says so (until branch protection and the merge queue are on), start the `reviewer` subagent with the subject and its
ready branches (a branch is ready when its head has no wip file). It merges them into `work/<slug>` one at a time, building and testing after each, then
merges the pull request into `main`, one pull request at a time, after its checklist: no wip, CI green on the combined result, consistency across
subjects and docs, every removed proposal backed by a recorded owner answer, every removed design text saying where it went, shared files only
appended, the index changed only on the owner's word. Mechanical conflicts it resolves; a design conflict comes back to you for the owner.

**Answers.** When the owner answers a proposal in this session for another subject, record the exact words under the proposal
(`Answer from the owner, <date>: ...`) on a `planning/<topic>` branch, or ask the reviewer to; the next worker for that subject acts on it.

**Backups.** Workers push their `backup/<slug>-<n>` at every stopping point; `python3 scripts/backup_branches.py push` pushes other unpushed local work.
The cleanup deletes a backup once `main` contains it, and a task's branch when the `work/<slug>` pull request naming the task merges. Nobody rewrites a
pushed branch.

**Reporting to the owner.** Per merged pull request: what was built, tests with counts, what was not run, and the new proposed decisions and defaults
(never decide a design question silently; a default is a proposal until the owner answers).
