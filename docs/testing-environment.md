# The environment of the tests

Every variable that only a test, a harness, a CI step or a developer sets or reads is named `DBDATAKIT_TEST_<NAME>`. No other name with a
product prefix exists (`scripts/check_env_names.py` fails on one, in every file but this page, the plan and the release notes), and nothing falls
back to an old name: a machine that still sets one has an unused variable. The refusal of a service covers the prefixes `DBGATEWAY_` and `DBPROXY_`
(settings of the Gateway) with no exception for tests: a leftover `DBGATEWAY_TEST_...` in a service's environment is refused like any other old
Gateway setting.

`testenv/testenv.sh env [product] [--dotenv]` prints the connection variables of the engines that run (one name for one meaning), and the
product's map in `testenv/env/` adds the few names a suite reads that this set does not have. The rule for a name:

- the old name without its product prefix (`DBDATA_`, `DBDATASYNC_`, `DBDATABUILD_`, `DBGATEWAY_`, `DBPROXY_`, `DBKIT_`) and without a duplicate `TEST`,
  behind `DBDATAKIT_TEST_`: `DBDATA_E2E_PG` is `DBDATAKIT_TEST_E2E_PG`, `DBGATEWAY_LIVE_USER` is `DBDATAKIT_TEST_LIVE_USER`;
- two old names with the same meaning and value are one name: the connection string of the first SQL Server is `DBDATAKIT_TEST_MSSQL` for every
  suite (it was `DBKIT_TEST_MSSQL`, `DBDATABUILD_TEST_MSSQL` and Sync's `..._MSSQL_SERVER`), its `host,port` is `DBDATAKIT_TEST_MSSQL_SERVER`;
- `DBDATAKIT_TEST_SQLSERVER` and `DBDATAKIT_TEST_SQLSERVER2022` (the engine the State suites run on) stay apart from `DBDATAKIT_TEST_MSSQL` and
  `DBDATAKIT_TEST_MSSQL2022`, because the `dbdatakit-older` map points them at SQL Server 2022 while the engines of the current release may
  run too. `DBDATAKIT_TEST_POSTGRES` is both: with `dbdatakit-older` it is PostgreSQL 16, so start only the older engines for that run
  (as `CLAUDE.md` says).

| Old name(s) | New name |
| --- | --- |
| `DBDATA_ADMIN_TESTS`, `DBGATEWAY_ADMIN_TESTS` | `DBDATAKIT_TEST_ADMIN_TESTS` |
| `DBDATA_APP_STORE` | `DBDATAKIT_TEST_APP_STORE` |
| `DBDATA_TEST_ARTIFACTS_PATH` | `DBDATAKIT_TEST_ARTIFACTS_PATH` |
| `DBDATA_BENCH` | `DBDATAKIT_TEST_BENCH` |
| `DBDATA_BENCH_MAX_ROWS` | `DBDATAKIT_TEST_BENCH_MAX_ROWS` |
| `DBDATA_BENCH_OUT` | `DBDATAKIT_TEST_BENCH_OUT` |
| `DBDATABUILD_TEST_BIGQUERY`, `DBKIT_TEST_BIGQUERY` | `DBDATAKIT_TEST_BIGQUERY` |
| `DBDATA_TEST_CHROME_SYSTEM` | `DBDATAKIT_TEST_CHROME_SYSTEM` |
| `DBDATA_TEST_CHROMIUM` | `DBDATAKIT_TEST_CHROMIUM` |
| `DBDATA_TEST_CONFIGURATION` | `DBDATAKIT_TEST_CONFIGURATION` |
| `DBDATA_CONFORMANCE_DUCKDB` | `DBDATAKIT_TEST_CONFORMANCE_DUCKDB` |
| `DBDATA_CONFORMANCE_MYSQL` | `DBDATAKIT_TEST_CONFORMANCE_MYSQL` |
| `DBDATA_CONFORMANCE_ORACLE` | `DBDATAKIT_TEST_CONFORMANCE_ORACLE` |
| `DBDATA_CONFORMANCE_POSTGRES` | `DBDATAKIT_TEST_CONFORMANCE_POSTGRES` |
| `DBDATA_CONFORMANCE_SQLSERVER` | `DBDATAKIT_TEST_CONFORMANCE_SQLSERVER` |
| `DBGATEWAY_TEST_DOES_NOT_EXIST` | `DBDATAKIT_TEST_DOES_NOT_EXIST` |
| `DBDATASYNC_DUCKDB_ENGINE_SOURCE` | `DBDATAKIT_TEST_DUCKDB_ENGINE_SOURCE` |
| `DBDATA_E12E_KEEP` | `DBDATAKIT_TEST_E12E_KEEP` |
| `DBDATA_E23_ERP_PASSWORD` | `DBDATAKIT_TEST_E23_ERP_PASSWORD` |
| `DBGATEWAY_E2E_ADDRESS` | `DBDATAKIT_TEST_E2E_ADDRESS` |
| `DBDATA_E2E_GATEWAY_DLL` | `DBDATAKIT_TEST_E2E_GATEWAY_DLL` |
| `DBGATEWAY_E2E_LOG_LINES` | `DBDATAKIT_TEST_E2E_LOG_LINES` |
| `DBGATEWAY_E2E_PASSWORD` | `DBDATAKIT_TEST_E2E_PASSWORD` |
| `DBDATA_E2E_PG` | `DBDATAKIT_TEST_E2E_PG` |
| `DBGATEWAY_E2E_PORT` | `DBDATAKIT_TEST_E2E_PORT` |
| `DBGATEWAY_E2E_REQUIRED` | `DBDATAKIT_TEST_E2E_REQUIRED` |
| `DBDATASYNC_E2E_SCREENSHOTS` | `DBDATAKIT_TEST_E2E_SCREENSHOTS` |
| `DBDATA_E2E_STORE_DLL` | `DBDATAKIT_TEST_E2E_STORE_DLL` |
| `DBGATEWAY_E2E_USER` | `DBDATAKIT_TEST_E2E_USER` |
| `DBDATABUILD_TEST_FLIGHT`, `DBKIT_TEST_FLIGHT` | `DBDATAKIT_TEST_FLIGHT` |
| `DBDATABUILD_TEST_FLIGHT_TLS`, `DBKIT_TEST_FLIGHT_TLS` | `DBDATAKIT_TEST_FLIGHT_TLS` |
| `DBDATABUILD_TEST_FLIGHT_CA`, `DBKIT_TEST_FLIGHT_TLS_CA` | `DBDATAKIT_TEST_FLIGHT_TLS_CA` |
| `DBDATASYNC_HARNESS_TABLES` | `DBDATAKIT_TEST_HARNESS_TABLES` |
| `DBDATASYNC_HARNESS_TARGET_ENGINE` | `DBDATAKIT_TEST_HARNESS_TARGET_ENGINE` |
| `DBGATEWAY_LIVE_ADDRESS` | `DBDATAKIT_TEST_LIVE_ADDRESS` |
| `DBGATEWAY_LIVE_PASSWORD` | `DBDATAKIT_TEST_LIVE_PASSWORD` |
| `DBGATEWAY_LIVE_USER` | `DBDATAKIT_TEST_LIVE_USER` |
| `DBDATASYNC_TEST_MARIADB_SERVER`, `DBKIT_TEST_MARIADB` | `DBDATAKIT_TEST_MARIADB` |
| `DBDATASYNC_MATRIX_UPDATE` | `DBDATAKIT_TEST_MATRIX_UPDATE` |
| `DBDATA_METADATA_ODBC` | `DBDATAKIT_TEST_METADATA_ODBC` |
| `DBDATA_METADATA_POSTGRES` | `DBDATAKIT_TEST_METADATA_POSTGRES` |
| `DBDATA_METADATA_POSTGRES_PROVIDER` | `DBDATAKIT_TEST_METADATA_POSTGRES_PROVIDER` |
| `DBDATA_METADATA_SQLSERVER` | `DBDATAKIT_TEST_METADATA_SQLSERVER` |
| `DBDATA_METADATA_SQLSERVER_PROVIDER` | `DBDATAKIT_TEST_METADATA_SQLSERVER_PROVIDER` |
| `DBDATABUILD_TEST_MSSQL`, `DBDATASYNC_TEST_MSSQL_SERVER`, `DBDATASYNC_TEST_MSSQL_SOURCE_SERVER`, `DBKIT_TEST_MSSQL` | `DBDATAKIT_TEST_MSSQL` |
| `DBDATABUILD_TEST_MSSQL2025`, `DBKIT_TEST_MSSQL2025` | `DBDATAKIT_TEST_MSSQL2025` |
| `DBDATA_TEST_ODBC`, `DBKIT_TEST_MSSQL_ODBC` | `DBDATAKIT_TEST_MSSQL_ODBC` |
| `DBDATA_TEST_SQLSERVER_PASSWORD`, `DBGATEWAY_TEST_SQLSERVER_PASSWORD`, `DBKIT_TEST_MSSQL_PASSWORD`, `DBPROXY_TEST_SQLSERVER_PASSWORD` | `DBDATAKIT_TEST_MSSQL_PASSWORD` |
| `DBDATA_TEST_SQLSERVER_SERVER`, `DBGATEWAY_TEST_SQLSERVER`, `DBKIT_TEST_MSSQL_SERVER` | `DBDATAKIT_TEST_MSSQL_SERVER` |
| `DBDATASYNC_TEST_MSSQL_TARGET_SERVER`, `DBKIT_TEST_MSSQL_TARGET` | `DBDATAKIT_TEST_MSSQL_TARGET` |
| `DBDATA_TEST_SQLSERVER_USER`, `DBGATEWAY_TEST_SQLSERVER_USER`, `DBKIT_TEST_MSSQL_USER` | `DBDATAKIT_TEST_MSSQL_USER` |
| `DBDATASYNC_TEST_MYSQL_SERVER`, `DBKIT_TEST_MYSQL` | `DBDATAKIT_TEST_MYSQL` |
| `DBDATASYNC_TEST_NETWORK` | `DBDATAKIT_TEST_NETWORK` |
| `DBDATA_TEST_NOSANDBOX` | `DBDATAKIT_TEST_NOSANDBOX` |
| `DBDATABUILD_TEST_ODBC_PG_DRIVER` | `DBDATAKIT_TEST_ODBC_PG_DRIVER` |
| `DBDATASYNC_TEST_ORACLE_SERVER`, `DBKIT_TEST_ORACLE` | `DBDATAKIT_TEST_ORACLE` |
| `DBDATABUILD_TEST_ORACLE`, `DBKIT_TEST_ORACLE_KV` | `DBDATAKIT_TEST_ORACLE_KV` |
| `DBDATABUILD_TEST_PG`, `DBDATA_TEST_POSTGRES`, `DBGATEWAY_TEST_POSTGRES`, `DBKIT_TEST_POSTGRES` | `DBDATAKIT_TEST_POSTGRES` |
| `DBDATA_TEST_POSTGRES16`, `DBKIT_TEST_POSTGRES16` | `DBDATAKIT_TEST_POSTGRES16` |
| `DBDATASYNC_TEST_POSTGRES_SERVER`, `DBKIT_TEST_POSTGRES_LOGICAL` | `DBDATAKIT_TEST_POSTGRES_LOGICAL` |
| `DBDATASYNC_TEST_POSTGRES_JDBC_URL`, `DBKIT_TEST_POSTGRES_LOGICAL_JDBC_URL` | `DBDATAKIT_TEST_POSTGRES_LOGICAL_JDBC_URL` |
| `DBDATA_TEST_PRINT_ASSETS` | `DBDATAKIT_TEST_PRINT_ASSETS` |
| `DBDATABUILD_TEST_PUBLISHED` | `DBDATAKIT_TEST_PUBLISHED` |
| `DBDATA_TEST_PUBLISHED_PACKAGE` | `DBDATAKIT_TEST_PUBLISHED_PACKAGE` |
| `DBDATA_REGENERATE_WIDGET_RESULTS` | `DBDATAKIT_TEST_REGENERATE_WIDGET_RESULTS` |
| `DBDATA_REQUIRE_APP_STORE` | `DBDATAKIT_TEST_REQUIRE_APP_STORE` |
| `DBDATA_REQUIRE_BUN` | `DBDATAKIT_TEST_REQUIRE_BUN` |
| `DBDATA_REQUIRE_CHROMIUM` | `DBDATAKIT_TEST_REQUIRE_CHROMIUM` |
| `DBDATA_REQUIRE_NETNS` | `DBDATAKIT_TEST_REQUIRE_NETNS` |
| `DBDATA_REQUIRE_POSTGRES`, `DBGATEWAY_REQUIRE_POSTGRES` | `DBDATAKIT_TEST_REQUIRE_POSTGRES` |
| `DBDATA_REQUIRE_POSTGRES16` | `DBDATAKIT_TEST_REQUIRE_POSTGRES16` |
| `DBDATA_REQUIRE_SQLSERVER`, `DBGATEWAY_REQUIRE_SQLSERVER`, `DBPROXY_REQUIRE_SQLSERVER` | `DBDATAKIT_TEST_REQUIRE_SQLSERVER` |
| `DBDATA_REQUIRE_SQLSERVER2022` | `DBDATAKIT_TEST_REQUIRE_SQLSERVER2022` |
| `DBDATA_REQUIRE_SSAS` | `DBDATAKIT_TEST_REQUIRE_SSAS` |
| `DBDATA_SNAPSHOT_FILE` | `DBDATAKIT_TEST_SNAPSHOT_FILE` |
| `DBDATABUILD_TEST_SPARK`, `DBKIT_TEST_SPARK` | `DBDATAKIT_TEST_SPARK` |
| `DBDATA_TEST_SQLSERVER` | `DBDATAKIT_TEST_SQLSERVER` |
| `DBDATA_TEST_SQLSERVER2022` | `DBDATAKIT_TEST_SQLSERVER2022` |
| `DBDATA_TEST_SSAS` | `DBDATAKIT_TEST_SSAS` |
| `DBDATA_TEST_SSAS_PROBE` | `DBDATAKIT_TEST_SSAS_PROBE` |
| `DBDATA_TEST_SSAS_RECORD` | `DBDATAKIT_TEST_SSAS_RECORD` |
| `DBDATA_UPDATE_GOLDEN`, `DBGATEWAY_UPDATE_GOLDEN` | `DBDATAKIT_TEST_UPDATE_GOLDEN` |
| `DBKIT_UPDATE_PROTOCOL_SNAPSHOT` | `DBDATAKIT_TEST_UPDATE_PROTOCOL_SNAPSHOT` |
| `DBDATA_UPDATE_SNAPSHOTS` | `DBDATAKIT_TEST_UPDATE_SNAPSHOTS` |
| `DBDATA_XMLA_RECORDING_DIR` | `DBDATAKIT_TEST_XMLA_RECORDING_DIR` |
| every other `DBKIT_TEST_<X>`: `BIGQUERY_IMAGE`, `CACHE`, `FLIGHT_IMAGE`, `MSSQL2022`, `MSSQL2022_IMAGE`, `MSSQL2022_PASSWORD`, `MSSQL2022_SERVER`, `MSSQL2022_USER`, `MSSQL2025_IMAGE`, `MSSQL_IMAGE`, `MSSQL_TARGET_PASSWORD`, `MSSQL_TARGET_SERVER`, `MSSQL_TARGET_USER`, `MYSQL_HOST`, `MYSQL_PORT`, `ORACLE_HOST`, `ORACLE_PASSWORD`, `ORACLE_PORT`, `ORACLE_SERVICE`, `ORACLE_USER`, `PASSWORD`, `POSTGRES16_DB`, `POSTGRES16_HOST`, `POSTGRES16_IMAGE`, `POSTGRES16_PASSWORD`, `POSTGRES16_PORT`, `POSTGRES16_USER`, `POSTGRES_DB`, `POSTGRES_HOST`, `POSTGRES_IMAGE`, `POSTGRES_LOGICAL_DB`, `POSTGRES_LOGICAL_HOST`, `POSTGRES_LOGICAL_PASSWORD`, `POSTGRES_LOGICAL_PORT`, `POSTGRES_LOGICAL_USER`, `POSTGRES_PASSWORD`, `POSTGRES_PORT`, `POSTGRES_USER`, `PULL`, `SPARK_IMAGE` | `DBDATAKIT_TEST_<X>` |

Left out on purpose: `DBDATASYNC_MSSQL_SA_PASSWORD` and `DBDATASYNC_POSTGRES_PASSWORD` (variables of a `docker-compose.yml` that is gone; the Sync dev
harness no longer refuses them: the password is `DBDATAKIT_TEST_PASSWORD`). GitHub secrets and repository variables keep their names.

## The file servers and object stores (plan 27a IX04 to IX06)

`testenv/testenv.sh up sftp ssh ftps` starts three small servers for `DbDataKit.Connections.Files.Tests` (not part of a bare `up`): `sftp` (`atmoz/sftp`, SFTP only, port 13022, folder `/upload`, a key pair made in the cache folder), `ssh` (`linuxserver/openssh-server`, commands and `scp` allowed, port 13023, folder `/config`) and `ftps` (`stilliard/pure-ftpd`, explicit TLS required, port 13021 and passive ports 30000 to 30009, a certificate it makes for `localhost`). Variables: `DBDATAKIT_TEST_SFTP_HOST/_PORT/_USER/_PASSWORD/_ROOT/_KEY_FILE`, `DBDATAKIT_TEST_SSH_HOST/_PORT/_USER/_PASSWORD/_ROOT`, `DBDATAKIT_TEST_FTPS_HOST/_PORT/_USER/_PASSWORD/_CERT_SHA256`. `testenv/testenv.sh up s3 azurite` adds the object stores (IX06): `s3` (Adobe's S3Mock, port 13090, bucket `dbkit`, it does not check signatures) and `azurite` (Microsoft's Azure Storage emulator, blob service on port 13100, the well-known account `devstoreaccount1`, it checks every Shared Key signature): `DBDATAKIT_TEST_S3_ENDPOINT/_ACCESS_KEY/_SECRET_KEY/_BUCKET`, `DBDATAKIT_TEST_AZURITE_ENDPOINT/_ACCOUNT/_KEY/_CONTAINER`. The tests skip (saying what to start) without them; `DBDATAKIT_TEST_REQUIRE_FILE_SERVERS=true` makes a missing variable a failure. In a cloud session pull each image through the mirror and tag it (`docker pull mirror.gcr.io/atmoz/sftp:alpine && docker tag mirror.gcr.io/atmoz/sftp:alpine atmoz/sftp:alpine`, the same for `linuxserver/openssh-server:latest` and `stilliard/pure-ftpd:latest`, `adobe/s3mock:latest`; Azurite pulls from `mcr.microsoft.com` directly), then `DBDATAKIT_TEST_PULL=false testenv/testenv.sh up sftp ssh ftps s3 azurite`. `testenv.sh down` removes every testenv container on the machine, other sessions' included: when sharing a machine remove only yours (`docker rm -f dbkit-test-sftp ...`).

## Building without JDBC (`SkipJdbc`)

`DBDATAKIT_TEST_SKIP_JDBC=true` (or `-p:SkipJdbc=true`) leaves JDBC and IKVM out of the Sync build: `DbDataSync.Api` and `DbDataSync.TaskRunner` do not
reference `DbDataSync.Drivers.Jdbc`, `DbDataSync.Api.Tests` has no IKVM, no pgJDBC jar and does not compile `JdbcTestQuery*` (the define `SKIP_JDBC` guards the
rest), and `DbDataSync.Drivers.Jdbc` and `.Jdbc.Tests` build nothing (a message says so). No IKVM package is restored. Off by default, so CI is unchanged.
Build the Sync projects one by one with it (not a solution that needs the Jdbc projects to produce output): `dotnet build DbDataSync/tests/DbDataSync.Api.Tests`.

## Fast verifier runs

A change is checked by the suites it can affect, not by all of them. What exists, from the cheapest cut to the most precise:

- **Leave out the engines and the shell-outs by category.** VSTest projects (Sync, Build unit): `dotnet test --filter "Category!=Integration"`; add `&Category!=Slow` to leave out the tests tagged `[Trait("Category","Slow")]` (a test that waits for a timeout or shells out; CI still runs them, it does not pass this filter). Microsoft.Testing.Platform projects (Kit, Shared, Gateway, Console, Runner, Store, Workspace; xunit v3) take trait filters on the built executable: `--filter-not-trait "Category=Integration"`, `--filter-not-trait "Category=Slow"` (xunit v3 option names; run the executable with `--help` to confirm for the installed version).
- **One class or one method.** MTP: `--filter-class "*Name"`, `--filter-method "*Name*"`; the xunit v3 in-process runner: `-class` and `-method`; VSTest: `--filter FullyQualifiedName~Name`. After a build, run the executable (`tests/<Name>/bin/Debug/net10.0/<Name>`) so no second build and no test-host handshake is needed.
- **No JDBC and no IKVM** (about 4 GB of packages): `DBDATAKIT_TEST_SKIP_JDBC=true`, see above. `scripts/tests/test_skip_jdbc.py` checks that the projects named there keep their conditions.
- **One build at a time, no compile server left behind.** When several agents share a machine and a lock around `dotnet`, add `-p:UseSharedCompilation=false` and keep `MSBUILDDISABLENODEREUSE=1`: the Roslyn server (`VBCSCompiler`) a build starts inherits the lock's file descriptor and holds `flock` for about 10 minutes after the build ended, which starves every waiter (observed: a queue of 12 commands blocked 40 minutes behind an idle compile server).
- Delete `bin/` and `obj/` (and `node_modules/`) of a project after its run when disk is tight; `dotnet clean` leaves most of it.

### The slowest groups

Measured on all the `DbDataSync` .NET suites under `SkipJdbc` (VSTest `--logger trx`; the run time is what the runner reports, the wall time is longer by the restore and the build):

| Group | Run time | How to skip it |
| --- | --- | --- |
| `DbDataSync.Api.Tests` as a whole (619 tests, 23 skipped, Windows-only ones among them) | 45 s | `-class` of the tests you changed |
| `QualityEndpointTests.A_run_evaluates_the_replications_checks_and_records_a_result_even_when_the_target_cannot_be_reached` | 16 s (waits out an unreachable target) | `--filter "Category!=Slow"` (tagged) |
| `RunnerOwnedRunsTests.A_line_of_a_run_on_the_runner_reaches_the_hub_within_a_second_of_being_written` | 3.2 s | `-class` |
| `ScriptTestServiceTests` | 2.3 s in total | `-class` |
| `DbDataSync.Certificates.Tests` (20 tests) | 4 s | |
| `TaskRunner.Tests` `RunExecutorTests` (many tests of 15 to 17 s each; `ExecuteWorkerAsync_*` spawn the worker) | 135 s of 150 s | `--filter "FullyQualifiedName!~RunExecutorTests"` |
| `Cli.Tests` `ReadinessChecksTests` (one test, 19.7 s, installs every built-in library) | 32 s | `-class` of what you changed |
| `Cli.Tests` `SetupStepsTests` (`ApplyStateDatabase_*` install a library, 15 s) | 30 s | `-class` |
| `State.Tests` `StateDatabaseFactoryTests` | 29 s | `-class` |
| `Cli.Tests` `TabWiringTests` | 14.5 s | `-class` |
| `Cli.Tests` `PackagePublishOnRealRunnerTests` | 9.4 s | `-class` |
| `TaskRunner.Tests` `DrainLoopTests` | 7 s | `-class` |
| `Libraries.Tests` `LibraryCompatibilityTests` | 5.5 s | `-class` |

Whole projects under `SkipJdbc` with `Category!=Integration` (run time): `TaskRunner.Tests` 150 s (125 tests), `Cli.Tests` 103 s (347), `State.Tests` 55 s (227), `Api.Tests` 45 s (619), everything else under 6 s. The two Jdbc projects and `Loader.Tests` have no test outside `Category=Integration` and run nothing.

Every other class of the projects above is under 5 s. Tests that shell out (cost is the child process, usually `dotnet publish` of a fixture project, a Bun bundle or a spawned host): `DbDataSync.Libraries.Tests/FixturePublisher.cs` and `Shared/tests/DbDataKit.Runner.Execution.Tests/PackageFixture.cs` (`dotnet publish`; run once per class fixture), `DbDataSync.Cli.Tests` (`LauncherTests`, `PackagePublishOnRealRunnerTests`: spawn the CLI and a runner), `DbDataSync.TaskRunner.Tests` (`RunnerWithoutItsOwnerTests`, `SyncRuntimePackageTests`: spawn the TaskRunner), `DbDataSync.Api.Tests/RunLifecycleIntegrationTests.cs`, `Shared/tests/DbDataKit.Apps.Bundling.Tests` (Bun), `Shared/tests/DbDataKit.Scripting.Tests/CsxCompilerTests.cs`, `DbDataGateway.Tests` (`WorkerFixture`, `WorkerProcessHostTests`: the 32-bit ODBC worker), `DbDataBuild.Tests.Conformance` (engines) and `DbDataRunner.Tests/SamplePackage.cs`. The ones that need an engine or the network already carry `Category=Integration`.

## Sharing one machine between builds (agents and verifiers)

Run every build or test as `flock -o /tmp/build.lock <command>` and give `dotnet build` and `dotnet test` the property `-p:UseSharedCompilation=false` (and export `MSBUILDDISABLENODEREUSE=1`). Without `-o`, the Roslyn server a build starts inherits the lock's file descriptor and keeps the lock for about ten minutes after the build has ended, so every other queued command waits for an idle process.
