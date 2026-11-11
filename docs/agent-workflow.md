# Working with many agents

How a long session with several agents at once is run here without git, CI or machine-resource trouble. It is the pattern of the overnight run of
2026-11-11 (about 40 agent branches merged, a few hundred commits, one main). `CLAUDE.md` has the rules of the repository; this page is how one
person's session coordinates agents inside them. The skill `agent-batches` (`.claude/skills/agent-batches/SKILL.md`) is the short checklist.

**Owner decision (2026-11-11), until further notice:** finished work goes straight to `main`, with no pull request, and Windows and CI problems are
fixed afterwards by the CI agent. A pull request per batch (`CLAUDE.md`, "Making changes") is the rule again whenever the owner says so.

## Roles

| Role | Does | Never does |
| --- | --- | --- |
| **Coordinator** (the main conversation) | Writes the briefs, starts agents, merges finished branches into the local `main` of the main checkout, resolves conflicts, runs the repository checks, answers the owner, keeps the list of what is open | Builds big things itself, or edits a branch an agent still works on |
| **Slice agent** | One area, in its own git worktree on a local branch `work/<name>`; audits the plan rows against the code first, builds the open slices in dependency order, **one commit per slice** with the plan row's status set in the same commit | Pushes, opens a pull request, edits a frozen format, touches another agent's area |
| **CI agent** | The only agent that pushes. Pushes local `main` when no CI run is in progress, watches the run set, reruns infrastructure flakes, fixes real failures on its own branch (`work/ci-<n>`) with a `docs/ci-lessons.md` row each | Skips or disables a test to get green, force-pushes |
| **Verifier / integrator** (when needed) | Builds and tests a combination of branches that were written without a build, fixes the breakage, one commit per fix | Redesigns |

At most five to seven agents run at once. More is possible on paper; the machine decides (see Resources).

## The loop of the coordinator

1. **Brief an agent** (template below). Give it one area and name the areas the others hold.
2. **Wait.** Reports arrive as messages. Read the report for what was built, what was skipped and why, what was run with counts, what is unverified and the decisions
   it left open. Pass decisions to the owner; take defaults only for routine ones and say so ("default taken").
3. **Merge:** in the main checkout, `git fetch origin main`, `git merge origin/main`, then `git merge work/<name>`. Conflicts are almost always text in shared tables:
   - `docs/ui-editors.md` counts line (take the numbers `scripts/check_ui_editors.py` prints for the rows after the merge);
   - plan rows renumbered or rewritten on both sides (keep the side whose status describes what the code does);
   - `docs/ci-lessons.md`, `docs/ui-coverage.md` (keep both sides' rows);
   - real code (a Program/Host file wired by two agents): keep both, in the order that makes sense (for example an auth gate before an audit middleware), and ask the CI agent to watch it.
4. **Check:** `python3 -I scripts/check_*.py` (all of them). `check_solution.py` and `check_docs_commands.py` also scan `.claude/worktrees`, so they can fail
   only because agent worktrees exist: run them from a clean path or ignore a complaint that names only a worktree path.
5. **Remove the finished agent's worktree** (`git worktree remove -f -f .claude/worktrees/agent-<id>`); the branch stays.
6. **Start the next agent** from the list of open work, so slots are not idle.
7. **Back up** (`python3 scripts/backup_branches.py push`), then **push to main only when no run is in progress** (next sections).

## Pushing

A push to `main` starts a run set and **cancels** the one still running. So:

- `gh api "repos/<owner>/<repo>/actions/runs?branch=main&per_page=14" --jq '.workflow_runs[]|select(.status!="completed")|"\(.name) \(.status) \(.head_sha[0:8])"'`;
  push only when it prints nothing. Manually dispatched workflows (a suite dry run) have no concurrency group and are not cancelled by a push.
- Fast-forward only. If the push is refused, the CI agent pushed in between: `git fetch`, `git merge origin/main`, check again.
- One push per completed run set. The CI agent does it on its own cadence; the coordinator pushes when a stop hook demands it and nothing runs.
- A big merge that was never built together is pushed anyway **after** the checks pass; CI is its first real build, which is why the CI agent is told to watch that run set hardest.

## Backups

The machine is temporary: work that exists only in a local `main` or in an agent's local branch is lost with it. Pending work is therefore pushed to **backup branches**
while it waits for CI to be idle:

- `python3 scripts/backup_branches.py push` pushes local `main` to `backup/main` and every local `work/*` branch that `origin/main` does not contain yet to `backup/<branch>`
  (for example `backup/work/delivery2`). Plain pushes, never forced: the branches only grow, so each push is a fast-forward. **The coordinator runs it after every merge into the local
  main** (and the CI agent at the start of each cycle). Pushes to `backup/*` start no CI run (workflows run on pushes to `main` only).
- Only **committed** work is covered: one commit per slice is why the rule exists. An agent's uncommitted files are not backed up; ask a long-running agent to commit work in progress to its branch.
- The workflow **Backup branch cleanup** (`.github/workflows/backup-branch-cleanup.yml`, hourly) runs `python3 scripts/backup_branches.py clean --apply`: a `backup/*` branch is deleted
  when its tip is contained in `origin/main` **and** its tip commit is older than 24 hours. A backup main has not caught up with is never deleted. By hand: Actions tab, "Run workflow"
  (a dry run unless `apply` is true).
- To recover after a lost machine: `git fetch origin 'refs/heads/backup/*:refs/remotes/origin/backup/*'`, then merge or check out the backup branches (`backup/main` first).

## Resources (a 4-core machine with a small disk)

- **One build at a time across all agents:** `flock -o /tmp/build.lock <command>`. The `-o` matters: without it the Roslyn compile server a build starts inherits the lock's file
  descriptor and holds the lock for about ten minutes after the build, and the queue starves. Also `dotnet build -p:UseSharedCompilation=false` and `MSBUILDDISABLENODEREUSE=1`.
- **Delete `bin`, `obj`, `node_modules`, `.duckdb` and `DbDataBuild/native` right after each step** (`find <dir> -type d \( -name bin -o -name obj -o -name node_modules \) -prune -exec rm -rf {} +`).
  Several products built at once filled the disk twice. Stop building when `df -h /` shows under 3 GB free.
- **Sync builds only with `DBDATAKIT_TEST_SKIP_JDBC=true`** (`docs/testing-environment.md`, "Building without JDBC"): the IKVM packages are about 4 GB.
- **Docker images** for the test engines are about 3 GB; prune them when no container runs (`docker image prune -a -f`), pull them again through the mirror when needed (`CLAUDE.md`).
- **Remove finished worktrees** and leave no untracked build output in them.
- The test host of an xunit v3 project may not start (`MSB1001`, handshake failures): run the built test executable (`-class`, `-method`).
- Playwright for the console only needs the console server: the recipe is the `docs/ci-lessons.md` row "run a console-only spec locally".

## Brief template for an agent

> You work in the DbDataKit monorepo (read `CLAUDE.md`, `docs/ci-lessons.md`, the plan rows named, `docs/testing-environment.md` end section). Your worktree starts from `main`
> (if your base is older, `git merge main` first). **Goal:** the open slices of <plan> in dependency order; audit each against the code first (the plan text may be behind).
> **Rules:** one commit per slice on local branch `work/<name>`, message starts with the slice id, the plan row's status set in the same commit; do not push, no pull request;
> frozen formats are never edited; new formats are registered (`docs/formats.json`) with an editor row (`docs/ui-editors.md`); screens for new capabilities;
> `check_*.py` pass. **Other agents work on:** <areas>: stay out. **Build rules:** (the Resources list). **Report:** branch, commit ids, slices built and skipped (why), what was
> run with counts, what is unverified, decisions unsettled.

## What went wrong, and the fix

| Trap | Fix |
| --- | --- |
| A worktree starts from an older commit than local `main` | Tell agents to `git merge main` (or reset an empty branch) before starting; report the base commit |
| Two agents rewrite the same plan rows (renumbered slice ids) | Keep one owner per plan section; the merge keeps the side that matches the code |
| A verifier was started on an old branch and its all-clear covered an old tree | Start the verifier from the current local `main`; check `git log` of its branch against `main` before trusting the report |
| Specs written without a browser | The CI agent or a Playwright agent runs them; every spec that signs in a new administrator sorts after `first-run` and `identity-home` |
| Reserved words (`exit`, `identity`) as column names | Only the engine legs find them; run the SQL Server and PostgreSQL legs of a new module before the first push when disk allows |
| A stop hook nags to push while CI runs | Answer with the run state; push when idle |
| Disk full | Prune docker images, remove finished worktrees, one build at a time |
