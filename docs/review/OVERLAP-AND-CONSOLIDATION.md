# Overlap and Consolidation

Source: the three per-repo reports in `detail/` (file paths and line numbers are there). This document is the
cross-repo view and proposes a shared-package layout. Claims about specific classes come from those reports
and were not all re-verified.

## 1. Where the projects overlap

| Area | Sync | Proxy | Build | Observation |
|---|---|---|---|---|
| Domain model (connection, source/target, mapping) | YAML entities | `DataSources` in appsettings | YAML models + connections | Same nouns, three schemas |
| Provider/driver abstraction | Driver descriptor (YAML + ADO.NET), JDBC via IKVM bridge, `GenericDriver` | Provider layer for SqlServer/DuckDB/ODBC, parameter binding | `ITarget` + `DialectProfile` + `TrackingDdl` | Three abstractions; Build's is split across three types |
| Dialect / type mapping | `SqlDialect`, `CanonicalType` | Arrow type mapper | `DialectProfile`, `ConnectionKinds`, `TransferValues`, duplicated type switches | Overlap is highest here |
| Identifier quoting | escaped in dialect | per-source quoting facts via `GetSqlInfo` | quoting duplicated in places | Needs one implementation + tests |
| Connection strings / logins | per-driver properties | plaintext connection strings | `OdbcLogin`, `FlightSqlLogin`, env-var strings | Builders are the shared piece |
| Secrets | ClrKernel keyring | none | env vars | Three models |
| Failure scrubbing | credential detector + redactor | returns raw DB errors | `DriverFailure` scrubbing | Sync and Build have it; Proxy lacks it |
| YAML | typed deserialization | n/a | strict event reader | Two parsers with different safety properties |
| ODBC | `GenericDriver` + `FixedConnectionStringProperties` | `DbData.Odbc.Metadata` + 32-bit worker | `OdbcLogin` | Three ODBC touchpoints |
| Read/write safety | per-job | `AllowWrites` (partial) | `MutationGate`, regex `ReadGuard`, statement log | Concept exists in Build; missing in Proxy |
| Read → stage → load | replication strategies | streams reader to Arrow, no staging | transfer between engines with `TransferValues` | Sync and Build solve the same problem |
| Install / update | NuGet tool + `Updates` | ps1 + service/Docker | release + native lib download | Three mechanisms |
| Git/versioned config | `GitCommitService` | none | plan files | Sync-only |
| Certificates | `Certificates` | self-signed generation | localhost web/MCP hardening | Reimplemented |

## 2. Proposed shared packages (common repo)

Ordered by payoff and by how little they depend on the others.

1. **DbData.Dialect** — engine enum, `CanonicalType`, type mapping to/from each engine and to Arrow,
   identifier quoting, literal escaping, a read-only SQL guard (a real parser, not regex). Merges Sync
   `SqlDialect`/`CanonicalType`, Build `DialectProfile`/`ConnectionKinds`/`TransferValues`, and Proxy's Arrow mapper.
2. **DbData.Connections** — descriptor model, connection-string builders with attribute allow-listing
   (reject `Authentication=`, `DRIVER=`, `FILEDSN`, etc. from user-supplied values), `OdbcLogin`,
   `FlightSqlLogin`, parameter-marker translation (`@name` → `?` for ODBC), retry/timeout defaults.
3. **DbData.Secrets** — adopt `ClrKernel.Core.Secrets` or wrap it behind an `ISecretResolver` with
   `env:`, `keyring:`, `file:` references. Config files reference a secret name and never contain values.
   Includes the credential detector/redactor and driver-failure scrubbing.
4. **DbData.Yaml** — Build's strict event-level reader plus a schema-version key and published JSON Schemas
   for connection/driver/mapping definitions. Sync could keep typed models on top of it.
5. **DbData.Odbc.Metadata** (in flight) — see section 4 for gaps before extraction.
6. **DbData.Transfer** — read/stage/load strategies (bulk copy, merge, upsert, truncate-load), staging-table
   lifecycle, batching, type coercion via `Dialect`. This is the biggest design task; start by writing down
   the strategy list Sync and Build each implement and diffing them.
7. **DbData.Hosting** — certificates, localhost web/MCP hardening, updater, release pipeline templates,
   service/Docker installers.

Rule of thumb: package 1–4 are pure libraries with no I/O beyond files; 5–6 touch databases; 7 is tooling.
Keep each independently versioned so a repo can adopt them one at a time.

## 3. Architectural thoughts beyond ODBC metadata

- **Proxy as the single bridge for exotic engines.** ODBC (especially 32-bit drivers) and JDBC both need
  process isolation. Sync's IKVM JDBC bridge and Proxy's 32-bit worker solve the same "run foreign driver
  out of process" problem. Consider a common worker protocol, with Flight SQL as the wire format Sync and Build
  consume. Cost: an extra hop and a service to deploy; benefit: one place for pooling, limits, auth and audit.
- **One provider interface per engine**, not per concern. Build's `ITarget`/`DialectProfile`/`TrackingDdl`
  split is where duplicated switches creep in. A single `IEngineProvider` (quote, types, DDL, bulk load,
  metadata, capabilities) gives all three repos the same extension point and a single place to add Postgres,
  Oracle, MySQL, etc.
- **Capability flags over `if (engine == …)`.** Proxy already reports capabilities via `GetSqlInfo`; reuse the
  same vocabulary inside the library.
- **Connection pooling.** Proxy has none (new connection per query and per ODBC metadata call). Whatever
  shared connection layer is built should own pooling, timeouts and cancellation so Proxy gets them for free.
- **Common definition vocabulary.** Even if each product keeps its own YAML, align names for `connection`,
  `engine`, `driver`, `secret`, `schema/table` and add `schemaVersion` everywhere (Build has none today).
- **Trust model written down.** Build treats YAML as trusted-author input, Sync lets Admins run arbitrary code
  by design, Proxy treats clients as semi-trusted. A one-page shared threat model keeps the shared libraries
  from being "safe" in one host and exploitable in another.
- **Test fixtures.** Each repo carries its own SQL Server test container and passwords. A shared
  `DbData.TestKit` (container fixtures, golden type-mapping tables, dialect conformance tests) would catch
  cross-engine regressions once, for all consumers. Conformance tests per engine are the best defence against
  the dialect drift this layout is meant to remove.
- **Release/install convergence.** Choose one: NuGet dotnet tool for CLIs plus a signed zip for services.
  Verify native artifacts (Build's Rust lib) against a checksum published *separately* from the artifact.
- **Dependency direction.** Common repo must not depend on any product. Products depend on common via
  NuGet (not submodules) so versions are explicit; this repo (DbDataKit) can remain the integration/meta repo.

## 4. Readiness of `DbData.Odbc.Metadata`

From the Proxy review: zero package dependencies and a clean interface seam, so extraction is mostly ready.
Before moving it:

- own `Directory.Build.props`, `Directory.Packages.props` and packaging metadata;
- add a statement timeout and a row cap on metadata calls;
- add connection-string allow-listing (see `DbData.Connections`);
- delete Core's duplicate DTOs afterwards;
- decide whether parameter-marker translation lives here or in `DbData.Connections`; Sync's ODBC gap
  (positional `?` binding vs `@name`) belongs to the same fix.

## 5. Suggested sequence

1. Fix the High security findings in place (SECURITY.md). Small, independent, and shared code shouldn't inherit them.
2. Create the common repo with CI baseline (central packages, pinned Actions, vuln scan). Move
   `DbData.Odbc.Metadata` first because it is already separated.
3. Extract `DbData.Dialect` + conformance tests; port Build then Sync onto it; Proxy adopts it for its Arrow mapper.
4. Extract `DbData.Secrets` and `DbData.Connections`; migrate Proxy off plaintext connection strings.
5. Write down Sync's and Build's transfer strategies side by side; design `DbData.Transfer`.
6. Decide the Proxy-as-bridge question (and retire Sync's direct ODBC path if yes).
