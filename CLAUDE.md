# Working in the DbDataKit repository

One repository, four products and the libraries they share. Read the product's own docs before changing it; `DbDataBuild/CLAUDE.md`
lists rules its tests enforce.

| Folder | What it is | Owns |
| --- | --- | --- |
| `DbDataKit/` | the shared libraries (`DbDataKit.*`, packages under `src/`, tests under `tests/`) | what two or more products need: config, connections, dialects, metadata, secrets, hosting, transfer |
| `DbDataSync/` | replication tool: ASP.NET API (no web app of its own), its screens (`DbDataKit/web/sync`, an npm workspace, shown by the console), per-run TaskRunner | the replication engine, its drivers, its UI |
| `DbDataGateway/` | Arrow Flight SQL gateway for relational databases | the Flight SQL server, its authentication, the 32-bit ODBC worker |
| `DbDataConsole/` | the `dbdatakit` console host: the web app (`DbDataKit/web/console`): sign-in, users, invitations, the servers it manages and their screens, every module's docs (more arrive slice by slice) | the console's host and its command line |
| `DbDataStore/` | master data models in your own databases (`model/1`, validated writes, history, audit; plan 15): a server of the suite's shape (`docs/architecture.md`), the skeleton so far | the store's server and its command line |
| `DbDataRunner/` | scheduled and batch work: the runner host (`dbdatarunner`; the engine is the library `DbDataKit.Runner`; plan 28 and 28a): a server of the suite's shape (`docs/architecture.md`) that runs the tasks of published packages, each in a process of its own | the host and its command line |
| `DbDataWorkspace/` | the Workspace server (`dbdataworkspace`; plan 22a GP31, decision D8; the workspace calls are the library `DbDataKit.Authoring`): the projects people and agents author in git workspaces (an app today, plan 27 adds more kinds) and a record of what was done, a server of the suite's shape (`docs/architecture.md`); no builds yet | the host and its command line |
| `DbDataBuild/` | CLI that builds tables and views from DuckDB-dialect SQL models | the model language, lowering, targets, plans |
| `dist/` | install scripts, MSI and Scoop generation for every product | packaging |
| `testenv/` | SQL Server and PostgreSQL containers shared by the suites | test engines |
| `scripts/` | repository-wide checks (Python, standard library only) | rules about the repository as a whole |
| `docs/` | cross-product documentation: `architecture.md` (the suite's shape and decisions), `releases.md`, `suite-*.md` | |
| `docs/plans/` | the open plans; `README.md` is the index (a finished plan's row points at the document that replaced it); `13c-follow-ups.md` holds what is left of the early suite work | |

A product references a shared library as `<DbDataKitReference Include="Hosting.Cli" />` (its `Directory.Build.targets` turns it into a
project reference in this repository, a package reference otherwise). Do not copy shared code into a product.

## Build setup

- The root `Directory.Build.props` (target framework, language version, nullable), `Directory.Packages.props` (**every** NuGet version)
  and `global.json` (SDK) apply to everything; each product's props imports the root one and adds its own.
- Versions live only in the root `Directory.Packages.props`. A `PackageReference` has no `Version`; the one allowed `VersionOverride`
  is listed in `scripts/check_package_versions.py`.
- `global.json` is read from the nearest folder only. `DbDataKit/` and `DbDataGateway/` have their own copy that adds the Microsoft Testing
  Platform runner (xunit v3); Sync and Build test on VSTest (xunit v2) and use the root one.
- `DbDataKit.slnx` lists every project (`scripts/check_solution.py` keeps it complete); `<Product>.slnf` is each product's filter of it.
- Warnings are errors in every product.

## Build and test

```
dotnet build DbDataKit.slnx                                   # everything
python3 -I -m unittest discover -s scripts/tests              # the repository checks
python3 -I scripts/check_package_versions.py && python3 -I scripts/check_solution.py && python3 -I scripts/check_duckdb_cache.py && python3 -I scripts/check_native_cache.py && python3 -I scripts/check_plans.py && python3 -I scripts/check_formats.py && python3 -I scripts/check_ui_editors.py && python3 -I scripts/check_env_names.py
```

Per product, from its folder: `dotnet build`, then the suites below. Run the suites a change can affect; CI runs the rest on the pull request.

- **DbDataKit, DbDataGateway, DbDataConsole, DbDataStore, DbDataRunner, DbDataWorkspace** (xunit v3, MTP): `dotnet test --project tests/<Name>`. If the test host cannot start (`Handshake failures`),
  run the built executable: `tests/<Name>/bin/Debug/net10.0/<Name>`; `-class` and `-method` select tests.
- **DbDataSync**: `dotnet test --filter "Category!=Integration"` (add `DbDataSync.slnf`); `Category=Integration` needs the engines.
- **DbDataBuild**: `scripts/fetch-native.sh` once, then `dotnet test tests/DbDataBuild.Tests.Unit`. The conformance suite needs engines.

Docker in a cloud (Claude Code on the web) session: Docker is installed but its daemon is not running at start, so `docker info` says it cannot
connect. Start it yourself, as root: `(nohup dockerd > /tmp/dockerd.log 2>&1 &); sleep 8; docker info` (it listens on `/var/run/docker.sock`).
Do not conclude Docker is unavailable, and do not skip the SQL Server and PostgreSQL runs of a state or engine test for that reason: start the
daemon, then `testenv/testenv.sh up mssql postgres`. Two limits of that environment, found by running `testenv` there: Docker Hub answers anonymous pulls with `429 Too Many Requests` (pull through the
mirror and tag the result: `docker pull mirror.gcr.io/library/postgres:17 && docker tag mirror.gcr.io/library/postgres:17 postgres:17`, the same for
`mirror.gcr.io/pgvector/pgvector:pg17` (and `:pg16` for `postgres16`; SQL Server 2022 pulls from `mcr.microsoft.com` directly), then `DBKIT_TEST_PULL=false testenv/testenv.sh up ...`), and the proxy lets containers reach registries but not
package repositories (`apt-get` inside a build fails with 403), so a testenv image must be pulled whole, never built with a package install.
Then `eval "$(testenv/testenv.sh env dbdatakit)"` sets `DBDATA_TEST_SQLSERVER` and `DBDATA_TEST_POSTGRES` for the state suites.

Test engines:

- One environment for every suite, `testenv/docker-compose.yml`, one profile per engine: `testenv/testenv.sh up mssql postgres`, then
  `eval "$(testenv/testenv.sh env dbdatagateway)"` (also `dbdatakit`, `dbdatabuild`, `dbdatasync`); `down` when finished. Profiles: `mssql`
  (14330, SQL Server Agent on), `mssql-target` (14331), `postgres` (15432), `postgres-logical` (15433), `mysql` (13306), `mariadb`
  (13307), `oracle` (15210). The previous release of each state engine (N-1, plan 29b U01) runs beside the current ones and is left out of a bare `up`:
  `mssql2022` (14333, SQL Server 2022) and `postgres16` (15434, `pgvector/pgvector:pg16`); `testenv/testenv.sh up mssql2022 postgres16`, then
  `eval "$(testenv/testenv.sh env dbdatakit-older)"` points `DBDATA_TEST_SQLSERVER` / `DBDATA_TEST_POSTGRES` (and the explicit `DBDATA_TEST_SQLSERVER2022` /
  `DBDATA_TEST_POSTGRES16`) at them, so the State suites (`DbDataKit.State.Tests`, `.State.Engines.Tests`, `.State.Cli.Tests`, `.Scheduling.Tests`) run on them; CI does this in the job `state-older-engines`. Sync: `testenv/testenv.sh up mssql mssql-target postgres-logical mysql mariadb oracle`, then
  `eval "$(testenv/testenv.sh env dbdatasync)"`. `postgres-logical` is the one second engine of a kind (wal_level=logical and wal2json,
  built from `testenv/postgres-logical` on a fresh `postgres:17`), kept apart so the plain `postgres` stays what the other suites test.
  Run one stack at a time on a machine: the ports are fixed.
- The DuckDB engine (`duckdb.pin`): `scripts/fetch-duckdb.sh` (or `.ps1`) downloads and hash-checks it. They read `DUCKDB_RELEASE_BASE` (a `file://` folder or an https address) before the pin's address and retry a failed download; CI fills a folder from the Actions cache with
  `.github/actions/duckdb-cache`, so a GitHub release outage does not fail runs (`scripts/check_duckdb_cache.py` makes a new job use it; `docs/ci-lessons.md`).
- The Build native library (`DbDataBuild/scripts/polyglot.pin`, the polyglot-sql FFI release): `DbDataBuild/scripts/fetch-native.sh` downloads it and checks the archive against the hash in the repository. It reads `POLYGLOT_RELEASE_BASE` (a `file://` folder or an https address) before upstream's address and retries; CI fills the folder `.native-archives/` with `.github/actions/native-cache`, and `scripts/check_native_cache.py` makes every job or Dockerfile that fetches the library use it.
- Tests with ODBC data sources (Gateway's SQL Server tests, Kit's ODBC projects) need unixODBC and Microsoft's "ODBC Driver 18 for SQL
  Server" installed on the machine. In a cloud session run `scripts/install-odbc.sh` (Debian/Ubuntu; it is the CI install, safe to repeat).

## Making changes

Work goes in **batches**: one integration branch, one pull request, one CI run. Parallel work (subagents) is welcome. What costs time is several pull
request branches open at once with different roots, each running CI and each fixing the same bugs, then being merged and re-tested after the fact.
The rule is about that, not about branches or merge style.

- A batch is one branch `suite/batch-<n>` from `main`, one pull request at a time. Each slice (a row of plan 28a, 28c, 26 and so on) is **one commit**
  on it whose message starts with the slice id (`E04: ...`); set the slice's status in its plan in that same commit. If a slice turns out wrong, change
  the plan in the batch and say so.
- Work in progress may be pushed (to back it up, to share it, to let CI run on it), as long as the branch is **pruned when it is done** (see Branches).
  Open at most one batch pull request at a time, and start the next batch from the merged `main`; do not run several pull requests side by side on
  different roots.
- Subagents work in their own local worktrees and report a local branch; the coordinator brings each slice into the batch (`git merge --squash` gives one
  commit per slice), resolves the overlaps once, builds and tests the combination, and pushes the batch. Subagents do not open pull requests. A fix to a
  CI problem that more than one slice meets goes into the batch at once (and into `docs/ci-lessons.md`), and slices still being built pick it up by
  merging the batch branch into their worktree.
- A merge commit or a rebase is fine, whichever is simpler for the pull request. Prefer "Rebase and merge" (or squash for a single slice) because it keeps
  `main` readable, but it is not a rule worth a delay.
- Before anything is pushed, read `docs/ci-lessons.md` and run its "before you push" list: the CI problems already found and fixed are written there with
  how to recognise them. A new CI problem and its fix are added to that file in the same commit that fixes it.
- **A finished plan becomes a product doc.** When the last slice of a plan lands, the same commit turns what is still true and useful (how it works,
  its rules and conventions, the decisions and why, how to use or operate it) into documentation beside the code it describes (a package or product README,
  `docs/<topic>.md`, a help page), written as how it is, not as a plan, and deletes the plan. History, rationale about abandoned options and status
  narrative are not carried over; git keeps them. Open decisions and unbuilt work move to the plan that owns them. **The same goes for a plan that is
  closed, dropped or superseded**: it is deleted, never kept with a status that says so; if it holds anything useful (what exists, rules in force) that becomes
  documentation, and the plans README row goes or points at that document. Never leave a plan whose status says
  proposed or in progress while all its rows are done, and never link to a plan that does not exist. `scripts/check_plans.py` checks this (links between
  plans, the numbers the index names, the index itself, open status with every row done, a status that says closed, dropped or superseded); what it tolerates today is in `scripts/plan_check_baseline.txt`,
  which only shrinks.
- **Done means its test passes:** a slice's "Done when" is a test run in CI, or it says what is checked by hand and why.
- **Adapter first, delete second:** a slice that replaces code keeps the old tests passing unchanged; deleting the old code is its own slice, or the last step when the tests already pass.
- A clean break is allowed, but never a silent one: a changed file format fails with a message naming the file, and a test says so.
- **Formats are listed and frozen** (plan 24a UC02): `docs/formats.json` has each definition and file format with its current and previous version and whether it is open or frozen.
  `scripts/check_formats.py` fails when the code declares another version than the file says (a bump needs its entry) and when a file of a frozen format changed (its sha256 is recorded).
  A frozen format is never edited: the change is the next number, with a new entry and new hashes (`python3 -I scripts/check_formats.py --hash <file>`).
- **Everything built and published has an editor screen** (owner, 2026-11-08): a stored format, or a thing a person authors or publishes by file or command, is not finished until a screen in the console, the Build or Sync workspace or the Gateway edits it, or `docs/ui-editors.md` says `none (reason)` or names the slice (of the plan that owns the screen) that will; adding a row is described in that page, and `scripts/check_ui_editors.py` keeps it complete.
  `scripts/check_ui_editors.py` fails, naming the format, when one registered in `docs/formats.json` has no row there; a slice that adds or widens an editor changes its row in the same commit.
- Branches: pushing a work-in-progress branch is fine; leaving it behind is not. The workflow `delete-merged-branch.yml` deletes a pull request's branch
  when it merges; a branch that was pushed and then abandoned, or whose work moved into another branch, is deleted by whoever abandoned it, in the same
  session. Sessions cannot delete remote branches themselves: the list `scripts/branches-to-delete.txt` and the workflow `delete-listed-branches.yml`
  (see `docs/branch-cleanup.md`) do it. Bring a branch up to date by merging `main` into it rather than copying it to a new name. Do not create one
  branch per task.
