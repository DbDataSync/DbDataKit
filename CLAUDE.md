# Working in the DbDataKit repository

One repository, four products and the libraries they share. Read the product's own docs before changing it; `DbDataBuild/CLAUDE.md`
lists rules its tests enforce.

| Folder | What it is | Owns |
| --- | --- | --- |
| `DbDataKit/` | the shared libraries (`DbDataKit.*`, packages under `src/`, tests under `tests/`) | what two or more products need: config, connections, dialects, metadata, secrets, hosting, transfer |
| `DbDataSync/` | replication tool: ASP.NET API (no web app of its own), its screens (`DbDataKit/web/sync`, an npm workspace, shown by the console), per-run TaskRunner | the replication engine, its drivers, its UI |
| `DbDataProxy/` | Arrow Flight SQL proxy for relational databases | the Flight SQL server, its authentication, the 32-bit ODBC worker |
| `DbDataConsole/` | the `dbdatakit` console host: the web app (`DbDataKit/web/console`): sign-in, users, invitations, the servers it manages and their screens, every module's docs (more arrive slice by slice, plan 13 section U) | the console's host and its command line |
| `DbDataStore/` | master data models in your own databases (`model/1`, validated writes, history, audit; plan 15 and plan 19 lane Z): a server on plan 13's shape, the skeleton so far | the store's server and its command line |
| `DbDataRunner/` | scheduled and batch work: the runner host (`dbdatarunner`; the engine is the library `DbDataKit.Runner`; plan 28 and 28a): a server on plan 13's shape that runs the tasks of published packages, each in a process of its own | the host and its command line |
| `DbDataBuild/` | CLI that builds tables and views from DuckDB-dialect SQL models | the model language, lowering, targets, plans |
| `dist/` | install scripts, MSI and Scoop generation for every product | packaging |
| `testenv/` | SQL Server and PostgreSQL containers shared by the suites | test engines |
| `scripts/` | repository-wide checks (Python, standard library only) | rules about the repository as a whole |
| `docs/plans/` | the plans; `13-suite-standardization.md` and `13a-suite-slices.md` are the current one | |

A product references a shared library as `<DbDataKitReference Include="Hosting.Cli" />` (its `Directory.Build.targets` turns it into a
project reference in this repository, a package reference otherwise). Do not copy shared code into a product.

## Build setup

- The root `Directory.Build.props` (target framework, language version, nullable), `Directory.Packages.props` (**every** NuGet version)
  and `global.json` (SDK) apply to everything; each product's props imports the root one and adds its own.
- Versions live only in the root `Directory.Packages.props`. A `PackageReference` has no `Version`; the one allowed `VersionOverride`
  is listed in `scripts/check_package_versions.py`.
- `global.json` is read from the nearest folder only. `DbDataKit/` and `DbDataProxy/` have their own copy that adds the Microsoft Testing
  Platform runner (xunit v3); Sync and Build test on VSTest (xunit v2) and use the root one.
- `DbDataKit.slnx` lists every project (`scripts/check_solution.py` keeps it complete); `<Product>.slnf` is each product's filter of it.
- Warnings are errors in every product.

## Build and test

```
dotnet build DbDataKit.slnx                                   # everything
python3 -I -m unittest discover -s scripts/tests              # the repository checks
python3 -I scripts/check_package_versions.py && python3 -I scripts/check_solution.py && python3 -I scripts/check_plans.py
```

Per product, from its folder: `dotnet build`, then the suites below. Run the suites a change can affect; CI runs the rest on the pull request.

- **DbDataKit, DbDataProxy, DbDataConsole, DbDataStore, DbDataRunner** (xunit v3, MTP): `dotnet test --project tests/<Name>`. If the test host cannot start (`Handshake failures`),
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
  `eval "$(testenv/testenv.sh env dbdataproxy)"` (also `dbdatakit`, `dbdatabuild`, `dbdatasync`); `down` when finished. Profiles: `mssql`
  (14330, SQL Server Agent on), `mssql-target` (14331), `postgres` (15432), `postgres-logical` (15433), `mysql` (13306), `mariadb`
  (13307), `oracle` (15210). The previous release of each state engine (N-1, plan 29b U01) runs beside the current ones and is left out of a bare `up`:
  `mssql2022` (14333, SQL Server 2022) and `postgres16` (15434, `pgvector/pgvector:pg16`); `testenv/testenv.sh up mssql2022 postgres16`, then
  `eval "$(testenv/testenv.sh env dbdatakit-older)"` points `DBDATA_TEST_SQLSERVER` / `DBDATA_TEST_POSTGRES` (and the explicit `DBDATA_TEST_SQLSERVER2022` /
  `DBDATA_TEST_POSTGRES16`) at them, so the State suites (`DbDataKit.State.Tests`, `.State.Engines.Tests`, `.State.Cli.Tests`, `.Scheduling.Tests`) run on them; CI does this in the job `state-older-engines`. Sync: `testenv/testenv.sh up mssql mssql-target postgres-logical mysql mariadb oracle`, then
  `eval "$(testenv/testenv.sh env dbdatasync)"`. `postgres-logical` is the one second engine of a kind (wal_level=logical and wal2json,
  built from `testenv/postgres-logical` on a fresh `postgres:17`), kept apart so the plain `postgres` stays what the other suites test.
  Run one stack at a time on a machine: the ports are fixed.
- Tests with ODBC data sources (Proxy's SQL Server tests, Kit's ODBC projects) need unixODBC and Microsoft's "ODBC Driver 18 for SQL
  Server" installed on the machine. In a cloud session run `scripts/install-odbc.sh` (Debian/Ubuntu; it is the CI install, safe to repeat).

## Making changes

Work goes in **batches**: one integration branch, one pull request, one CI run, a linear history. Parallel work (subagents) is welcome; what is not
welcome is a branch and a pull request per task, each running into the same CI problems on its own.

- A batch is one branch `suite/batch-<n>` from `main`, pushed once or a few times, one pull request. Each slice (a row of plan 13a, 28a, 28c, 26 and so on)
  is **one commit** on it whose message starts with the slice id (`E04: ...`); set the slice's status in its plan in that same commit. If a slice turns out
  wrong, change the plan in the batch and say so.
- Subagents work in their own local worktrees and **do not push and do not open pull requests**. They commit on a local branch and report it. The
  coordinator brings each slice into the batch with `git merge --squash` (one commit per slice, no merge commits), resolves the overlaps once, builds and
  tests the combination, and only then pushes the batch. A fix to a CI problem that more than one slice meets goes into the batch at once (and into
  `docs/ci-lessons.md`), and slices still being built pick it up by merging the batch branch into their worktree.
- `main` stays linear: the batch pull request is merged with "Rebase and merge" (or squashed when it is a single slice), never with a merge commit.
- Before anything is pushed, read `docs/ci-lessons.md` and run its "before you push" list: the CI problems already found and fixed are written there with
  how to recognise them. A new CI problem and its fix are added to that file in the same commit that fixes it.
- **Finished plans are deleted or cut down.** When the last slice of a plan lands, the same commit deletes the plan, or cuts it to a short "as built"
  note beside the product's docs; open decisions and unbuilt work move to the plan that owns them. Never leave a plan whose status says proposed or
  in progress while all its rows are done, and never link to a plan that does not exist. `scripts/check_plans.py` checks this (links between plans,
  the numbers the index names, the index itself, open status with every row done); what it tolerates today is in `scripts/plan_check_baseline.txt`,
  which only shrinks.
- A clean break is allowed, but never a silent one: a changed file format fails with a message naming the file, and a test says so.
- Branches: do not push experiments, scratch copies, "draft" or "wip" branches, or one branch per task. The workflow `delete-merged-branch.yml` deletes a
  pull request's branch when it merges; a branch whose work is abandoned is deleted by whoever abandoned it, in the same session. Sessions cannot delete
  remote branches themselves: the list `scripts/branches-to-delete.txt` and the workflow `delete-listed-branches.yml` (see `docs/branch-cleanup.md`) do it.
  Bring a branch up to date by merging `main` into it, not by copying it to a new name.
