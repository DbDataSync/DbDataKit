# Building DbDataSync from source

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [State database](state-database.md) · **Building from source**

This is for working on DbDataSync itself — contributing, running the dev loop, or building it rather
than installing the packaged CLI. If you just want to run DbDataSync, see [Install](install.md)
instead; nothing here is needed for that.

## Quick start: the dev harness

To get a working dev environment without following the manual steps below, one command does the lot —
containers, databases, seed data, the API, the Sync screens' dev server, and a configured replication:

```sh
tools/dev-harness up          # tools\dev-harness up on Windows
```

Open `http://localhost:5173` (the Sync screens on their own dev server: the product's screens are the dbdatakit console's, see below) and pick the `dev-sync` replication. Ctrl+C stops the API and that server
(the containers keep running; `tools/dev-harness down` stops those).

In another terminal, put real traffic through it:

```sh
tools/dev-harness seed --rows 25000            # bulk-load the source
tools/dev-harness workload --rate 20 --duration 2m   # live inserts/updates/deletes
tools/dev-harness verify                       # compare source and target row by row
tools/dev-harness drift                        # corrupt the target behind the replication's back
```

`drift` is the quickest way to see why batch reload exists: it changes the *target* only, so Change
Tracking has nothing to report and no incremental run will ever repair it — `verify` keeps failing
until you trigger a backfill with a reconciling writer.

`tools/dev-harness help` lists every verb and option. The tool itself is
`tools/DbDataSync.DevHarness`.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 24+](https://nodejs.org/) and npm
- [Docker](https://www.docker.com/) (for the test engines)

## 1. Start the test engines

DbDataSync has no compose file of its own: its databases are profiles of the repository's one test-engine environment,
`testenv/docker-compose.yml`, started with `testenv/testenv.sh` from the repository root (a bash script; on Windows use Git Bash or WSL).

```sh
testenv/testenv.sh up mssql mssql-target postgres-logical
```

This starts two independent SQL Server containers — `dbkit-test-mssql` (`localhost,14330`, with SQL Server Agent on, which change data
capture needs) and `dbkit-test-mssql-target` (`localhost,14331`) — so a local replication genuinely crosses two database server instances,
not just two databases on one shared instance. The third is PostgreSQL with `wal_level=logical` and the wal2json plugin
(`dbkit-test-postgres-logical`, `localhost:15433`, user `dbdatasync`), which the Postgres logical-decoding reader needs; the plain
`postgres` profile on 15432 that the other products test against does not have it. MySQL (13306), MariaDB (13307) and Oracle
(15210, service `FREEPDB1`) are the profiles `mysql`, `mariadb` and `oracle`; `up` with no names starts every engine.

Every engine uses the password `DbKit_Test_Pw1` (set `DBKIT_TEST_PASSWORD` before `up` to change it; it is read when the containers
are created) and the users `sa`, `root`, `dbdatasync` (PostgreSQL) and `dbkit` (Oracle). Nothing is kept between runs: `testenv/testenv.sh
down` removes the containers and their data, and every test creates its own databases and tables. `up` waits for each engine's health
check. `tools/dev-harness up` starts the three engines above itself.

The tests read where the engines are from `DBDATASYNC_TEST_*` variables; print them for what is running and load them into your shell:

```sh
eval "$(testenv/testenv.sh env dbdatasync)"
```

(`testenv/testenv.sh env dbdatasync --dotenv >> "$GITHUB_ENV"` in a GitHub Actions job.) Without them the tests use literal defaults, the
values of the old `DbDataSync/docker-compose.yml`: password `DbDataSync_Test_Pw1`, PostgreSQL on 15432. Those no longer match the shared
engines (password `DbKit_Test_Pw1`, PostgreSQL on 15433, Oracle user `dbkit`), so run the suites after the `eval` above. The variables:
`DBDATASYNC_TEST_MSSQL_SERVER` and `_MSSQL_SOURCE_SERVER` (14330), `_MSSQL_TARGET_SERVER` (14331), `_POSTGRES_SERVER` and
`_POSTGRES_JDBC_URL` (15433), `_MYSQL_SERVER`, `_MARIADB_SERVER`, `_ORACLE_SERVER`.

## 2. Build the solution

```sh
dotnet build
```

The API spawns `DbDataSync.TaskRunner` as a child process per run and locates its build output
relative to its own — build the whole solution at least once (not just `src/DbDataSync.Api`) before
running the API.

## 3. Run the API

```sh
dotnet run --project src/DbDataSync.Api
```

Listens on `http://localhost:5183`. On first run it creates a local `dbdatasync-repo/` directory
(under `src/DbDataSync.Api/`, gitignored) as its git-backed config store and SQLite state database —
no separate setup step needed. Override the location via the `DbDataSync__App__RepoRoot` and
`DbDataSync__State__DbPath` environment variables (or `appsettings.Development.json`) if you'd rather
keep it elsewhere. This is one of several ways to start DbDataSync — see
[Configuration](configuration.md) for the rest (the `dbdatasync` CLI, the Windows service, the
container image) and everything each one can be configured with, including authentication.

Secrets (connection passwords) are in the encrypted store under the repository root (`secrets/`, see "Secrets" in
[Configuration](configuration.md)); the environment variables `DBDATA_SECRET_DBDATA_SYNC_CONNECTION_<NAME>` (the connection name in capitals)
answer for a connection the store has no password for — set these before triggering a run if you would rather not store one locally.

## 4. Run the screens

The server serves no web app: the product's screens are the web app of the dbdatakit console (`DbDataKit/web/console`), which shows Sync's at `/servers/<id>/...`
once the server is paired with it ([Getting started](getting-started.md#open-the-server-in-the-console)). To work on the Sync screens themselves, run them on their own dev server against the API:

```sh
cd ../DbDataKit/web
npm install
npm run dev -w @dbdatakit/sync
```

Open `http://localhost:5173`. It proxies `/api` and `/hubs` to the API (`http://localhost:5183` by
default — override with the `DBDATASYNC_API_URL` environment variable). It has no docs and no console around it; for those, and for
what a person sees, run the console's dev server (`npm run dev -w @dbdatakit/console`, which proxies to a running console) or build it.

From here, [Getting started](getting-started.md) walks through the same screens regardless of how
DbDataSync was started.

## Running the tests

```sh
dotnet test --filter "Category!=Integration"   # fast, no external dependencies
dotnet test --filter "Category=Integration"    # needs the engines from step 1 and the eval of `testenv.sh env dbdatasync`
```

The Playwright end-to-end suite (`tests/DbDataSync.Web.Tests/`) drives the same golden-path
scenario as [Getting started](getting-started.md) through a real browser. Sync serves no web app of its
own, so the suite runs through the console: it starts a console and a real `dbdatasync server run`, signs
the console in as an administrator with a passkey, pairs the server, and opens the screens at
`/servers/<id>/...` in the console, whose relay carries their calls (`fixtures.ts` translates the specs'
`/replications/...` and `/api/...` addresses; the specs are written in the server's own). It also starts
its own scratch config repo, state database and disposable SQL Server test database. It needs the builds of
the API, the worker, the CLI and the console server, and the console's web app (which includes these docs):

```sh
dotnet build src/DbDataSync.Api && dotnet build src/DbDataSync.TaskRunner && dotnet build src/DbDataSync.Cli
(cd ../DbDataConsole && dotnet build src/DbDataConsole.Server)
(cd ../DbDataKit/web && npm ci && npm run build -w @dbdatakit/console)
cd tests/DbDataSync.Web.Tests
npm install
npx playwright install chromium
npx playwright test
```

The screens are shown inside the console's frame, so a run writes its pictures to a scratch folder and leaves the committed
`screenshots/` alone (`DBDATASYNC_E2E_SCREENSHOTS=repo` replaces them on purpose).

## Repository layout

- `src/DbDataSync.Api` — ASP.NET Core orchestrator: REST API, scheduler, process supervisor, SignalR
  live-run hub.
- `../DbDataKit/web/sync` — the React/TypeScript Sync screens (`@dbdatakit/sync`), shown by the console (`../DbDataKit/web/console`), which also serves these docs (generated from this folder).
- `src/DbDataSync.Core` — config models, git-backed config store, secrets.
- `src/DbDataSync.State` — shared SQLite state store (runs, logs, watermarks, locks).
- `src/DbDataSync.Drivers.Abstractions` — the driver interfaces and capability discovery.
- `src/DbDataSync.Drivers.Generic` — the engine-neutral implementations every driver gets for free: a
  SQL dialect, a watermark reader, a batch-reload reader, a batched-insert staging provider and a
  delete/insert writer. A new engine's driver is a dialect, a connection factory and a catalog.
- `src/DbDataSync.Drivers.MsSql` — SQL Server, with its own faster implementations (Change Tracking
  reader, `SqlBulkCopy` staging, MERGE writers) alongside the generic ones.
- `src/DbDataSync.Drivers.Postgres` — PostgreSQL, on Npgsql. Registers only the generic pipeline;
  batch and watermark mode, no CDC.
- `src/DbDataSync.TaskRunner` — the console process actually spawned per replication run.
- `tools/DbDataSync.DevHarness` — the dev harness above (environment setup, workload generation,
  drift injection, source/target verification — including SQL Server → PostgreSQL, via
  `--target-engine postgres`). Not part of the shipped product.
- `tools/DbDataSync.Benchmarks` — `tools/benchmarks`, which measures how much the in-memory shape of a
  change batch costs, through a real `SqlBulkCopy` and through a typed sink. Also not shipped.
- `architecture/` — the project's design and planning records, for contributors; it is not part of the
  product documentation in `docs/`.
