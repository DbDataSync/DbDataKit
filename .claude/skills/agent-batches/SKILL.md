---
name: agent-batches
description: >-
  Run several subagents on the DbDataKit monorepo without git, CI or machine-resource trouble, under the roles of planning/ROLES.md: one agent per
  subject in planning/, its own worktree and branch, work-in-progress notes in planning/wip/, small pull requests merged one at a time by the
  coordinator, builds serialised with build slots, backups of unpushed work. Use this whenever the user asks to run agents in parallel, "keep agents
  busy", work overnight, start more subagents, merge subject pull requests, or explains the work pattern ("it works well, document it").
---

# Running many agents on DbDataKit

Roles and the merge rules: `planning/ROLES.md`. Machine side (worktrees, backups, build slots, pacing, traps): `docs/agent-workflow.md`. This is the checklist.

**Roles.** The owner answers proposed decisions, orders and assigns subjects. One subject agent per subject (`planning/<SLUG>.md`), on its own branch,
editing only its subject file and the code and docs its tasks need. One coordinator merges pull requests one at a time, keeps `planning/README.md`,
carries "Needs from other subjects" across, and builds no features.

**Starting a subject agent.** Name the subject and the tasks in order; it reads `CLAUDE.md`, `planning/ROLES.md`, its subject file and
`docs/ci-lessons.md`, and audits each task against the code first. Rules in the brief: commit messages start with the task id; work-in-progress notes,
defaults and questions go in `planning/wip/<SLUG>/<TASK-ID>.md`, never in the subject file or a product doc; before the pull request, resolve each wip
item (finished work out of the task table, open questions as `<SLUG>-P<n>` proposals, needs as "Needs from other subjects") and delete the wip files;
frozen formats are never edited; new formats get a `docs/formats.json` entry and a `docs/ui-editors.md` row; shared files are edited by adding entries;
`scripts/check_*.py` pass; `git merge main` first if the worktree base is old.

**Machine rules (put them in every brief).**
- Every build or test: `scripts/build-slot.sh <command>` with `-p:UseSharedCompilation=false` and `MSBUILDDISABLENODEREUSE=1`.
- Delete `bin`, `obj`, `node_modules`, `.duckdb`, `DbDataBuild/native` right after each step; stop building under 3 GB free (`df -h /`).
- Sync only with `DBDATAKIT_TEST_SKIP_JDBC=true`; never restore IKVM.
- Prune unused docker images when disk is low; pull again through the mirror when engines are needed.

**Merging (coordinator).** One pull request at a time. Refuse it while any file exists under `planning/wip/` (`scripts/check_no_wip.py`). Check that
each proposal it removes from a subject file has a commit message `answered by the owner: ...` backed by what the owner actually said, and that no
agent default reached a product doc. With branch protection and the merge queue on, they prove "merges cleanly, CI green on the combined result";
until then merge `main` into the branch and wait for CI. Resolve only mechanical conflicts; send semantic ones back to the subject agent.

**Backups.** `python3 scripts/backup_branches.py push` pushes unpushed local work to `backup/<branch>`; the cleanup workflow deletes a backup once
`main` contains it. Agents never rewrite a pushed branch.

**Reporting to the owner.** Per merged pull request: what was built, tests with counts, what was not run, and the new proposed decisions (never decide
a design question silently; a default is a proposal until the owner answers).
