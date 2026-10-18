# Target engines we can test without a cloud subscription

The more engines the lowering, the target rules and the support matrix are checked against early, the less each of them bends to the first two. This is the shortlist of engines that run on a developer
machine: in docker, as an embedded library, or in a vendor's own emulator. Nothing here needs an account, a credit card or a network after the image is pulled. "Local images" marks what is already on the
machine this was written on (`docker images`).

## Running today

| Engine | How | Notes |
|---|---|---|
| SQL Server 2022 | `mcr.microsoft.com/mssql/server:2022-latest` | the primary target; every probe and template runs on it |
| PostgreSQL 17 | `postgres:17-alpine` | second target |
| DuckDB 1.5 | the library | the reference: every answer is compared with it |

## Probed now (dialect only; no plan or apply yet)

Oracle 23ai Free, Spark SQL 4.0 and the BigQuery emulator run the dialect probes locally and in CI (`scripts/test-engines.sh up oracle spark bigquery`, one CI job each); the scoreboard and what
differs is in `engine-differences/oracle-spark-bigquery.md`. Two notes on the choices: the BigQuery emulator is `goccy/bigquery-emulator`, a **community** project (Google's own emulator is Spanner's), so a
failure there can be the emulator's; and Spark runs with ANSI mode on, which is Spark 4's default and the closer match to DuckDB's errors.

## Next, in this order

| # | Engine | How | Why it is worth it | Dialect notes |
|---|---|---|---|---|
| 1 | **SQL Server 2025**, and 2022 databases in compatibility mode | `mcr.microsoft.com/mssql/server:2025-latest` (pulled and run; a new database starts at level 170, `ALTER DATABASE ... SET COMPATIBILITY_LEVEL = 160` gives the older behavior) | the regular expressions (`REGEXP_LIKE`, `REGEXP_REPLACE`, `REGEXP_SUBSTR`, `REGEXP_COUNT`, `REGEXP_INSTR`, RE2 like DuckDB), `||`, `PRODUCT`, `CURRENT_DATE`; a 2025 server with a level-160 database has none of the regex functions (probed), so the version gate has to be min(engine version, compatibility level / 10) | still no `LPAD`, `NTH_VALUE`, `JSON_ARRAY_LENGTH`, `SPLIT_PART` |
| 2 | **Oracle Database 23ai Free** (probed) | `gvenzl/oracle-free` (local image) or `container-registry.oracle.com/database/free`; Oracle's own | the most different mainstream SQL: no `LIMIT`, `FETCH FIRST`, empty string is NULL, `DATE` has a time, `VARCHAR2`, no boolean in SELECT lists before 23ai, `||` NULL rules | polyglot has an `oracle` dialect; the biggest test of "NULL and empty text" |
| 3 | **MySQL 9 / MariaDB 11** | `mysql:9`, `mariadb:11` (both local images) | the other common OLTP dialect: backtick quoting, `||` is OR unless `PIPES_AS_CONCAT`, integer division `DIV`, no `FULL JOIN`, `CTE` and window functions from 8.0 / 10.2 | `mysql` dialect in polyglot; MariaDB differs from MySQL in JSON and `RETURNING` |
| 4 | **Apache Spark SQL** (probed; Delta Lake locally next) | `apache/spark` image or `pyspark` in a container; local mode, no cluster | Databricks, Fabric's lakehouse SQL endpoint and EMR all speak it; `ANSI` mode on and off, `QUALIFY`, `PIVOT`/`UNPIVOT` natively, `TRY_CAST`, `LATERAL VIEW explode` for lists | `spark` dialect; the stand-in for Databricks |
| 5 | **Trino** | `trinodb/trino` image with the `memory` and `tpch` connectors | Athena, Starburst and many lakehouses; strict types (no implicit casts), `||`, `TRY_CAST`, `UNNEST` of arrays, `date_diff`, no `UPDATE` on most connectors | `trino` dialect; the stand-in for Athena |
| 6 | **ClickHouse** | `clickhouse/clickhouse-server` | the analytics engine with the most unusual semantics: `NULL` is a type, integer overflow wraps, `toDate`, arrays everywhere, `ARRAY JOIN` | `clickhouse` dialect |
| 7 | **Spanner emulator** | Google's own `gcr.io/cloud-spanner-emulator/emulator` (GoogleSQL and PostgreSQL dialects) | the only vendor-provided BigQuery-family emulator: GoogleSQL is BigQuery's dialect (`QUALIFY`, `UNNEST`, `SAFE_CAST`, `DATE_DIFF`) | `bigquery` dialect in polyglot is close; data types and DDL differ from BigQuery |
| 8 | **Db2 Community** | `icr.io/db2_community/db2` | the other big enterprise engine; strict typing, `VALUES`, `FETCH FIRST`, `CURRENT DATE` | `db2` is not a polyglot dialect: needs the generic path |
| 9 | **CockroachDB / YugabyteDB / TiDB** | single-node docker images | PostgreSQL (the first two) and MySQL (the last) wire and syntax with different semantics under it (serial ordering, transactions, `TIMESTAMP` handling); cheap checks of "PostgreSQL" really meaning PostgreSQL | reuse the postgres and mysql targets |
| 10 | **SQLite** | the library (embedded, `Microsoft.Data.Sqlite`) | the smallest dialect: dynamic typing, no `DATE`, `||`, no `FULL JOIN` before 3.39; good for finding what the tool assumed | `sqlite` dialect |

## Cannot be run without a subscription (or only as a look-alike)

| Engine | What there is locally | Use it for |
|---|---|---|
| **Microsoft Fabric** (Warehouse, Lakehouse SQL endpoint) | nothing: Azure SQL Edge, the closest local T-SQL engine, was retired in 2025. SQL Server 2025 in a container is the nearest dialect | Fabric stays "unverified"; verify the constructs against SQL Server 2025 and read Fabric's T-SQL surface-area page |
| **Azure SQL Database / Managed Instance** | SQL Server 2025 container (the same engine family, evergreen features) | the same code path as SQL Server at its newest compatibility level |
| **Azure Synapse dedicated pool** | nothing | skip |
| **Snowflake** | only third-party emulators (LocalStack's is paid) | `snowflake` dialect in polyglot; unverified until someone has an account |
| **BigQuery** | community emulator only (`goccy/bigquery-emulator`), not the vendor's | the Spanner emulator and the polyglot `bigquery` dialect stand in |
| **Redshift** | nothing; it is PostgreSQL 8-derived | the postgres target with Redshift's limits written down |
| **Databricks SQL** | Spark SQL locally (the open-source core) | Spark covers the language, not the Photon behavior |
| **Teradata, SAP HANA, Vertica, Exasol** | trial or express editions that are heavy VMs or need registration | later, if a user asks |

## How a new engine is added

1. A target name, an `ITarget` (dialect for polyglot, loader strategies) and a DDL generator (`DdlGenerator.MapType` and the identifier quoting).
2. A container in `scripts/test-engines.sh` (a third and fourth container, started on request) and an `Engine` class in the conformance suite.
3. The probes first (`EngineDifferenceProbes`): they say what differs from DuckDB with no model involved, and every difference becomes a matrix row, a target rule or a refusal.
4. Then the templates: the four sample projects are the end-to-end check, compared row by row with DuckDB.
5. A matrix column for the engine in `matrix/constructs.yml` (a status per row); the rows already carry `unverified` for Fabric, so a missing column is a tooling change, not a rewrite.

The probes (about 270 cases) and the template comparisons are what make the third engine cheap: a new engine mostly produces a list of rows for the matrix, not new test code.
