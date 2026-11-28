# Running several workers on one machine

Who does what, and how work reaches `main`, is [planning/ROLES.md](../planning/ROLES.md) (the owner; sessions, which plan and start subagents but do no
product code work; workers, one subject each on `backup/<slug>-<n>`; the reviewer, which combines them into `work/<slug>` and merges one pull request at
a time). This page is the machine side of a session with several workers: worktrees, backups, build slots, pacing and the traps already met. The skill `agent-batches` (`.claude/skills/agent-batches/SKILL.md`) is the short checklist.

## Worktrees and branches

- Each worker works in its own git worktree on its task's branch `backup/<slug>-<n>` (`backup/installer-3`); the reviewer on `work/<slug>`; a session's
  own planning changes on `planning/<topic>`. A harness that names the branch otherwise is switched to the standard name before the first push. A branch
  starts from the current `main` and merges `main` whenever `main` moves. Nobody rewrites a pushed branch (no amend, squash, rebase or reset of commits
  already pushed).
- Commit messages start with the task id (`INSTALLER-3: ...`). Work-in-progress notes go in `planning/wip/<SLUG>/<SLUG>-<n>.md` on the task's branch
  (`planning/ROLES.md`).
- A finished worktree is removed (`git worktree remove -f -f .claude/worktrees/agent-<id>`); its branch stays until the `work/<slug>` pull request that
  carries it merges (`docs/branch-cleanup.md`).
- `python3 -I scripts/plan_status.py <NAME>` prints the plan `planning/plans/<NAME>.md` with the status of each task: merged, in progress, blocked or not
  started. It reads the local refs, so run `git fetch origin` first; with `--remote` it asks `origin` for the `backup/*` branches.
- `check_solution.py` and `check_docs_commands.py` also scan `.claude/worktrees`, so in the main checkout they can fail only because agent worktrees exist:
  run them from a clean path or ignore a complaint that names only a worktree path.

## Backups

The machine is temporary: work that exists only in a local branch is lost with it. A worker's task branch is itself a backup branch (`backup/<slug>-<n>`),
pushed at every stopping point; with its wip file it is the handover when a session or machine goes away. Other local work not yet on a pushed branch
is pushed to **backup branches** too:

- `python3 scripts/backup_branches.py push` pushes local `main` to `backup/main` and every local `work/*` branch that `origin/main` does not contain yet to `backup/<branch>`
  (for example `backup/work/delivery2`). Plain pushes, never forced: the branches only grow, so each push is a fast-forward. Run it before a long build, before a pause and at the end of a session. Pushes to `backup/*` start no CI run (workflows run on pushes to `main` only).
- **Agents never rewrite their branch** (no amend, squash, rebase or reset of commits already made): the backups are plain pushes. If a branch is rewritten anyway, the next push goes to
  `backup/<branch>-2` (then `-3`...) and the cleanup removes the old name once `main` has the same files (it compares content, not only history, so a squash or rebase is covered).
- Only **committed** work is covered: one commit per slice is why the rule exists. An agent's uncommitted files are not backed up; ask a long-running agent to commit work in progress to its branch.
- The workflow **Backup branch cleanup** (`.github/workflows/backup-branch-cleanup.yml`) runs `python3 scripts/backup_branches.py clean --apply` on every push to `main` (and hourly as a net): a `backup/*` branch is deleted
  **as soon as** its tip is contained in `origin/main`. Merging a branch with `git merge` (not a squash or cherry-pick) is what keeps its tip contained, so a merged task branch goes with the next push to main. When a `work/<slug>` pull request merges, the workflow **Delete merged branch** also deletes the `backup/<slug>-<n>` branches of the task ids its commits start with, when the pull request carried their tip (so a squash merge is covered too; `docs/branch-cleanup.md`).
  A backup main has not caught up with is never deleted. `--hours N` keeps a young backup as a margin if ever wanted. By hand: Actions tab, "Run workflow"
  (a dry run unless `apply` is true).
- To recover after a lost machine: `git fetch origin 'refs/heads/backup/*:refs/remotes/origin/backup/*'`, then merge or check out the backup branches (`backup/main` first).

## Resources (a 4-core machine with a small disk)

- **Builds go through build slots:** `scripts/build-slot.sh <command>` runs the command in one of two pools of slots: **dotnet** (`DBDATAKIT_BUILD_SLOTS`, default 2: dotnet, msbuild, built test executables and anything else) and **node** (`DBDATAKIT_NODE_SLOTS`, default 2: npm, npx, node, tsc, vitest, playwright, oxlint), chosen by the command (`DBDATAKIT_BUILD_POOL` overrides). Slot 1 of the dotnet pool is `/tmp/build.lock`, so `flock -o /tmp/build.lock <command>` still works and counts against it. A build is refused (exit 75) when `/` has less than `DBDATAKIT_BUILD_MIN_FREE_GB` (3) free. The cores, not the slots, are the limit on this 4-core machine: a load average far above 4 means more slots only slow every build. Raise the slots only with disk and cores to spare: each build leaves gigabytes of `bin`, `obj` and `node_modules` until it is cleaned. The rest of this item explains the `-o`:
  descriptor and holds the lock for about ten minutes after the build, and the queue starves. Also `dotnet build -p:UseSharedCompilation=false` and `MSBUILDDISABLENODEREUSE=1`.
- **Each build gets its share of the cores:** `scripts/build-slot.sh` adds `-m:<cores / slots>` (2 on this machine) to `dotnet build|test|publish|restore|pack` unless the caller gave `-m`, and sets `VITEST_MAX_WORKERS` for node commands; `DBDATAKIT_BUILD_CPUS` overrides it. Without the cap every `dotnet build` starts one MSBuild node per core and two builds at once oversubscribe the machine (load average 10 to 16 on 4 cores). Tune by the load average of `uptime`, not by the slot count.
- **Hard links keep a worktree small:** `scripts/build-slot.sh` makes MSBuild hard-link NuGet assemblies and copied content into `bin` (`DBDATAKIT_HARDLINKS=false` turns it off; a small test project had 72 of 76 files linked), and `scripts/web-install.sh` installs `Shared/web` with bun, which hard-links every file into its global cache (a worktree's `node_modules` then costs almost nothing instead of about 325 MB; the console build and its 270 vitest tests ran on it). CI keeps `npm ci`. Both need the repository and the caches on one volume.
- **Delete `bin`, `obj`, `node_modules`, `.duckdb` and `DbDataBuild/native` right after each step** (`find <dir> -type d \( -name bin -o -name obj -o -name node_modules \) -prune -exec rm -rf {} +`).
  Several products built at once filled the disk twice. Stop building when `df -h /` shows under 3 GB free.
- **`scripts/build-slot.sh` sets `DBDATAKIT_TEST_SKIP_JDBC=true` unless it is already set** (set it to `false` to build the JDBC projects). **Sync builds only with `DBDATAKIT_TEST_SKIP_JDBC=true`** (`docs/testing-environment.md`, "Building without JDBC"): the IKVM packages are about 4 GB.
- **Docker images** for the test engines are about 3 GB; prune them when no container runs (`docker image prune -a -f`), pull them again through the mirror when needed (`CLAUDE.md`).
- **Remove finished worktrees** and leave no untracked build output in them.
- The test host of an xunit v3 project may not start (`MSB1001`, handshake failures): run the built test executable (`-class`, `-method`).
- Playwright for the console only needs the console server: the recipe is the `docs/ci-lessons.md` row "run a console-only spec locally".

## Pacing against a usage limit (2026-11-15)

When the owner's plan has a usage window (for example a 5-hour limit), the session paces its workers against it:

- Run agents in the background and ask the owner for the usage reading now and then; measure the rate (a fresh agent used about 0.8% of a 5-hour window a minute; an agent with a very large context about twice that).
- Prefer a **fresh agent** with a complete brief over resuming one whose context is huge: it is cheaper per step. The brief carries everything (the rules, the decisions, the order of work, the push rule) because the agent starts cold.
- Schedule a **wind-down** message before the limit (leave about 10% for the wind-down and the report): the agent stops at a clean point, commits what passes, backs up the rest (`scripts/backup_branches.py push`), deletes its build output and reports. Schedule the **resume** just after the window resets.
- After each report: check CI, remove the worker's worktree, and take the worker's defaults and open questions to the owner (they reach the subject file as proposed decisions when the worker resolves its wip file; `planning/ROLES.md`).

## What went wrong, and the fix

| Trap | Fix |
| --- | --- |
| A worktree starts from an older commit than `main` | `git merge main` before starting; report the base commit |
| Two agents rewrite the same rows | A worker edits only its own subject file; two tasks that change the same code go to one worker or one after the other (`planning/ROLES.md`) |
| A verifier was started on an old branch and its all-clear covered an old tree | Start it from the current `main`; check `git log` of its branch against `main` before trusting the report |
| Specs written without a browser | Run them in CI before the pull request merges; every spec that signs in a new administrator sorts after `first-run` and `identity-home` |
| Reserved words (`exit`, `identity`) as column names | Only the engine legs find them; run the SQL Server and PostgreSQL legs of a new module before the first push when disk allows |
| A stop hook nags to push while CI runs | Answer with the run state; push when idle |
| Disk full | Prune docker images, remove finished worktrees, one build at a time |
