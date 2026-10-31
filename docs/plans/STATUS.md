# Plan status

What exists in the repository, what the three products have adopted, and what is not built. The work that remains is the slice table in
[13a-suite-slices.md](13a-suite-slices.md), whose rows carry their own status; this page is the overview, kept short. Last brought up to date on
2026-10-23 (slice X04).

## Layout

One repository: `DbDataKit/` (the shared libraries), `DbDataSync/`, `DbDataProxy/`, `DbDataBuild/`, `dist/` (packaging), `testenv/` (the shared test
engines), `scripts/` (repository checks). `DbDataKit.slnx` lists every project; `<Product>.slnf` is a product's filter of it. Versions are in the root
`Directory.Packages.props`. [CLAUDE.md](../../CLAUDE.md) has the build and test commands.

## Shared libraries (`DbDataKit/src`)

| Area | Packages | State |
| --- | --- | --- |
| Config (plan 01) | `Config`, `Config.Yaml`, `Config.Hosting`, `Config.Testing`, `Config.Git` | strict reader, layering, schema versions, golden and fuzz helpers; git service moved in from Sync (U06). Not done: git-commit backup mode, JSON input behind the node tree |
| Secrets (plan 02) | `Secrets.Abstractions`, `Secrets`, `Secrets.Store`, `Secrets.Windows`, `Secrets.Cli` | encrypted file store with DPAPI or restricted-file keys, rotation, export and import, the `secret` verbs; the first key is created safely from several processes at once. No OS keychain (D10). Cloud providers are not built |
| Hosting (plan 03) | `Hosting.Abstractions`, `Hosting`, `Hosting.WindowsService`, `Hosting.Systemd`, `Hosting.Certificates` (+ `.Windows`), `Hosting.Children`, `Hosting.Cli`, `Hosting.Local`, `Security.Acl` | one ACL implementation; the real service-control manager test needs `DBDATA_ADMIN_TESTS=1` and an elevated prompt |
| ODBC and metadata (plan 04) | `Odbc.Metadata`, `Metadata.Abstractions`, `Metadata` | catalog readers for INFORMATION_SCHEMA, SQL Server, PostgreSQL, DuckDB and ODBC. Not built: a Flight SQL reader, MySQL and Oracle readers |
| Dialects (plan 05) | `Dialect.Abstractions`, `Dialect`, `Dialect.Arrow`, `Dialect.Conformance` | data-driven dialect tables (`dialects/*.yaml`) for type mapping, quoting and literals; Sync and Build read them directly, Proxy through `Connections` and `Metadata` |
| Connections (plan 06) | `Connections.Abstractions`, `Connections`, `.SqlServer`, `.Postgres`, `.DuckDb`, `.Odbc`, `.Descriptor` | `connection/1`, composers, the failure classifier and its mappers, login names (`LoginEnvironment`). Not built: Flight SQL, JDBC, MySQL and Oracle providers |
| Transfer (plan 07) | `Transfer.Abstractions`, `Transfer`, `.Conformance`, `.SqlServer`, `.Postgres` | contracts, value conversion, staging policy, strategy SQL builders. Not built: Arrow-first and Parquet staging, MySQL and Oracle writers, a verifier, segmenting, SCD2 |
| Update (plan 08) | `Update.Abstractions`, `Update`, `.Service`, `.Detect`, `.Tools`, `.Updater` | manifests, channels, checks, downloads, signature checks. Not done: Authenticode, the scheduled helper |
| Suite (plan 13) | `Cli`, `Scheduling`, `Monitoring`, `Web` | shared command specs and effect classes (C01); cron and time-zone evaluation (M01); the management authorization model (T05) |

## What the products have adopted

- **DbDataSync:** strict config reading with a required `schemaVersion` (A01-A03); the encrypted secret store under `dbdata:sync:...` names, with
  nothing imported from the old store (A04, A05); the shared dialect tables for every engine's type mapping (A06, A07); the shared git service and
  scheduler evaluator; workers and library validation start as the same executable (F14, in review). Not yet: connection composers, the shared transfer
  strategies, the console, the script and library loader of plan 14.
- **DbDataBuild:** shared quoting, command specs and effect classes (C01), the login variable rule (B02), the failure classifier (B03), the
  project file commands (W01). Not yet: the shared connection types and ODBC catalog reader, the server, scheduling.
- **DbDataProxy:** the shared connections, metadata readers and failure model (plan 10), secrets in the encrypted store (V06). Not yet:
  the management API, the console.

## Tests and checks

- Repository: `python3 -I -m unittest discover -s scripts/tests`, `python3 -I scripts/check_package_versions.py`, `python3 -I scripts/check_solution.py`.
- Engines: `testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env <product>)"`. Build's slow engines (SQL Server 2025, Oracle,
  Spark, BigQuery, Flight SQL) are profiles too (`mssql2025`, `oracle`, `spark`, `bigquery`, `flight`, `flight-tls`; `up all` starts everything).
- Shared libraries and Proxy use xunit v3 on the Microsoft Testing Platform (run the built test executable if `dotnet test` cannot start the host);
  Sync and Build use xunit v2.
- CI is one workflow per product plus `dbdatakit-ci.yml`, `repo-checks.yml` and the `dist-*` workflows; product jobs start from
  `.github/actions/setup-product`.

## Not built (carried forward)

Flight SQL and JDBC providers; MySQL and Oracle providers and writers; Arrow-first and Parquet staging; a transfer verifier; config CLI commands
beyond the shared `secret` verbs (the C-series); the reusable release workflow and the suite release (the L-series). Decisions are in plan 13,
section 7; breaking changes since the last release are in [RELEASE-NOTES-PENDING.md](RELEASE-NOTES-PENDING.md).
