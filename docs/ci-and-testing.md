# CI and testing

How the suite's tests and CI are organised. Pitfalls found and fixed are in [ci-lessons.md](ci-lessons.md) (read its "before you push" list before pushing).
Branching and releases are in [releases.md](releases.md).

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

## Rules

1. **Variable names:** a suite reads the canonical `DBKIT_TEST_*` variable first and its old name as a fallback; the mapping files in `testenv/env/` re-export the canonical names under the old ones.
2. **Which engines gate every push:** SQL Server and PostgreSQL. MySQL, MariaDB, Oracle and the rest run when their code changes (and Build's conformance nightly).
3. **The shared libraries' live tests** gate every pull request that touches them (`DbDataKit/**`, `testenv/`).

## Not built (add a task when it becomes an issue)

A nightly run for the products other than Build, a Windows nightly with a real SQL Server (Express), skipped-test summaries and `--minimum-expected-tests` on every .NET job, engine jobs that fail instead of skipping, a single test-kind vocabulary, and the full engine matrix inside every product's release. They were dropped from the plan on 2026-11-09 because there are enough CI tasks.
