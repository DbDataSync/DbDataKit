# Building DbDataSync from source

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · **Building from source**

This is for working on DbDataSync itself — contributing, running the dev loop, or building it rather
than installing the packaged CLI. If you just want to run DbDataSync, see [Install](install.md)
instead; nothing here is needed for that.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
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

Every engine uses the password `DbKit_Test_Pw1` (set `DBDATAKIT_TEST_PASSWORD` before `up` to change it; it is read when the containers
are created) and the users `sa`, `root`, `dbdatasync` (PostgreSQL) and `dbkit` (Oracle). Nothing is kept between runs: `testenv/testenv.sh
down` removes the containers and their data, and every test creates its own databases and tables. `up` waits for each engine's health
check.

The tests read where the engines are from `DBDATAKIT_TEST_*` variables; print them for what is running and load them into your shell:

```sh
eval "$(testenv/testenv.sh env dbdatasync)"
```

(`testenv/testenv.sh env dbdatasync --dotenv >> "$GITHUB_ENV"` in a GitHub Actions job.) Without them the tests use literal defaults, the
values of the old `DbDataSync/docker-compose.yml`: password `DbDataSync_Test_Pw1`, PostgreSQL on 15432. Those no longer match the shared
engines (password `DbKit_Test_Pw1`, PostgreSQL on 15433, Oracle user `dbkit`), so run the suites after the `eval` above. The variables:
`DBDATAKIT_TEST_MSSQL` (14330), `_MSSQL_TARGET` (14331), `_POSTGRES_LOGICAL` and `_POSTGRES_LOGICAL_JDBC_URL` (15433), `_MYSQL`, `_MARIADB`,
`_ORACLE` (each a connection string; the names of the suite are in `docs/testing-environment.md`).

## 2. Build the solution

```sh
dotnet build
```

`DbDataSync.Cli` builds `dbdatasync`; `DbDataSync.TaskRunner` builds the engine, whose output is the package `dbdatasync-runtime` a Runner
runs (`scripts/runtime-manifest.sh` prints its manifest). To run a replication end to end, write the package with `dbdatasync package publish --to runner --out
<directory>` and send it to a Runner of your own (`dbdatarunner server run`, see the Runner's README) with the Runner client library
(`RunnerPublishClient` in `DbDataKit.Runner.Client`), as the console's servers suite does (`DbDataKit/tests/DbDataKit.Server.Servers.Tests`,
`SyncOnRunner`). The command line itself never sends a package to a Runner.

## Running the tests

```sh
dotnet test --filter "Category!=Integration"   # fast, no external dependencies
dotnet test --filter "Category=Integration"    # needs the engines from step 1 and the eval of `testenv.sh env dbdatasync`
```

## Repository layout

- `src/DbDataSync.Cli` — the `dbdatasync` command line.
- `../Shared/src/DbDataKit.Sync.Core` — config models, git-backed config store, secrets. It is a shared library because the Workspace reads a
  sync project with it too. The other libraries in `../Shared/src/DbDataKit.Sync.*` are `Drivers.Abstractions`, `Drivers.Generic`,
  `Drivers.Descriptor`, `Scripting.Abstractions` and `Provisioning`. Each library's namespace is its assembly's name (`DbDataKit.Sync.Core.Config`,
  `DbDataKit.Sync.Scripting.Abstractions`), and Sync's projects reference them with `<DbDataKitReference Include="Sync.Core" />`. Their tests are in
  `../Shared/tests/DbDataKit.Sync.*.Tests`.
- `src/DbDataSync.Drivers.Descriptor` — what loads a driver: the library behind a `driver.yaml` (`DriverDescriptorReader.BuildDriver`,
  `DriverLoader`) and the compiler of a `driver.csx` (`DriverScriptHost`). Reading a `driver.yaml` is the shared `DbDataKit.Sync.Drivers.Descriptor`.
- `src/DbDataSync.State` — the engine's records (drain requests, batches, positions) kept in a Runner's state.
- `src/DbDataSync.Scripting` — the script compiler and host, and the script test, schema browsing and opener of a configured connection a program composes.
- `../Shared/src/DbDataKit.Sync.Drivers.Abstractions` — the driver interfaces and capability discovery.
- `../Shared/src/DbDataKit.Sync.Drivers.Generic` — the engine-neutral implementations every driver gets for free: a
  SQL dialect, a watermark reader, a batch-reload reader, a batched-insert staging provider and a
  delete/insert writer. A new engine's driver is a dialect, a connection factory and a catalog.
- `src/DbDataSync.Drivers.MsSql` — SQL Server, with its own faster implementations (Change Tracking
  reader, `SqlBulkCopy` staging, MERGE writers) alongside the generic ones.
- `src/DbDataSync.Drivers.Postgres` — PostgreSQL, on Npgsql. Registers only the generic pipeline;
  batch and watermark mode, no CDC.
- `src/DbDataSync.TaskRunner` — the engine a Runner starts for the replications' tasks (`--runner`).
- `tools/DbDataSync.Benchmarks` — `tools/benchmarks`, which measures how much the in-memory shape of a
  change batch costs, through a real `SqlBulkCopy` and through a typed sink. Also not shipped.
- `architecture/` — the project's design and planning records, for contributors; it is not part of the
  product documentation in `docs/`.
