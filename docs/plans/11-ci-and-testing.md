# CI and testing across the three products

Status: proposed. Phase 0 is done (see "Rollout"); everything after it needs the decisions at the end.

The three products and the shared libraries overlap heavily on supported databases, and all need Linux and Windows
tests. Today each one starts its databases differently, none of that is shared, and the shared libraries' own live tests
never run in CI. This plan describes one test environment that all of them use, and the CI structure around it.
Branching and releases are in [09](09-MONOREPO-BRANCHING-AND-RELEASES.md); this is only about testing.

## What exists today

| | DbDataSync | DbDataProxy | DbDataBuild | DbDataKit (shared) |
| --- | --- | --- | --- | --- |
| how databases start | `docker-compose.yml`, fixed ports 14330/14331/15432/13306/13307/15210 | one GitHub `services:` SQL Server container, port 1433 | `scripts/test-engines.sh` (`docker run`, dynamic ports) | none |
| engines | SQL Server (×2: source, target), PostgreSQL 17 with logical replication (custom image), MySQL 9, MariaDB 11, Oracle Free 23 | SQL Server 2022 (SqlClient and ODBC), DuckDB, text ODBC | SQL Server 2022 and 2025, PostgreSQL, Oracle Free 23, Spark, BigQuery emulator, Arrow Flight | PostgreSQL, SQL Server, DuckDB, ODBC (env-gated live tests) |
| connection env vars | `DBDATASYNC_TEST_MSSQL_SERVER`, `…_POSTGRES_SERVER`, `…_MYSQL_SERVER`, … | `DBPROXY_TEST_SQLSERVER`, `…_USER`, `…_PASSWORD`, `DBPROXY_REQUIRE_SQLSERVER` | `DBDATABUILD_TEST_MSSQL`, `…_PG`, `…_ORACLE`, `…_SPARK`, … | `DBDATA_TEST_SQLSERVER`, `DBDATA_TEST_POSTGRES`, `DBDATA_TEST_ODBC` |
| Linux job | build + unit + `dotnet-integration` (compose) + Playwright | build + tests against the SQL Server container | unit on Linux and Windows; conformance matrix on Linux | **none** |
| Windows job | `Category!=Integration` + the Windows-only tests | build + tests + package smoke test | unit | **none** |
| nightly / full | no | no | weekly `conformance-full` (all engines) | no |

The same engines appear in several columns (SQL Server in all four, PostgreSQL in three, Oracle in two), started three
different ways with three different sets of variable names.

## Problems found, with evidence

1. **The shared libraries' suite had never run on Linux CI, and it had real Linux defects.** DbDataSync's job for it was
   always skipped and the Proxy's CI does not run it, so nothing noticed until a merge from `main` brought `DbDataKit/`
   changes into DbDataSync's lane and its own inline copy of the job (without unixODBC) failed on Ubuntu, which blocked
   promotion of `dbdatasync/test`. The first baseline of the shared job (`_dbdatakit.yml`) on GitHub's runners: **Windows
   passed; Linux had 3 failures in 1873**, plus 14 ODBC tests that only need unixODBC installed (the job now installs it).
   The three, and what they were:
   - `SqlServerFailureMapper` classified an unreachable server as `Other` on Linux. **A real defect:** on Linux and macOS
     Microsoft.Data.SqlClient reports every transport-level failure as error number 0, class 20, where Windows reports the
     socket error number. Fixed: number 0 with class 20 or higher is a network failure.
   - `ElevationDetector`'s test expected the Windows wording ("elevated"); elsewhere the message says to use sudo. A test
     assumption. Fixed.
   - The unknown-ODBC-driver test expected SQLSTATE `IM002` and a "word size" explanation; unixODBC reports `01000` for the
     same input. A test assumption (the unknown-DSN test, `IM002` on both, was fine). Fixed.

   A fourth defect showed up only on a machine using a distro-packaged .NET: `LibraryValidation.ValidateRuntimeIdentifier`
   rejected the host's own RID `ubuntu.24.04-x64` (it allowed one dotted version component, and distro RIDs like
   `ubuntu.24.04-x64` and `alpine.3.20-x64` have two), so planning any library install failed there. Fixed and tested.
2. **Live tests are skipped everywhere they could matter.** The shared libraries have 50–66 live-gated tests that skip
   because nothing provides the engines. The Proxy's Windows job skips its SQL Server tests (`.\SQLEXPRESS` is not
   reachable on `windows-latest`) and only the Linux job runs them.
3. **Tests assumed their platform.** Three Proxy tests fed Windows paths to code that treats them as relative on Linux
   (fixed in the Phase 0 PR); one asserted that an ordinary child process is in no Windows job object, which is false on
   any CI runner because the runner puts every step in one (also fixed).
4. **Duplicated work.** The shared libraries' tests run in more than one product's CI, and each product re-solves
   "install the ODBC driver", "wait for SQL Server", "which port".
5. **A flaky test was seen once and not identified** (PR #2: one failure on a first parallel run, green on rerun).
6. **Nobody can see what was skipped.** Only the Proxy sets `--minimum-expected-tests`; no job reports skipped counts.
7. **Inconsistent plumbing:** `actions/checkout`/`setup-dotnet` at v4 in some workflows and v7/v6 in others, different
   NuGet caching, different timeouts.

## Design

### A. One engine catalogue

A single `testenv/` directory at the repository root holds every database the tests use: one `docker-compose.yml` with a
**profile per engine** (`mssql`, `mssql2025`, `postgres`, `postgres-logical`, `mysql`, `mariadb`, `oracle`, `spark`,
`bigquery`, `flight`), with images pinned in one place, health checks, init scripts (the Oracle and PostgreSQL ones move
from DbDataSync), and fixed, documented host ports. One script drives it, the same way on a developer machine and in CI:

```
testenv/testenv.sh up mssql postgres     # start and wait for health
eval "$(testenv/testenv.sh env)"         # export connection variables for whatever is running
testenv/testenv.sh down
```

It replaces DbDataSync's compose file and DbDataBuild's `test-engines.sh` rather than adding a fourth way. Linux and
macOS only (Docker); Windows is covered in D.

### B. One set of connection variables

`testenv.sh env` emits canonical variables (`DBKIT_TEST_MSSQL`, `DBKIT_TEST_POSTGRES`, `DBKIT_TEST_ORACLE`, …) in each
engine's native connection-string form. Migration without touching test code first: each product gets a small mapping
file (`testenv/env/dbdatasync.sh`, …) that re-exports the canonical values under the names its suite already reads.
Suites move to the canonical names one at a time later, reading the canonical variable first and the old name as a
fallback, and the mapping files shrink and disappear.

### C. Shared workflows, not shared copies

- `_dbdatakit.yml` (added in Phase 0): the shared libraries' suite on Linux and Windows, called by any product whose
  change touches `DbDataKit/**`, and runnable by hand.
- `_dotnet-test.yml` (Phase 3): one reusable workflow that does checkout, SDK, NuGet cache, restore, build, test, trx
  upload and a skipped/failed summary, taking inputs for the product folder, solution or project, OS list, engine list,
  test filter and extra system packages (unixODBC, MS ODBC driver). Each product's CI becomes a handful of calls with its
  own engine list and filters, plus its extras (DbDataSync's Playwright, the Proxy's package smoke test).

All workflows use the same action versions and timeouts.

### D. Windows

`windows-latest` cannot run Linux containers, and the image has no SQL Server. So tests fall into three kinds:

1. **Platform behaviour**, which exists only on Windows (services, DPAPI, ACLs, the certificate store, the 32-bit worker,
   the text ODBC driver, job objects): run on `windows-latest` with no engine. Every product already has these.
2. **Engine behaviour**: run on Linux against the containers. This is where the shared catalogue pays off.
3. **A Windows driver against a real engine** (for example the Windows ODBC driver for SQL Server): the only kind with no
   obvious home. Options: install SQL Server Express on the runner (several minutes), or run it nightly, or accept
   Linux coverage plus a manual release check. See decision 1.

### E. Test kinds, filters and rules

One vocabulary, as xunit traits or categories, so the same filter means the same thing everywhere: `unit` (no external
dependency, the default), `integration` (needs an engine; the engine names it needs are in the trait), `platform` with
`windows` or `linux`, `e2e` (Playwright) and `conformance` (the same checks across engines). Rules for every job:
`--minimum-expected-tests`, a job timeout, test results uploaded as trx, and a summary that lists skipped tests by reason.
A test that is retried or quarantined carries an issue link; there is no silent retry. A job that expects engines fails
rather than skips when they are missing (the Proxy's `DBPROXY_REQUIRE_SQLSERVER` idea, generalised).

### F. Cadence

- **On every pull request into `main`, and every push to it:** unit tests on Linux and Windows, and the integration tests against the
  core engines (SQL Server, PostgreSQL), plus the product's own extras (the slow engines run after a merge and nightly).
- **Nightly, on each `<product>/test`:** the full engine matrix (MySQL, MariaDB, Oracle, SQL Server 2025, Spark,
  BigQuery, Flight), conformance, and the shared libraries' live tests against everything. Replaces DbDataBuild's weekly
  job and covers the other products.
- **Release:** requires a green CI run for the exact commit, and a green nightly (or a full run) for it too.
- Slow engines (Oracle's image alone takes minutes to become healthy) belong in the nightly unless a change touches
  their code; the `changes` job can select them by path.

### G. Cost control

Cancel superseded PR runs (not runs on `main`); each product's `changes` job keeps unrelated changes from running its suites; image pulls
and the NuGet cache are cached; the shared libraries' job runs once (`dbdatakit-ci.yml`), not once per product.

## Implemented: `testenv/` (phase 2, first slice)

`testenv/docker-compose.yml` has a profile per engine; so far `mssql` (port 14330) and `postgres` (port 15432).
`testenv/testenv.sh` drives it: `up [engines]`, `down`, `status`, and `env [product] [--dotenv]`.

- **Canonical variables**, each emitted only while its engine is running: `DBKIT_TEST_MSSQL`, `DBKIT_TEST_MSSQL_SERVER`,
  `_USER`, `_PASSWORD`, `_ODBC`; `DBKIT_TEST_POSTGRES`, `DBKIT_TEST_POSTGRES_HOST`, `_PORT`, `_USER`, `_PASSWORD`, `_DB`.
- **Mapping files** (`testenv/env/<product>.map`) re-export the canonical values under the names a suite already reads.
  Line forms: `TARGET <- SOURCE` and `TARGET = literal if SOURCE`. Present: `dbdataproxy`, `dbdatakit`.
- `testenv/selftest.sh` checks the generation and mappings without Docker (17 checks).
- Wired into: the Proxy Linux job (replaces its `services:` container) and `_dbdatakit.yml` (opt-in `engines` input).
  A manual dispatch with `engines=true` was green on CI, so `dbdatakit-ci.yml` runs the shared suite with the engines on for every pull
  request and push to `main` that touches `DbDataKit/` or `testenv/`.
- Every workflow job now has a `timeout-minutes`.
- Both containers speak TLS with throwaway self-signed certificates (the Postgres one generates its own at start), so the live
  tests run with the libraries' secure default (encrypt) and certificate verification switched off, as a developer's local
  server would be. With the engines on, the shared libraries' suite is 1873 passed, 0 failed, 35 skipped locally. The skips are the
  `DBDATA_CONFORMANCE_*` live dialect tests, which are stubs that throw if their variable is set (no driver is wired into
  `StubLive.Connect`), so testenv deliberately does not set them.
- The live-test failures found when `engines` was first tried were test-side: the ODBC live test did not trust the self-signed
  SQL Server certificate, the Postgres container had no TLS to offer the encrypt-by-default provider, and the conformance
  runner rendered SqlClient's `DateTime` for a `date` column with a time part (it now renders by the column's canonical type).

## Rollout

| phase | what | state |
| --- | --- | --- |
| 0 | Proxy CI and release workflows, `_dbdatakit.yml`, Proxy test fixes (the lanes and the `merge-release` action built here were later retired for the trunk model in plan 09) | done |
| 1 | Baseline of `_dbdatakit.yml` on Linux and Windows, and fix what it found (above); DbDataSync's CI calls the shared job instead of its own copy | done in the PR after this one |
| 2 | `testenv/`: port DbDataSync's compose and DbDataBuild's engines, canonical variables and mapping files; wire the Proxy and the shared libraries first, since they have the least to migrate; the shared libraries' live tests start running | needs decisions 2, 3 |
| 3 | `_dotnet-test.yml`; move each product's CI onto it; skipped-test summary; one action version | |
| 4 | Nightly full matrix on each `*/test`; release gate on it | needs decision 4 |
| 5 | DbDataBuild moves in; its CI, nightly full conformance and release are on the trunk model (plan 09) | done; its engines are still started by `DbDataBuild/scripts/test-engines.sh`, not `testenv/` |

## Decisions needed

1. **Windows + real engine (D.3).** Install SQL Server Express on the runner for the per-push run, run it nightly only,
   or rely on Linux coverage plus a manual check at release?
2. **Variable names.** Mapping files first and canonical names later (recommended, no test code changes up front), or
   rename every suite at once?
3. **Which engines gate every push.** Recommended: SQL Server and PostgreSQL on every push; MySQL, MariaDB, Oracle and the
   rest nightly or when their code changes. Is Oracle in the per-push set for DbDataSync?
4. **Release gate.** Require a green nightly for the exact commit, or run the full matrix inside the release?
5. **The shared libraries' live tests.** Once the engines exist they become part of every lane's gate when `DbDataKit/**`
   changes. Acceptable, or nightly only?
