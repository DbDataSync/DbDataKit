# Plan 13a: the slices of plan 13

Status: **proposed** (2026-10-21). This breaks [plan 13](13-suite-standardization.md) into pull requests. The workstream sections of plan
13 say *why*; this says *in what pieces*.

## Rules for every slice

- **One pull request, small.** Most slices are S (a day or two); none is meant to be more than about three days. A slice that grows is
  split before it merges, not after.
- **Done means its test passes.** The "Done when" column is the test, run in CI. A slice with no automatic test says what is checked by
  hand and why.
- **CI green and independently revertable.** Each slice merges into `main` on its own (plan 09's trunk model, rebase merge). Nothing in
  the middle of a sequence leaves `main` broken or a product unshippable.
- **Adapter first, delete second** (plan 10): a slice that replaces code keeps the old tests passing unchanged; deleting the old code is its
  own slice, or the last step of the same one when the tests already pass.
- **Behaviour changes are named** in the pull request and in the product's release notes (breaking changes are allowed, but never silent).
- **Docs in the same pull request**: the product's docs, `DESIGN.md`/progress entries where a product's own rules ask for them (Build's
  `CLAUDE.md`), and the "Status" line of the slice's row here.
- **Branch names** `suite/<id>-<short-name>` (for example `suite/f01-root-props`).
- **Waits on** names a decision of plan 13 section 7. A slice whose decision is open does not start; everything else can.

Sizes: **S** a day or two, **M** three days. Anything larger is split.

## Milestones

Slice IDs are a letter per workstream and two digits; decisions of plan 13 are `D` and one or two digits without a leading zero (`D8`, `D16`).

Each milestone is a point where something can be shown working. They follow plan 13's phases.

| Milestone | Slices | What can be shown |
| --- | --- | --- |
| **M0 Foundations** | F01-F13, K01-K06, S01 | one build setup and one version per package; every suite run against DuckDB 2.0.0; the dotnet-script spike report |
| **M1 Engines and loaders** | K07-K11, S02-S10, A01-A07, B01-B03, U01-U03 | 2.0 is the default; Sync loads libraries, drivers and scripts through dotnet-script; Sync's UI builds from the shared workspace |
| **M2 Console with Sync** | T01-T09, U04-U12, C01-C07, E01, E04, M01-M06, A08-A10, A17-A18 | Sync is managed from the dbdatakit console on another machine; the new command names; Sync's scheduler on the shared packages |
| **M3 Proxy and Build managed** | P01-P10, R01-R09, M07-M11, E02-E03, E05, A11-A16, A19, A20, B04-B08, C08 | Proxy and Build are configured and operated from the console; Build refreshes run on schedule and are monitored |
| **M4 Authoring and release** | W01-W10, L01-L06 | a Build project authored, approved and applied from the console; the first suite release |
| **M5 Clean-up** | X01-X04 | the duplicated code listed in plan 13 section 6 is gone |

Inside a milestone the slices of different workstreams run in parallel; the **Needs** column is the only ordering that matters.

## F. Repository foundations (W0)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| F01 | Root `global.json` and a root `Directory.Build.props` (net10.0, nullable, LangVersion) that each product's props imports | | S | every product and `DbDataKit/` build and test exactly as before; CI green |
| F02 | Root `Directory.Packages.props`; `DbDataKit/` and Proxy pins move into it (union) | F01 | S | `dotnet list package` shows the same versions as before for both; CI green |
| F03 | Build onto central package management through the root file | F02 | S | Build CI green; no `Version=` left in Build `.csproj` files |
| F04 | Sync onto central package management, same versions as today | F02 | S | Sync CI green; no `Version=` left in Sync `.csproj` files |
| F05 | Sync: `Npgsql` 9.0.3 to 10.0.3 | F04 | S | Sync Postgres driver, state and integration tests green |
| F06 | Sync: `Microsoft.Data.SqlClient` 7.0.2 to 7.1.1, `DuckDB.NET.Data.Full` 1.5.5 to 1.5.6 | F04 | S | Sync MsSql, DuckDb and integration tests green; one version of each package in the repository (a CI check fails on two) |
| F07 | Proxy: warnings as errors, fixing what surfaces | F01 | S | Proxy builds with warnings as errors; tests unchanged |
| F08 | Build: the `DbDataKitReference` mechanism (`Directory.Build.targets` as Proxy and Sync have it) | F03 | S | a test project in Build references one shared package through it and builds in CI |
| F09 | Root `DbDataKit.slnx` with every project; per-product `.slnf` filters | F01 | S | a CI job builds `DbDataKit.slnx`; each product's workflow builds its filter |
| F10 | Reusable `_product-ci.yml` (changes job, build, test with MTP floors, artifact names); Proxy moves onto it | F09 | M | Proxy CI runs through it with the same jobs and test counts as before |
| F11 | Sync and Build move onto `_product-ci.yml` | F10 | M | their CI runs through it with unchanged test counts |
| F12 | Build's conformance suite uses the `testenv/` engines instead of `scripts/test-engines.sh` | F11 | M | the quick conformance group green in CI on `testenv/`; the old script prints where it went |
| F13 | Root `CLAUDE.md` (who owns what, build and test the repository); root `docs/review/` moved into products or deleted | | S | docs only; links checked by the docs link test where one exists |

## K. DuckDB 2.0 (W1)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| K01 | `scripts/duckdb-native-swap.sh` at the root (from Build's preview script): runs any test project against a pinned, hash-checked 2.0.0 `libduckdb` | | S | it runs Build's unit suite on 2.0.0; the result (pass count, failures) is written to `DbDataBuild/docs/research/duckdb-2.0/README.md` |
| K02 | Build conformance suite on 2.0.0 through the swap | K01 | S | result recorded; any new failure has an OPEN-ITEMS entry |
| K03 | `Connections.DuckDb` and Proxy's DuckDB tests on 2.0.0; fix sandbox setting names if 2.0 renamed any | K01 | S | the sandbox tests (no external access, locked configuration, allowed directories, read-only) pass on 1.5.6 and 2.0.0 |
| K04 | Sync's `Drivers.DuckDb`, `Verification` and `Scripting` tests on 2.0.0 | K01 | S | green on both versions, or failures recorded with a slice to fix each |
| K05 | Storage format: a test that opens a 1.x database file read-only with 2.0 and asserts its bytes are unchanged; an audit of every read-write open in the products | K01 | S | the test passes; the audit is a table in the research README; read-write opens of user files, if any, become read-only or are named in release notes |
| K06 | A dispatch-and-nightly CI job running K01-K04 on 2.0.0 | K01-K04 | S | the job is green on `main` |
| K07 | Build: ANY/ALL in 2.0's CASE form | D8 decision | M | the six ANY/ALL unit cases and the differential test pass on 2.0 (translated or refused with a named diagnostic, per D8) |
| K08 | Build: `%` and `//` by zero | D8 | S | unit and real-engine tests give the D8 answer on 2.0 |
| K09 | Build: decimal `median`/`quantile_cont`; `current_schemas`, `current_setting`, `getvariable`, `getenv` refused with a named diagnostic | D8 | S | the quantile test passes on 2.0; each folding function has a diagnostic fixture |
| K10 | The pin: DuckDB.NET 2.0 when released, otherwise the interim build (1.5.6 managed, 2.0.0 native, pinned and hashed) | D6, K06 | S | every suite green on the new pin on Linux and Windows; one DuckDB version in the repository |
| K11 | 2.0 the default: regenerate Build's lowered artifacts and template goldens; remove 1.x-only code per D7 | K07-K10, D7 | M | `UPDATE_GOLDEN=1` diff reviewed and committed; all suites green; the swap job becomes the normal DuckDB job |

## S. Libraries, drivers and scripts on dotnet-script (W2)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| S01 | Spike: `Dotnet.Script.Core` 2.x inside the ASP.NET host on .NET 10 (throwaway branch) | | M | a written report: restore online and from a local feed, native assets (DuckDB, SQLite, Oracle), unload, cold-compile time against today's Roslyn path, Docker image, Windows service profile; D9 decided |
| S02 | `DbDataKit.Scripting`: the resolver (package references to a closure on disk, cached by hash), with `Connections.Libraries.Validation` moved in | S01 | M | unit tests against a local feed fixture; the H1 hostile-input corpus passes |
| S03 | The loader: a collectible load context per library, driver or script, with native assets | S02 | S | tests load Npgsql and DuckDB from a resolved closure, run a query, and unload |
| S04 | The compiler: `.csx` to a cached assembly, the allow-list and `ScriptSyntaxGuard` ported | S02 | M | Sync's `ScriptCompiler` tests, ported, pass against it |
| S05 | Sync libraries through the resolver and loader (`library.csx`; `library.json` still read) | S03, A03 | M | Sync's library tests and the Libraries page Playwright test pass unchanged |
| S06 | Sync compiled drivers as `driver.csx` | S03, S04 | S | the plugin-driver tests pass with a `.csx` driver and with the old `driver.json` |
| S07 | Sync scripts as `.csx` | S04 | S | Sync's scripting tests pass on `.csx` scripts |
| S08 | The migration: `.cs` to `.csx`, `library.json` to `library.csx`, `driver.json` to a shim `driver.csx` | S05-S07, A03 | S | golden migration pairs (`Config.Testing`); a `.bak` is written; a migrated fixture repository starts and runs |
| S09 | `dbdatasync script test <name>` and the editor stub for `.csx` | S07, C06 | S | CLI test runs a fixture script; the stub resolves in a C# language-server test |
| S10 | Delete the old paths (`DbDataSync.Libraries`, plugin loading in `Drivers.Descriptor`, `ScriptCompiler`'s compilation, `Connections.Libraries`' installer) | S08 | S | tests green; a source scan finds no reference to the removed types |

## A. Sync adopts the shared libraries (W3)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| A01 | Sync connection files read through `Config.Yaml`'s strict reader | F04 | S | Sync's config tests pass; unknown keys are errors with a path (new tests); fuzz test over the connection files |
| A02 | The remaining Sync entity files (replications, mappings, scripts' manifests, drivers) through the strict reader | A01 | M | same as A01 for each type |
| A03 | `schemaVersion` and the `Config` migration framework in Sync; `dbdatasync config migrate` | A02 | S | golden migration pairs; `--apply` writes `.bak` |
| A04 | Sync secret references through `DbDataKit.Secrets` | D10 decision, A01 | M | Sync's secret tests pass; a sentinel-password test finds no secret in any log line |
| A05 | Remove `ClrKernel.Core.Secrets` | A04 | S | no reference left; tests green; stored keyring secrets migrated or the release note says how to re-enter them |
| A06 | Sync quoting and lexer from `Dialect` | F04 | M | Sync's dialect tests pass; the shared hostile-SQL corpus runs against Sync's dialects |
| A07 | Sync type maps from `Dialect` (`CanonicalType` from the shared package); delete `Core/Sql`'s copies | A06 | M | type round-trip tests per engine; `Core/Sql` keeps only what is Sync's |
| A08 | Sync SQL Server and Postgres connection strings through the `Connections` composers | A07 | M | connection tests per driver; the hostile-credential corpus; TLS verify-by-default named in release notes |
| A09 | `Connections.MySql` and `Connections.Oracle` (new shared packages) and Sync on them | A08 | M | composer tests; Sync's MySQL and Oracle integration tests |
| A10 | Sync failures through the shared classifier | A08 | S | each driver's failure tests map to the shared kinds; no raw driver text reaches the UI (test) |
| A11 | Sync catalog reads for SQL Server and Postgres through `Metadata` | A08 | M | metadata tests pass unchanged; the mapping metadata cache still works (Playwright) |
| A12 | MySQL and Oracle readers in `Metadata`; Sync on them | A09, A11 | M | reader tests per engine, live in CI |
| A13 | Transfer benchmark baseline for Sync's staging paths | | S | numbers committed; the benchmark runs in a manual job |
| A14 | Sync SQL Server bulk staging through `Transfer.SqlServer` | A13, A08 | M | bulk-load tests; benchmark within 5% of A13 |
| A15 | Sync Postgres COPY staging through `Transfer.Postgres` | A13, A08 | M | same as A14 |
| A16 | Sync merge and delete-insert strategies from the shared strategy builders | A14, A15 | M | replication integration tests unchanged |
| A17 | `DbDataKit.Connections.FlightSql` from Sync's Flight SQL connection code; Sync's driver keeps only its readers | A08 | M | Sync's Flight SQL tests pass against the shared client |
| A18 | CI: the Flight SQL client against a real `dbdataproxy server` | A17 | S | an end-to-end job runs queries through Proxy to SQL Server and DuckDB |
| A19 | Build's `FlightSqlRowSource` and `FlightSqlLogin` on `Connections.FlightSql` | A17, B02 | S | Build's Flight conformance group (`up flight`) green |
| A20 | Unused shared packages: adopt or delete `Connections.Descriptor`, `Dialect.Arrow` (Proxy benchmark, plan 10 C7), `Secrets.Cli` | | S each | each either has a consumer with tests or is removed from the solution |

## B. Build adopts the shared libraries (W3)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| B01 | Build quoting and lexer from `Dialect`, behind `DialectProfile` | F08 | M | Build unit suite unchanged; Build's quoting fixtures added to the shared conformance cases |
| B02 | Build logins (`Logins`, `OdbcLogin`, `FlightSqlLogin`) through `Connections` | F08 | M | login tests; `DBDATABUILD_<TARGET>_<READ|WRITE>` still works; no read-to-write fallback (test) |
| B03 | Build failures through the shared classifier; `DriverFailure` removed | B02 | S | the real-engine scrub test passes; "driver messages are never echoed" is a shared-classifier test |
| B04 | Build catalog reads (`CatalogReader`, `SourceCatalogReader`, `OdbcCatalogClient`) through `Metadata` | B02 | M | Build's import and status tests unchanged; conformance quick group green |
| B05 | `ServerFacts`: Build uses the shared record | B04 | S | facts golden tests unchanged |
| B06 | `MutationGate` replaced by the shared `WriteGate`; `GateInvariantTests` moved to `DbDataKit/` and run against both | B02 | M | the gate invariant test passes in the shared suite and for Build |
| B07 | Build's statement log on the shared write gate's log | B06 | S | statement log tests and retention tests unchanged |
| B08 | Build's `Transfer.cs` (copies) on `Transfer` | B06, A16 | M | the `fan_in_fan_out` template and copy conformance tests green |

## C. One command surface and the renames (W4)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| C01 | `DbDataKit.Cli`: the command spec model (effect class, JSON document schema, help) taken from Build's `CommandSpecs`; Build uses it | F08 | M | Build's command tests, `docs/commands.md` generation and the output-schema test unchanged |
| C02 | The shared nouns in `DbDataKit.Cli`/`Hosting.Cli`: `server run`, `service`, `cert`, `config`, `secret`, `health check` | C01 | M | unit tests per verb against fakes; help snapshot tests |
| C03 | Proxy: lower-case executables (`dbdataproxy`, `dbdataproxy-worker`), service registration, `dist.json`, `install.ps1`, MSI | F10 | S | dist tests, the MSI end-to-end test and the service tests pass with the new names |
| C04 | Proxy grammar: `server run` (no default command), `secret hash`, `secret protect`, `query run`; `install`/`uninstall` removed | C02, C03 | S | CLI snapshot tests of help and exit codes; release note |
| C05 | Proxy commands on the spec model with JSON documents | C04 | M | every Proxy command has a schema; a test fails for a command without one |
| C06 | Sync grammar: `server run`, `server setup`, `cert`, `secret`, `library`, `driver`, `health check` | C02 | M | `CertCommandSnapshotTests` and the CLI tests updated to the new names; the service's command line and `service-e2e.ps1` updated; release note |
| C07 | Sync commands on the spec model with JSON documents | C06 | M | as C05 |
| C08 | `ui mcp` from the shared surface for Sync and Proxy | C05, C07 | S | an MCP tool-list snapshot per product |

## T. Console and server trust (W12)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| T01 | Token format, signing and validation in `DbDataKit.Web` (ed25519, issuer, audience, expiry, replay, ceiling) | | M | unit tests, including hostile tokens: wrong key, wrong audience, expired, replayed, above the ceiling, malformed |
| T02 | The local channel in `DbDataKit.Hosting`: named pipe (Windows) and Unix socket, OS-restricted | | M | tests on Linux and on the Windows job: allowed identity connects, another is refused |
| T03 | Server side of pairing: `server pair` (single-use code), paired consoles in `server.yaml`, `server consoles`, `server unpair` | T01, T02, C02 | M | tests: code expires, code is single use, unpair takes effect on the next call |
| T04 | Console side: `dbdatakit instance add`, certificate pinning, the console's signing key in a protected store | T03, U07 | M | an end-to-end test pairs a console with a fake server over HTTPS with a self-signed certificate |
| T05 | Authorization on management APIs: the role from the effect class, the ceiling, the lower of the two | T01, C01 | S | a test per role and effect class; the server refuses what the console would hide |
| T06 | Audit: user and console on every change, and as the author of config commits | T05, U06 | S | tests read the audit log and the git author after a change |
| T07 | Signing key rotation (the new key signed with the old) | T04 | S | after rotation old tokens fail, new ones pass, no re-pairing |
| T08 | Write version checks (stale writes refused with the current version) | T05 | S | two concurrent writes: the second is refused with the current version |
| T09 | Management API version reported at pairing and on every call; the console's N and N-1 rule | T04, D17 | S | a console refuses a server two versions ahead with the upgrade message (test) |

## U. Shared web platform and the console host (W5)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| U01 | npm workspace at `DbDataKit/web/`; `DbDataSync.Web` moved in as `@dbdatakit/sync`, building the same output | | S | Sync's vitest and Playwright suites pass; the served files are the same |
| U02 | `@dbdatakit/shell` extracted (shell, sign-in, common components, API client) | U01 | M | suites unchanged; `shell` imports nothing from `sync` (lint rule) |
| U03 | `@dbdatakit/admin` extracted (users, certificates, updates, config history, libraries, drivers, files, docs) | U02 | M | suites unchanged; `admin` imports nothing from `sync` |
| U04 | `DbDataKit.State`: users, sessions, invites, notifications and audit stores out of `DbDataSync.State` | F04 | M | the moved store tests pass on SQLite, SQL Server and PostgreSQL |
| U05 | `DbDataKit.Web`: session handler, passkeys, Windows sign-in, policies out of `DbDataSync.Api` | U04 | M | Sync's API auth tests pass unchanged |
| U06 | `DbDataKit.Config.Git` from Sync's `GitCommitService` | F04 | S | the git tests moved and green |
| U07 | Console host skeleton: `dbdatakit server run` serving the workspace build, sign-in, users, invites (`dbdatakit user ...`) | U03, U05, C02 | M | Playwright: first-run invite, passkey sign-in, user list against the console |
| U08 | Console relay (D16): the console back-end forwards a module's calls to a paired server with a token | U07, T04, T05, D16 | M | Sync's Playwright suite passes through the console against a paired `dbdatasync server` |
| U09 | Live updates through the relay (Sync's SignalR run hub) | U08 | S | the run-progress Playwright tests pass through the console |
| U10 | Users, passkeys and invites migrate from Sync's state database to the console's | U07 | S | a migration test on a fixture database; existing passkeys sign in on the console |
| U11 | Sync's server stops serving the SPA | U08-U10 | S | Sync's API tests green; `/` on the Sync server returns the console's address; release note |
| U12 | Console: instances page (pair, list, health, version, unpair) | T04, T09 | S | Playwright: pair, see health, unpair |

## E. Shared definitions (W8)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| E01 | Sync connections as `connection/1` (read both forms, write the new one; migration) | A03, A08 | M | golden migration pairs; the Connections page works unchanged |
| E02 | Proxy data sources as `connection/1` with `extensions.proxy` | P01 | S | Proxy's config tests; golden migration from today's data sources |
| E03 | Build connection files as `connection/1` (a project schema change, migrated by `project update`) | B02 | M | every template migrates; schema test and fuzz tests; `--check` reports old files |
| E04 | Sync host settings from `appsettings.json` to `server.yaml` | A03 | S | migration test; `server run` reads only `server.yaml`; release note |
| E05 | Console: the Connections view across servers, with copy to another server | E01, E02, U08 | M | Playwright: list connections of a Sync and a Proxy server; copy one; the target validates it |

## P. DbDataProxy management (W6)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| P01 | Proxy config to YAML (`server.yaml`, `proxy/`, `connections/`) in a git-tracked directory; migration from `appsettings.json` | C04, U06 | M | golden migration of the shipped example; an upgrade test from the last JSON release in CI; strict validation unchanged |
| P02 | Proxy state store (SQLite through `DbDataKit.State`) | U04, D18 | S | store tests; created on first start |
| P03 | Management listener: health, version, effective config (redacted, with origins), validation | T05, P01 | M | API tests through a console token and through the local channel; no secret in any response (sentinel test) |
| P04 | Data sources: create, change, delete, test connection; hot reload | P03, E02 | M | API tests; a change applies without a restart (test runs a query before and after) |
| P05 | Client identities: Flight users, Windows accounts, JWT mappings, pass-through | P03 | M | API tests; the password hash is never returned |
| P06 | Sessions and running queries (with cancel); limits and login throttle state | P03 | M | a cancel test against a long query; limits reported |
| P07 | Certificate status and renewal; recent classified failures | P03 | S | API tests |
| P08 | Console module: overview and data sources | U08, P04 | M | Playwright against a paired Proxy |
| P09 | Console module: client identities, TLS, activity, config history | P05-P07, P08 | M | Playwright against a paired Proxy |
| P10 | Proxy feeds failed logins, refused statements and limits reached into `Monitoring` | M04, P03 | S | events appear in the console's monitoring view (test) |

## R. DbDataBuild server and management (W7)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| R01 | `dbdatabuild server run` with one project: the management API exposes the read-only commands through child processes | C01, T05 | M | API tests: `connection status`, `project show graph` return the same documents as the CLI |
| R02 | Several projects: `server project add|remove|list`, `build/projects.yaml`, local path or git remote (D19) | R01, D19 | M | tests: two projects served; a remote project is cloned and pulled |
| R03 | Write and deploy commands through the API with roles; approvals recorded in plan events | R01, T05 | M | test: Viewer refused, Approver applies; the plan's events name the approver and console |
| R04 | Secret references into each command's environment (through `DbDataKit.Secrets`, already used by Proxy) | R01 | S | test: a command logs in with a resolved secret; no fallback from read to write |
| R05 | `dbdatabuild service install|...` (Windows and systemd) | C02, R01 | S | unit-file snapshot; Windows service test in the Windows job |
| R06 | Build state store (SQLite) for last runs, workspaces, approvals | U04, R01 | S | store tests |
| R07 | Console module: projects, compile and diagnostics, graph | U08, R02 | M | Playwright against a paired Build server with a template project |
| R08 | Console module: status and drift, plans (review, approve, apply), compare, sample | R03, R07 | M | Playwright: deploy a template project to the test PostgreSQL, approved by a second user |
| R09 | Retire `ui web` | R08 | S | the command is gone (CLI snapshot); release note |

## M. Shared scheduling and monitoring (W10)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| M01 | `DbDataKit.Scheduling`: the schedule model and evaluator (cron with time zone, interval, misfire) from Sync's `SchedulingEvaluator`/`TaskScheduling` | F04 | M | Sync's scheduling tests moved and green; a fake-clock test for daylight saving and missed runs |
| M02 | Work queue with leases and the run lock into `Scheduling`, on the `State` dialects | M01, U04 | M | `WorkQueueStore` and `RunLockStore` tests moved and green on the three dialects |
| M03 | Sync's scheduler on `Scheduling` | M02 | S | Sync's scheduler and run tests unchanged |
| M04 | `DbDataKit.Monitoring`: run model and health rules (overdue, slow, failed, lag) | | M | rule tests with a fake clock |
| M05 | Notifications in `Monitoring`, with per-console read state | M04, U04 | S | store tests; two consoles read independently |
| M06 | Sync's run monitor, lag and notifications on `Monitoring` | M05, M03 | M | Sync's monitoring tests and the Playwright monitoring tests unchanged |
| M07 | Build: `schedule:` and `time_zone:` on refresh plans (parse, validate, store in the plan file, `project show refresh`) | D13 | S | schema and fixture tests; fuzz tests; a diagnostic for a bad cron |
| M08 | The Build server runs schedules through `Scheduling` | M02, M07, R02 | M | a fake-clock test runs a plan at its time; a manual run at the same time is skipped or waits per `on_busy` |
| M09 | Build run history from the tracking tables into `Monitoring` | M04, R01 | S | overdue and slow appear for a plan that missed its `expected_every` (test) |
| M10 | Retention of plan files and statement logs from scheduled runs | M08 | S | test: files beyond the policy are removed, the newest kept |
| M11 | Console: one monitoring view across servers; pause and run-now for schedules | M06, M09, U08 | M | Playwright: a failed Build run and a lagging Sync replication appear together; pause stops the next run |

## W. Build authoring in the console (W11)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| W01 | `project file list|read|write|move|delete`, confined to the project tree, with content hashes | C01 | M | tests: path escapes refused, stale hash refused; mutation tests over every file it parses |
| W02 | Workspaces: a git worktree per user on the Build server; list, create, abandon | R02, W01 | M | tests: a draft does not change what a scheduled run reads |
| W03 | A long-lived child process per workspace (warm DuckDB and parser) | W02 | M | compile of a changed model under a set time on the template projects (measured in CI) |
| W04 | Console: file tree and the SQL editor with diagnostics as markers | W02, R07 | M | Playwright: edit a model, see a diagnostic, fix it |
| W05 | Console: schema-driven forms for model, folder, connection, refresh plan, test and seed files, with a YAML tab | W04 | M | Playwright per file kind; round trip keeps comments |
| W06 | Console: live lowered and rendered text per target, sample rows, the graph around the model | W03, W04 | M | Playwright: change a model, the rendered SQL and sample rows change |
| W07 | Console: new project from a template, new model, the definition questions as a form, import from a connection | W05 | M | Playwright: create the `retail` template, add a model, answer its questions |
| W08 | Git in the console: diff, commit, history; push and pull request, or merge inside the server (D15) | W02, D15 | M | tests for both paths; conflicts refused and shown |
| W09 | The Author role | T05, W08 | S | Author can edit and commit, cannot approve or apply (test) |
| W10 | End-to-end: template to applied plan, all in the console | W07-W09, R08 | S | one Playwright run: create, edit, commit, merge, plan, approve (second user), apply on the test PostgreSQL |

## L. Distribution and releases (W9)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| L01 | `dbdatakit` console zip and Docker image | U07 | S | image builds in CI; health check passes in a container |
| L02 | Build server Docker image | R05 | S | image builds; `server run` answers its health check |
| L03 | Compose files pairing each product image with the console image (pairing code from an environment variable) | L01, T03 | S | a CI job starts each compose file and the console shows the paired server |
| L04 | Each product MSI includes the console as a feature, on by default, and pairs it at install | L01, T03 | M | the MSI end-to-end test (`dist/tests/msi-e2e.ps1`) installs, pairs and signs in |
| L05 | The `dbdatakit` suite MSI (one feature per product plus the console) | L04 | S | MSI end-to-end test with all features |
| L06 | One release workflow producing every image, MSI and zip from one commit; product release workflows call it | L01-L05 | M | a dry-run dispatch produces every artifact and checksum; a beta release |

## X. Clean-up

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| X01 | Remove code that plan 13 section 6 lists as replaced and that is still present | the slices that replace it | S each | a source scan per item finds no use |
| X02 | Shared packages still without a consumer (except `Update.*`, shelved) removed | A20 | S | solution and package lists |
| X03 | Product docs audited for the renamed commands and the console | C04, C06, R09, U11 | S | a docs test that fails on an old command name in any product doc |
| X04 | Root `docs/plans/STATUS.md` brought up to date | | S | docs only |

## What can start now

Everything in M0 except what waits on a decision: **F01-F13, K01-K06, S01**, plus the slices with no "Needs": **A13, M04, T01, T02, U01**.
Good first pull requests: F01, K01, S01 (a spike, not merged), T01 and U01. Most later slices depend on them, and none waits on a decision.
