# CI and testing

Status: in progress. The shared test environment and the per-product CI are built; what is left is nightly runs, a Windows job with a real
engine, skip visibility and a release gate. Branching and releases are in [docs/releases.md](../releases.md).

## What exists

- **One engine catalogue, `testenv/`.** `docker-compose.yml` has a profile per engine, fixed ports, images pinned in one place:
  `mssql`, `mssql-target`, `postgres`, `postgres-logical`, `mysql`, `mariadb`, `oracle` (what a bare `up` starts); `mssql2025`, `spark`,
  `bigquery`, `flight`, `flight-tls` (the slow ones, Build's conformance suite only; `up all` starts them); `mssql2022` and `postgres16`
  (the previous release of each state engine, N-1, left out of a bare `up`). `testenv/testenv.sh` has `up`, `down`, `status`, `env`;
  `testenv/selftest.sh` checks the generation without Docker.
- **Connection variables.** `testenv.sh env <product>` emits canonical `DBKIT_TEST_*` variables for what is running and applies the
  product's mapping file in `testenv/env/` (`dbdatakit`, `dbdatakit-older` for the N-1 engines, `dbdataproxy`, `dbdatasync`, `dbdatabuild`),
  which re-exports them under the names that suite reads.
- **Build's engines live in `testenv/`** (SQL Server 2022 and 2025, PostgreSQL, Oracle, Spark, the BigQuery emulator, Arrow Flight with and
  without TLS); `DbDataBuild/scripts/test-engines.sh` is gone.
- **Per-product workflows** in `.github/workflows/`: `dbdatakit-ci.yml` (the shared libraries, once, on Linux and Windows, with SQL Server and
  PostgreSQL; a job on the N-1 engines for State and Scheduling; a DAX/SSAS job), `_dbdatakit.yml` (its reusable form), `dbdatasync-ci.yml`
  (Linux, Windows, `dotnet-integration` on six engines, Playwright), `dbdataproxy-ci.yml` (Linux with SQL Server and the ODBC driver,
  Windows without an engine), `dbdatabuild-ci.yml` (unit on Linux and Windows, runtime package on Linux and Windows, conformance groups on
  SQL Server and PostgreSQL), `dbdataconsole-ci.yml`, `dbdatarunner-ci.yml` and `dbdatastore-ci.yml` (SQLite plus an engines job; Linux
  only), `_changes.yml` (a job skipped by it still reports success), `repo-checks.yml`. Every job has a timeout; superseded pull request runs
  are cancelled.
- **Nightly:** only `dbdatabuild-conformance-full.yml` (daily 05:00, skipped when nothing of Build's or `testenv/` changed; also called by
  `dbdatabuild-release.yml`, which `suite-release.yml` calls).
- **Playwright:** the console's suite (`DbDataConsole.Web.Tests`, 12 specs, which also walks Build and Proxy through the console) and Sync's
  (`DbDataSync.Web.Tests`, 24 specs). Build's own suite (`DbDataBuild.Web.Tests`, 4 specs) reaches its page through `dbdatabuild ui mcp`. Specs run one at a time in the order of their file names.
- **ODBC:** installed by steps in the workflows (`unixodbc`, `msodbcsql18`); `scripts/install-odbc.sh` does the same on a Debian or Ubuntu machine (a cloud session) and the workflows could call it.
- **`--minimum-expected-tests`:** set in the Proxy, Console, Runner and Store jobs only.
- **`docs/ci-lessons.md`:** the problems already found, how to recognise them, and the "before you push" list.

## Decisions (owner)

1. **Windows with a real engine:** a nightly job, with SQL Server Express installed through Chocolatey. Not on every push.
2. **Variable names:** mapping files first, canonical names later (a suite reads the canonical variable first and the old one as a fallback;
   the mapping files shrink and go).
3. **Which engines gate every push:** SQL Server and PostgreSQL. MySQL, MariaDB, Oracle and the rest run when their code changes, and nightly.
4. **Release gate:** the full matrix runs inside the release (as Build's already does), not a lookup of a green nightly.
5. **The shared libraries' live tests** gate every pull request that touches them (`DbDataKit/**`, `testenv/`).

## Remaining work

| Id | Slice | Done when |
| --- | --- | --- |
| CI01 | A nightly workflow for the products other than Build: the full engine matrix for Sync (its six engines), the shared libraries' live tests on every engine, Proxy, Console, Runner and Store with their engines jobs, skipped when nothing changed in the last day, like Build's | the workflow runs on schedule on `main` and by dispatch; a run with a deliberately broken engine variable fails the matching job; `docs/ci-lessons.md` says where to look |
| CI02 | The Windows nightly: SQL Server Express installed with Chocolatey on a Windows runner, the shared libraries' and Proxy's engine tests (SqlClient and the Windows ODBC driver) run against it, and the Runner gets a Windows job beside its Windows packaging | the nightly runs the Proxy's SQL Server tests on Windows with none skipped (`.\SQLEXPRESS` is reached), and a Runner Windows job is green |
| CI03 | Skipped-test summary and `--minimum-expected-tests` on every .NET test job (Kit, Sync, Build, and the Windows jobs, not only Proxy, Console, Runner, Store) | each job's summary lists skipped tests by reason, and a job whose test count falls below its floor fails (checked by deleting a test project from a branch) |
| CI04 | Engine jobs fail instead of skipping when their engines are expected: the `DBPROXY_REQUIRE_SQLSERVER` idea as one variable every engine-gated test reads (Kit, Sync, Build) | with `testenv` down, the engine jobs fail naming the missing engine; a test gated on an engine that is not expected still skips |
| CI05 | The test-kind vocabulary applied: `unit`, `integration` (engine names in the trait), `platform` with `windows` or `linux`, `e2e`, `conformance`, as one trait or category; today Kit's tests use `Category` only in the four Windows certificate classes | every job selects its tests by that vocabulary, a check lists test classes that need an engine or a platform and have no trait, and the filters read the same in every product |
| CI06 | The release gate for the other products: `dbdataproxy-release.yml`, `dbdatasync-release.yml` and the console's image build call the full matrix of CI01 for the exact commit, as `dbdatabuild-release.yml` calls Build's | a release of each product starts the full matrix first and stops when it fails; the run is linked from the release |

Canonical variable names (decision 2) are not a slice: a suite moves when it is touched.
