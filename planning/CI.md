# CI: targeted checks on a pull request, everything once a week

**Draft.** Being worked out with the owner.

## Goal

A pull request runs only what its change can break. It always runs the fast repository checks. It runs the build and tests of the projects the change
touches, and of the projects that depend on them, found from the projects' references. Everything else runs once a week, and on demand before a stable
release: every suite on every engine (including the previous release of each state engine), Windows and Linux, the browser stories, the installers, and
the conformance suites and their test servers. A stable release waits for a green full run of the commit it ships.

CI covers the builds, the test runs and the releases in GitHub Actions; which tests exist and what they cover is TESTING's.

A flaky test is tracked, not just rerun. It is on a list until it is fixed, and the list stays short.

Example: Ana changes a method in `DbDataKit.Connections.Http`.

- **On her pull request:** the repository checks run, plus the build and tests of that library and of the projects that reference it: the Runner kit,
  the console, the Gateway. Sync's engine suites don't run, because they don't reference it, so no SQL Server, Oracle or MySQL container starts.
- **On Sunday:** the weekly full run covers everything.

## What exists today

- **27 workflows.** One CI workflow per product (Build, Gateway, the console, Runner, Store, Sync, Workspace), plus Shared, `dist` (installers),
  `dist-msi`, the repository checks and the planning checks, each running on pull requests and pushes to `main`. There are also release workflows and
  branch cleanup.
- **Change detection by hand-kept path lists.** `_changes.yml` skips a product's jobs when no path in its list changed. The lists are wide: Sync's list
  includes all of `Shared/`, `DbDataKit/` and `testenv/`. So a change to any shared library starts Sync's jobs, with their engine containers. A list
  that is too narrow misses a change, as when a Sync source change didn't start the shared Sync driver tests (fixed with #478).
- **Slow jobs held back by a label.** `_changes.yml` has a `slow` output that is true after a merge, on request, and on a pull request only with the
  `ci:full` label. Only Build uses it.
- **One scheduled full run:** Build's conformance suite (`dbdatabuild-conformance-full`, nightly).
- **Windows legs** run on pull requests in seven workflows: the Gateway, Runner, Sync, Shared, `dist`, and two in `dist-msi`.
- **Flakes:** recorded in `docs/ci-lessons.md` and rerun by the reviewer; `docs/ci-flakes.md` starts the tracked list.

## Three tiers

**Tier 1: every pull request, always.** Fast checks that need no build of the products:

- the repository checks (`scripts/check_*.py` and their tests) and the planning checks (`check_plans`, `check_no_wip`);
- formatting and lint of what changed.

These run in a few minutes. A pull request that changes only docs or planning runs this tier and nothing else.

**Tier 2: a pull request, for what it touches.** The projects to build and test come from the change, not from hand-kept lists:

- **.NET:** the projects whose files changed, plus every project that references them, directly or through others, found from the `.csproj` references
  in `DbDataKit.slnx`.
- **Web:** the packages under `Shared/web` whose files changed, plus the packages that depend on them, found from each `package.json`.
- **What runs for those projects:**
  - the build, with warnings as errors;
  - the unit tests on Linux, with state on SQLite;
  - the vitest suites of the web packages;
  - the engine tests (SQL Server, PostgreSQL) of the projects whose own files changed, not of the projects that only depend on them.
- **Windows:** runs on a pull request only when a changed project is Windows-specific (Windows hosting, the MSI, the 32-bit ODBC worker).
- **Browser stories:** run when a web package or the console changed.
- **A full run on request:** the `ci:full` label runs tier 3 on that pull request.

Example: Ben changes `Shared/web/sync-ui/src/RefreshPanel.tsx`. Tier 2 runs vitest for `sync-ui` and the packages that depend on it (`workspace`, the
console), the console's web build, and the browser stories. No .NET project builds and no engine starts.

**Tier 3: weekly, and on demand.** Everything, on every platform it supports:

- every .NET suite on Linux and Windows;
- every engine, including the previous release of each state engine;
- every browser story;
- the installers: the MSI, the Scoop and install scripts, the compose quickstart;
- Build's conformance suite, which moves from nightly to here;
- the connection kinds' conformance suites with their test-server containers;
- the sandbox backends and the hostile-job tests (SANDBOX);
- the compatibility job against the previous release.

It runs on `main` every week. It also runs on any commit by hand, and must be green on the commit a stable release ships.

**After a merge to `main`.** Tier 2 runs again for the merged change, because `main` may have moved since the pull request's run. Nothing more.

**What a failure in each tier does.**

- **Tiers 1 and 2:** a failure blocks the merge.
- **Tier 3:** a failure on `main` opens a tracked item naming the suite and the first commit it failed on, and holds the next stable release until it's
  green. It doesn't block pull requests.

## Flakes

A flaky test is one that fails and then passes on a rerun with no change.

- **The list.** Every known flake is in `docs/ci-flakes.md`: the test, the signature of its failure, when it was first seen, and the task that fixes it.
- **Reruns.** The reviewer reruns a failed job only when the failure matches an entry on the list. A failure that matches nothing is treated as real.
- **Leaving the list.** A test leaves the list when a fix has merged and the test has passed in four weekly runs since. The fix is the cause found and
  fixed, never a skip or a longer wait with no reason.
- **A new flake.** It gets its entry and its fix task in the same week it's first seen.
- **The weekly run** reports how many entries the list has and which failed that week.

Example: `RunnerDataChangedTests` fails on Windows with `409 Conflict`. It matches the entry "409 while the queue item finishes", so the reviewer
reruns it once. A failure in the same test with `ArgumentOutOfRangeException` matches no entry, so it goes back to a worker as a real failure.

## A stable release

- **Before shipping:** a stable release ships only a commit with a green tier 3 run. The release workflow checks that before it builds, and refuses with
  a message naming the run, or the missing run, when there's none. A snapshot or beta release doesn't need one.
- **Getting the run:** the person cutting the release starts tier 3 on the commit by hand when the weekly run didn't cover it.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CI-1 | **The pull-request tiers.** `scripts/affected.py` (standard library only) works out from the changed files which .NET projects and web packages to build and test, from their references; a short list in the script names the Windows-specific projects. One pull-request workflow runs tier 1, then tier 2 from the script's answer; the browser stories run when a web package or the console changed. The product workflows lose their `pull_request` trigger | none | M | the script's tests: a change to `DbDataKit.Connections.Http` selects the Runner kit, the console and the Gateway, and not Sync's engine tests; a change to `docs/` selects tier 1 only; a pull request runs only the new workflow |
| CI-2 | **The weekly run and the release gate.** One workflow runs tier 3 on `main` every week, and by hand on any commit; Build's nightly conformance run moves into it. A failed weekly run opens a tracked item naming the failed suites. The stable release workflow refuses a commit without a green tier 3 run | none | M | a run started by hand covers every suite in tier 3; a stable release on a commit with no green run is refused, naming the missing run |
| CI-3 | **The flake list.** `docs/ci-flakes.md` holds every flake rerun in the last month, each with its fix task; the weekly run reports the list; the CI docs (`docs/ci-lessons.md`, `docs/agent-workflow.md`, the release docs) describe the tiers | CI-1, CI-2 | S | the list holds every flake rerun in the last month; the docs name the workflows that exist |
| CI-4 | The compatibility job runs the previous release's programs against the current build (waits for the first stable release); the release workflows write `release.json` with the compatibility generation, so the console's "outside N-1" mark shows for a release from GitHub, not only from a mirror | none | M | the `compatibility` job pairs the previous stable console with the current Gateway and Runner and the reverse, and a publish and a query pass; a snapshot release carries `release.json` with its generation and the console's Upgrades page marks a release outside N-1 from it |
| CI-6 | Run the other products' suites on the 2025 engines (SQL Server 2025, pgvector PostgreSQL) | none | S | the Gateway, Runner and Build conformance jobs pass on the 2025 profiles |

## Shared areas touched

- `.github/workflows/` (every CI workflow's triggers), `scripts/` (the affected-project script and its tests), `docs/ci-lessons.md`,
  `docs/ci-flakes.md`, the release docs, `.claude/agents/reviewer.md`.
