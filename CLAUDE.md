# Working in the DbDataKit repository

One repository: the suite's four services (the console, Workspace, Gateway and Runner; the Build, Sync and Store servers were deleted in plan 33 SC23), the command lines and engines they run, and the libraries they share. Read the product's own docs before changing it; `DbDataBuild/CLAUDE.md`
lists rules its tests enforce.

| Folder | What it is | Owns |
| --- | --- | --- |
| `Shared/` | the shared libraries (`DbDataKit.*`, packages under `src/`, tests under `tests/`) | what two or more products need: config, connections, dialects, metadata, secrets, hosting, transfer |
| `DbDataSync/` | replication: `dbdatasync`, a command line on a configuration repository, and the engine a Runner runs (per-run TaskRunner); no server since plan 33 SC23. It is authored as a Workspace sync project, run from that project's Runs tab and the Runner's screens | the replication engine, its drivers, its command line |
| `DbDataGateway/` | Arrow Flight SQL gateway for relational databases | the Flight SQL server, its authentication, the 32-bit ODBC worker |
| `DbDataKit/` | the product, the `dbdatakit` host (projects `DbDataKit.Server*`; "the console" is the web app of it): the web app (`Shared/web/console`): sign-in, users, invitations, the servers it manages (Workspace, Gateway, Runner; a paired Build, Sync or Store server is told where its features went) and their screens, every module's docs (more arrive slice by slice) | the console's host and its command line |
| `DbDataStore/` | master data models in your own databases (`model/1`, validated writes, history, audit; plan 15): no server since plan 33 SC23; `dbdatastore` is the runtime of a model's package on a Runner and a command line on a folder of models | the store's runtime and its command line |
| `DbDataRunner/` | scheduled and batch work: the runner host (`dbdatarunner`; the engine is the library `DbDataKit.Runner`; plan 28 and 28a): a server of the suite's shape (`docs/architecture.md`) that runs the tasks of published packages, each in a process of its own | the host and its command line |
| `DbDataWorkspace/` | the Workspace server (`dbdataworkspace`; plan 22a GP31, decision D8; the workspace calls are the library `DbDataKit.Authoring`): the projects people and agents author in git workspaces (an app today, plan 27 adds more kinds) and a record of what was done, a server of the suite's shape (`docs/architecture.md`); no builds yet | the host and its command line |
| `DbDataBuild/` | CLI that builds tables and views from DuckDB-dialect SQL models, and the runtime of a Build package on a Runner (no server since plan 33 SC23); authored as a Workspace build project | the model language, lowering, targets, plans |
| `dist/` | install scripts, MSI and Scoop generation for every product | packaging |
| `testenv/` | SQL Server and PostgreSQL containers shared by the suites | test engines |
| `scripts/` | repository-wide checks (Python, standard library only) | rules about the repository as a whole |
| `docs/` | cross-product documentation: `architecture.md` (the suite's shape and decisions), `releases.md`, `suite-*.md` | |
| `planning/` | the open work: `README.md` is the one index of subjects in priority order, `<SLUG>.md` is one subject (goal, tasks, needs, proposed decisions), `ROLES.md` is how the work is run | |

A product references a shared library as `<DbDataKitReference Include="Hosting.Cli" />` (its `Directory.Build.targets` turns it into a
project reference in this repository, a package reference otherwise). Do not copy shared code into a product.

**Reusable libraries live under `Shared/` (owner, 2026-11-16).** A library that more than one service, command line, engine or package composes, or
that is written to be composed (a feature such as quality, lineage, delivery, a task a package can declare), is a project `Shared/src/DbDataKit.<Name>`
with its tests in `Shared/tests/DbDataKit.<Name>.Tests`, never a project under a product's folder. A product's folder holds only what makes that
product: its host, its command line, its own engine and that engine's internals. When code in a product folder turns out to be needed by a second
product, it moves to `Shared/src/DbDataKit.*` first (an adapter in the product if that keeps the old tests passing), then the second product uses it.

## Build setup

- The root `Directory.Build.props` (target framework, language version, nullable), `Directory.Packages.props` (**every** NuGet version)
  and `global.json` (SDK) apply to everything; each product's props imports the root one and adds its own.
- Versions live only in the root `Directory.Packages.props`. A `PackageReference` has no `Version`; the one allowed `VersionOverride`
  is listed in `scripts/check_package_versions.py`.
- `global.json` is read from the nearest folder only. `Shared/` and `DbDataGateway/` have their own copy that adds the Microsoft Testing
  Platform runner (xunit v3); Sync and Build test on VSTest (xunit v2) and use the root one.
- `DbDataKit.slnx` lists every project (`scripts/check_solution.py` keeps it complete); `<Product>.slnf` is each product's filter of it.
- Warnings are errors in every product.

## Build and test

```
dotnet build DbDataKit.slnx                                   # everything
python3 -I -m unittest discover -s scripts/tests              # the repository checks
python3 -I scripts/check_package_versions.py && python3 -I scripts/check_solution.py && python3 -I scripts/check_duckdb_cache.py && python3 -I scripts/check_native_cache.py && python3 -I scripts/check_plans.py && python3 -I scripts/check_formats.py && python3 -I scripts/check_state_migrations.py && python3 -I scripts/check_compatibility.py && python3 -I scripts/check_ui_editors.py && python3 -I scripts/check_env_names.py && python3 -I scripts/check_secret_names.py && python3 -I scripts/check_default_ports.py && python3 -I scripts/check_product_name.py && python3 -I scripts/check_doc_inputs.py
```

Before a pull request into `main` merges: `python3 -I scripts/check_no_wip.py` (it fails while any file exists under `planning/wip/`; CI runs it on
pull requests into `main` and on pushes to `main`, not on other branches).

Per product, from its folder: `dotnet build`, then the suites below. Run the suites a change can affect; CI runs the rest on the pull request.

- **Shared, DbDataGateway, DbDataKit, DbDataStore, DbDataRunner, DbDataWorkspace** (xunit v3, MTP): `dotnet test --project tests/<Name>`. If the test host cannot start (`Handshake failures`),
  run the built executable: `tests/<Name>/bin/Debug/net10.0/<Name>`; `-class` and `-method` select tests.
- **DbDataSync**: `dotnet test --filter "Category!=Integration"` (add `DbDataSync.slnf`); `Category=Integration` needs the engines.
- **DbDataBuild**: `scripts/fetch-native.sh` once, then `dotnet test tests/DbDataBuild.Tests.Unit`. The conformance suite needs engines.

Docker in a cloud (Claude Code on the web) session: Docker is installed but its daemon is not running at start, so `docker info` says it cannot
connect. Start it yourself, as root: `(nohup dockerd > /tmp/dockerd.log 2>&1 &); sleep 8; docker info` (it listens on `/var/run/docker.sock`).
Do not conclude Docker is unavailable, and do not skip the SQL Server and PostgreSQL runs of a state or engine test for that reason: start the
daemon, then `testenv/testenv.sh up mssql postgres`. Two limits of that environment, found by running `testenv` there: Docker Hub answers anonymous pulls with `429 Too Many Requests` (pull through the
mirror and tag the result: `docker pull mirror.gcr.io/library/postgres:17 && docker tag mirror.gcr.io/library/postgres:17 postgres:17`, the same for
`mirror.gcr.io/pgvector/pgvector:pg17` (and `:pg16` for `postgres16`; SQL Server 2022 pulls from `mcr.microsoft.com` directly), then `DBDATAKIT_TEST_PULL=false testenv/testenv.sh up ...`), and the proxy lets containers reach registries but not
package repositories (`apt-get` inside a build fails with 403), so a testenv image must be pulled whole, never built with a package install.
Then `eval "$(testenv/testenv.sh env dbdatakit)"` sets `DBDATAKIT_TEST_SQLSERVER` and `DBDATAKIT_TEST_POSTGRES` for the state suites.

Test engines (every variable of a test, harness or CI step is `DBDATAKIT_TEST_<NAME>`; `docs/testing-environment.md` has the old-to-new table):

- One environment for every suite, `testenv/docker-compose.yml`, one profile per engine: `testenv/testenv.sh up mssql postgres`, then
  `eval "$(testenv/testenv.sh env dbdatagateway)"` (also `dbdatakit`, `dbdatabuild`, `dbdatasync`); `down` when finished. Profiles: `mssql`
  (14330, SQL Server Agent on), `mssql-target` (14331), `postgres` (15432), `postgres-logical` (15433), `mysql` (13306), `mariadb`
  (13307), `oracle` (15210). The previous release of each state engine (N-1, plan 29b U01) runs beside the current ones and is left out of a bare `up`:
  `mssql2022` (14333, SQL Server 2022) and `postgres16` (15434, `pgvector/pgvector:pg16`); `testenv/testenv.sh up mssql2022 postgres16`, then
  `eval "$(testenv/testenv.sh env dbdatakit-older)"` points `DBDATAKIT_TEST_SQLSERVER` / `DBDATAKIT_TEST_POSTGRES` (and the explicit `DBDATAKIT_TEST_SQLSERVER2022` /
  `DBDATAKIT_TEST_POSTGRES16`) at them, so the State suites (`DbDataKit.State.Tests`, `.State.Engines.Tests`, `.State.Cli.Tests`, `.Scheduling.Tests`) run on them; CI does this in the job `state-older-engines`. Sync: `testenv/testenv.sh up mssql mssql-target postgres-logical mysql mariadb oracle`, then
  `eval "$(testenv/testenv.sh env dbdatasync)"`. `postgres-logical` is the one second engine of a kind (wal_level=logical and wal2json,
  built from `testenv/postgres-logical` on a fresh `postgres:17`), kept apart so the plain `postgres` stays what the other suites test.
  Run one stack at a time on a machine: the ports are fixed.
- The DuckDB engine (`duckdb.pin`): `scripts/fetch-duckdb.sh` (or `.ps1`) downloads and hash-checks it. They read `DUCKDB_RELEASE_BASE` (a `file://` folder or an https address) before the pin's address and retry a failed download; CI fills a folder from the Actions cache with
  `.github/actions/duckdb-cache`, so a GitHub release outage does not fail runs (`scripts/check_duckdb_cache.py` makes a new job use it; `docs/ci-lessons.md`).
- The Build native library (`DbDataBuild/scripts/polyglot.pin`, the polyglot-sql FFI release): `DbDataBuild/scripts/fetch-native.sh` downloads it and checks the archive against the hash in the repository. It reads `POLYGLOT_RELEASE_BASE` (a `file://` folder or an https address) before upstream's address and retries; CI fills the folder `.native-archives/` with `.github/actions/native-cache`, and `scripts/check_native_cache.py` makes every job or Dockerfile that fetches the library use it.
- Tests with ODBC data sources (Gateway's SQL Server tests, Kit's ODBC projects) need unixODBC and Microsoft's "ODBC Driver 18 for SQL
  Server" installed on the machine. In a cloud session run `scripts/install-odbc.sh` (Debian/Ubuntu; it is the CI install, safe to repeat).

## Making changes

How the work is run is [planning/ROLES.md](planning/ROLES.md): the owner, the sessions the owner talks with, the workers they start
(`.claude/agents/worker.md`) and the reviewer that merges (`.claude/agents/reviewer.md`). In short:

- **A session does no product code work.** A conversation with the owner talks, designs, writes and changes planning (subject files, proposed decisions,
  open designs) and starts workers and reviewers; it writes no product code, fixes no tests and resolves no merge conflicts. That work is a worker's
  (code, tests) or the reviewer's (mechanical conflicts while combining). The same holds for everything around the code: scripts, hooks,
  `.claude/` settings, `.mcp.json`, the cloud environment's settings and setup script, and services started to try something. A session reads
  (code, docs, planning) and may build or run something only to answer a question, saying so; it changes nothing outside `planning/` until the owner has
  agreed the plan.
- **Talk with the owner in short messages, one question at a time** ([planning/ROLES.md](planning/ROLES.md), "Talking with the owner"): the answer or
  question first, each question complete on its own; proposed text for docs and planning shown in small batches and applied only after approval.
- **Some files change only with the owner's approval** ([planning/ROLES.md](planning/ROLES.md), "Files only the owner changes"): `CLAUDE.md`,
  `planning/ROLES.md`, `docs/architecture.md`, the `product-docs` skill and the protection's own settings and hook. Claude Code asks before writing them.
- **A new request starts in `planning/`, not in the code** ([planning/ROLES.md](planning/ROLES.md), "A new request"): find the subjects that cover it in
  `planning/README.md` and read them, check the product docs they name, say what exists, what overlaps and what contradicts, and work the plan out
  with the owner before any change. Ask what the owner means when the request could mean more than one thing; do not offer to build first.
- Open work is a **subject** in `planning/` (`planning/README.md` is the only index; the owner sets its order, status and assignment). A worker builds one
  subject's tasks on `backup/<slug>-<n>` (`backup/installer-3`), edits only its subject file and the code and docs its tasks need, starts each commit
  message with the task id (`INSTALLER-3: ...`), merges `main` into its branch when `main` moves, and reports defaults and questions without deciding them.
  The reviewer combines ready task branches into `work/<slug>` and merges its pull request (title starting with the slug) into `main`, one at a time.
  Planning changes outside a subject's tasks go on `planning/<topic>`.
- **Nothing stays only on one machine:** every commit is pushed to its branch as soon as it is made; a commit for `main` that must wait is first pushed to a
  `backup/<topic>` branch; a report names the branch and commit where the work is pushed. A harness that starts on another branch name switches to the standard one before
  its first push.
- **Work in progress lives in `planning/wip/<SLUG>/<SLUG>-<n>.md`** on the task's branch, never in the subject file or a product doc: what is built so far,
  open questions, defaults taken (each with an example). A task branch is ready when it has none: finished work leaves the subject file, an answered
  question becomes design in the product doc, an unanswered question or a default becomes a proposed decision `<SLUG>-P<n>` (a concrete scenario with
  options and a recommendation), a need goes under "Needs from other subjects". A pull request into `main` cannot merge while any file exists under
  `planning/wip/` (`scripts/check_no_wip.py`, run by CI on pull requests into `main` and on pushes to `main`).
- **Product docs describe how the product is**: what it does, why and an example, never as a list of decisions. Write and check them with the skill
  `product-docs` (`.claude/skills/product-docs/SKILL.md`): plain sentences a reader can act on, no slogans, no decisions, owners or dates in the text. A doc's one date is its review date
  (`<!-- reviewed: YYYY-MM-DD -->` under the title, set when the owner approves a full review of the file; the skill and [planning/ROLES.md](planning/ROLES.md), "Review dates"). When the owner answers a proposed
  decision (in any session; the answer is recorded under the proposal), the proposal leaves the subject file and its outcome is written into the doc that
  describes that part, in a commit whose message names the proposal id and says `answered by the owner: <the answer>`. Nothing is dropped from planning
  without the owner's word, and a commit that removes design text says where it went.
- **`docs/architecture.md` shows where the suite is going**: a subject that changes the architecture keeps its direction in the section "Planned
  architecture changes", marked Planned or In progress, and each landed task moves its part into the description of what is built
  ([planning/ROLES.md](planning/ROLES.md), "Architecture directions").
- **A finished subject becomes product documentation**: the pull request that lands its last task writes what is still useful into the docs beside the code
  and deletes the subject file. `scripts/check_plans.py` keeps `planning/` consistent (the index, slugs, task and proposal ids, references between
  subjects) and refuses any document that links to the old plans folder or to a subject that is gone.
- **Code comments describe the code** (what it does and why), never a plan, task id, proposal or subject; a comment may point at a finished doc as
  `Design: docs/<path>.md#<anchor>` ([docs/code-comments.md](docs/code-comments.md)).
- **Never lose an exception's details** ([docs/error-handling.md](docs/error-handling.md)): code that throws its own error in place of one it caught
  passes the original as the inner exception, logs it, or both; a `Dispose`, `finally` or cleanup step never replaces the error already on its way
  out. This holds in product code, tests and scripts.
- Before anything is pushed, read `docs/ci-lessons.md` and run its "before you push" list. A new CI problem and its fix are added to that file in the same
  commit that fixes it.
- Shared files (`docs/ci-lessons.md`, `docs/formats.json`, `docs/ui-editors.md`, `Directory.Packages.props`, `DbDataKit.slnx`, the check baselines) are
  edited by adding entries, never by rewriting others' entries; release notes are never cut.
- A task that deletes code lists the tasks and documents it voids and updates them in the same pull request.
- Machine resources, build slots and backups for a session with several workers: `docs/agent-workflow.md` (and the skill `agent-batches`).
- **Done means its test passes:** a task's "Done when" is a test run in CI, or it says what is checked by hand and why.
- **Adapter first, delete second:** a task that replaces code keeps the old tests passing unchanged; deleting the old code is its own task, or the last step when the tests already pass.
- A clean break is allowed, but never a silent one: a changed file format fails with a message naming the file, and a test says so.
- **Formats are listed and frozen** (plan 24a UC02): `docs/formats.json` has each definition and file format with its current and previous version and whether it is open or frozen.
  `scripts/check_formats.py` fails when the code declares another version than the file says (a bump needs its entry) and when a file of a frozen format changed (its sha256 is recorded).
  A frozen format is never edited: the change is the next number, with a new entry and new hashes (`python3 -I scripts/check_formats.py --hash <file>`).
- **Everything built and published has an editor screen** (owner, 2026-11-08): a stored format, or a thing a person authors or publishes by file or command, is not finished until a screen in the console, the Workspace (its build, sync and store projects), the Runner or the Gateway edits it, or `docs/ui-editors.md` says `none (reason)` or names the task (of the subject that owns the screen) that will; adding a row is described in that page, and `scripts/check_ui_editors.py` keeps it complete.
  `scripts/check_ui_editors.py` fails, naming the format, when one registered in `docs/formats.json` has no row there; a task that adds or widens an editor changes its row in the same commit.
- Branches: pushing a work-in-progress branch is fine; leaving it behind is not. The workflow `delete-merged-branch.yml` deletes a pull request's branch
  when it merges; a branch that was pushed and then abandoned, or whose work moved into another branch, is deleted by whoever abandoned it, in the same
  session. Sessions cannot delete remote branches themselves: the list `scripts/branches-to-delete.txt` and the workflow `delete-listed-branches.yml`
  (see `docs/branch-cleanup.md`) do it. Bring a branch up to date by merging `main` into it rather than copying it to a new name. `backup/*` branches (`scripts/backup_branches.py`, `docs/agent-workflow.md`) are the exception: pending work is pushed there as a backup and they are deleted automatically as soon as `main` contains them, or when the `work/<slug>` pull request that names their task merges.
