# Plan 06: Connections, Drivers and Providers (`DbDataKit.Connections`)

Status: proposed. Depends on: Plans 01 (definitions), 02 (secrets), 05 (dialect). Plan 04 supplies the ODBC pieces.

## 1. Problem

| | Sync | Proxy | Build |
|---|---|---|---|
| Definition | `ConnectionConfig` YAML (driver id, host/port XOR connection string, auth mode, `CredentialSecretRef`, timeouts, `Properties`, script/hook bindings) | `DataSourceOptions` (provider, connection string, bitness, passthrough, allow writes, metadata mode, batch size, timeout) | `connections:` in `dbdatabuild.yml` (engine keyword, access `readonly`, version) + env logins |
| Provider model | `IDriver` (readers/staging/writers + `CreateConnection`), built-ins, `GenericDriver` over any `DbProviderFactory`, YAML descriptor, compiled plugin, JDBC via IKVM | `IDbProvider` (`CreateConnection`, `ApplyCredentials`, `ParameterStyle`): SqlServer, DuckDB, ODBC | `ITarget` (build), `IRowSource` (read: ADO/DuckDB/ODBC/Flight SQL), `IWriteExecutor` |
| Connection string | builder-based; ODBC double-quote vs brace mismatch; `Properties` applied last and can override `Password` | admin config; pass-through uses builders; `Authentication=` style keys not stripped | rebuilt via builders; ODBC written as is; env var per login |
| Secrets | secret store ref | plaintext | env var (see Plan 02) |
| Pooling | none (provider pool) | none: new connection per query and metadata call | none |
| Timeouts | stamped on connection | optional, default none | per command |
| Library loading | `Libraries/`: NuGet closure installer, `KnownLibraries`, compat checker | compiled in | compiled in |
| Failures | `ConnectionDiagnostics.Redact` | raw DB message to clients | `DriverFailure` (type + number only) |
| Known defects | ODBC `@name` binding (positional only), `TrustServerCertificate=true` default, library install injection (High) | pass-through unthrottled, no read-only enforcement | `token_command` exec |

## 2. Goals
1. One connection definition schema (with a per-product extension bag).
2. One provider interface and registry; built-ins plus descriptor/plugin/JDBC as optional packages.
3. One secure connection-string composer with credential and allow-list policy; secure TLS defaults.
4. Read-only and read/write login separation enforced at the connection, not only in SQL.
5. One failure model that never echoes driver text.
6. Optional pooling, limits and cancellation owned in one place.

## 3. Design

### 3.1 `ConnectionDefinition` (Plan 01 schema `connection/1`)
```yaml
schema: connection/1
name: warehouse
engine: sqlserver            # sqlserver | postgres | mysql | oracle | duckdb | odbc | flightsql | jdbc | <descriptor id>
dialect: sqlserver           # optional override (Plan 05 id)
access: readwrite            # readonly | readwrite; readonly forbids a write login (Build DDB-455)
address: { host: db1, port: 1433, database: dw }     # XOR connection_string (Sync rule)
connection_string_secret: secret:...                 # whole-string secrets (Build style)
auth: { mode: sql | integrated | none | token, user: svc_dw, password_secret: secret:dbdata:build:connection:warehouse:write }
read_login: {...}            # optional separate logins (Build: no fallback from read to write)
tls: { encrypt: true, verify: true, ca_file: null, trust_server_certificate: false }   # explicit opt-out only
timeouts: { connect_s: 15, command_s: 300 }          # defaults non-null (Proxy gap)
properties: { ApplicationName: x }                   # cannot set credential keys (policy)
extensions: { sync: {...}, proxy: { bitness: x86, passthrough: false, allow_writes: false }, build: {...} }
```
Product-specific fields live under `extensions.<product>`; `ConnectionConfig` script/hook bindings (Sync) move there, slimming the
shared type as the Sync review recommends. Proxy's `DataSourceOptions` becomes a view over the same record, plus pass-through rules.

### 3.2 Provider interface
```csharp
interface IDbProvider {
  EngineId Engine { get; }  ISqlDialect Dialect(ConnectionDefinition c);  EngineCaps Caps { get; }
  DbConnection Create(ConnectionContext ctx);         // unopened; ctx = definition + resolved credentials + policy
  Task<ConnectionTestResult> TestAsync(ConnectionContext ctx, CancellationToken ct);
  IConnectionStringComposer Composer { get; }
  ICatalogReader Catalog(ConnectionContext ctx);      // Plan 04
  ReadOnlySession ApplyReadOnly(DbConnection c);      // engine recipe (3.5)
}
```
Merges Sync `IDriver.CreateConnection` + optional `IConnectionTester/Previewer`, Proxy `IDbProvider`, Build `IRowSource` opening logic.
Readers/writers/staging stay out of this interface: they are Plan 07's (Sync's `IDriver` becomes a thin product-side facade
combining a provider with transfer components, so Sync's driver contract version is preserved for plugins during migration).

### 3.3 Provider packages
```
DbDataKit.Connections.Abstractions   definition, IDbProvider, ConnectionContext, policy, failure model
DbDataKit.Connections                registry, composers, pool/limits, env/secret login resolution, built-in generic ADO provider
DbDataKit.Connections.SqlServer / .Postgres / .MySql / .Oracle / .DuckDb / .Odbc / .FlightSql
DbDataKit.Connections.Descriptor     YAML-described engines over a DbProviderFactory (Sync Drivers.Descriptor)
DbDataKit.Connections.Libraries      on-demand NuGet provider install (Sync Libraries), hardened
DbDataKit.Connections.Jdbc           IKVM JDBC bridge (Sync Drivers.Jdbc); optional, heavy
DbDataKit.Connections.Plugins        compiled plugin loading with AssemblyLoadContext
```
Engine packages carry their client libraries; products reference only what they ship. Proxy references SqlServer/DuckDb/Odbc; Build SqlServer/Postgres/DuckDb/Odbc/FlightSql; Sync references all, plus descriptor/libraries/JDBC.

### 3.4 Secure composition (`IConnectionStringComposer`)
- Always use the engine's builder (`SqlConnectionStringBuilder`, `NpgsqlConnectionStringBuilder`,
  `OdbcConnectionStringBuilder` with brace quoting). The generic path gets an `OdbcQuoting` flag, fixing Sync 9.8.
- Credentials are applied last and **cannot be overridden by `properties`** (a deny-list of credential and host keys, shared with
  the Plan 02 detector); `fixed_properties` from descriptors apply first and are overridable only where marked.
- Pass-through credential injection (Proxy) strips every integrated-security style key per engine
  (`Trusted_Connection`, `Integrated Security`, `Authentication=*`, `AuthenticationType`, Kerberos keywords), then asserts none remain (S-10),
  caps lengths, and has a hostile-credentials test (`x};Driver={evil};UID={`).
- ODBC policy from Plan 04 (`AllowedDrivers`, `ForbidFileDsn`, `ForbidSaveFile`) applied to user-authored strings.
- TLS defaults: encrypt and verify on; `trust_server_certificate` is an explicit per-connection field and never set by catalog YAML
  (Sync 9.5, Proxy example config).
- Parameter markers: `ParameterStyleAdapter` rewrites `@name` to `?` for positional providers (ODBC) using the Plan 05 lexer and
  binds by recorded order. This fixes Sync's ODBC gap and lets Proxy delete `SqlMarkers`.

### 3.5 Read-only and login separation
`access: readonly` connections refuse a write login. Session recipes applied at open, each with tests against real engines:
SQL Server: dedicated read login plus `ApplicationIntent=ReadOnly` (routing only, not enforcement; document); Postgres:
`default_transaction_read_only=on` (Build already does this); DuckDB: `access_mode=READ_ONLY`, `enable_external_access=false`,
disable extension autoload/install, `lock_configuration=true` (Build's sandbox set, closes Proxy S-1 for DuckDB); ODBC:
`SQL_ATTR_ACCESS_MODE=READ_ONLY` where the driver supports it, otherwise rely on the login. Combined with the Plan 05 classifier
as defence in depth. Env-var login compatibility: `DBDATA_<APP>_<CONN>_<READ|WRITE>` and legacy `DBDATA_BUILD_*` via Plan 02.

### 3.6 Failure model
`ConnectionFailure { Kind: Login|Network|Timeout|Tls|Permission|Syntax|Unsupported|Other, Code, Engine }` created from provider
exceptions (Build `DriverFailure` generalised; Proxy `IsLoginFailure`). Public text is never raw driver output; raw text is available
only to local diagnostics behind an explicit flag, passed through the Plan 02 redactor. Proxy maps `Login` to `Unauthenticated` and
feeds its throttle (S-2).

### 3.7 Lifecycle: pooling, limits, cancellation
`ConnectionLease`/`IConnectionPool` per definition: max concurrency, acquire timeout, default command timeout, idle reaping, metrics;
wraps provider pooling rather than replacing it, and gives Proxy a global/per-user concurrency control (S-6) and a place for ODBC
catalog sessions (Plan 04). Optional for Sync/Build; default pass-through to the provider pool.

### 3.8 Flight SQL and the Proxy relationship
`DbDataKit.Connections.FlightSql` unifies Build's `FlightSqlRowSource`/`FlightSqlLogin` grammar with Proxy's `ProxyFlightClient`
(which reads Arrow-IPC-compressed streams stock clients cannot). Login grammar keeps `token`, `token_env`, `user/password`,
headers; `token_command` is gated by policy `allowExternalCommands` (off for web/MCP) and requires an absolute path (Build F-1).
Strategic option, to be decided: route all exotic engines (32-bit ODBC, vendor-only drivers) through the Proxy and let Sync/Build
see them as Flight SQL connections. Sync's direct ODBC would then be retained only as a local fallback.

### 3.9 Library installation hardening (Sync `Libraries/`)
Before extraction: validate `PackageId` (NuGet id regex) and `Version` (SemVer grammar); build the csproj with `XElement`; resolve and
prefix-check every path in `*Paths` helpers (`Directory.Delete` traversal); restrict `Source` to configured feeds; same
`ValidateName` for driver ids and file names (Sync 9.2, 9.3). These are prerequisites, fixed in Sync first.

## 4. Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Sync fixes for 9.2/9.3 in place (small PR) | High finding closed |
| 1 | Abstractions + definition schema + failure model + composer with policies; hostile-string test corpus | Package published |
| 2 | SqlServer and Postgres providers; Build adopts (replaces `Logins`/`OpenAsync` internals) | Build suite unchanged |
| 3 | ODBC provider (with Plan 04 policy, param adapter); Proxy `OdbcProvider` and Sync `GenericDriver` ODBC use it; add the missing Sync ODBC end-to-end test | ODBC parameterised statement works in Sync |
| 4 | DuckDB provider with sandbox recipe; Proxy enforces read-only (S-1) | Hostile SQL tests pass |
| 5 | Descriptor + libraries + JDBC + plugins packages extracted from Sync | Sync driver contract preserved |
| 6 | Flight SQL package unification; decide Proxy-as-bridge | Decision doc |
| 7 | Pool/limits; Proxy concurrency controls and defaults | Limits tests |

## 5. Risks
- Sync's `IDriver` is a public plugin contract (`ContractVersion`): keep a compatibility facade until plugins migrate.
- JDBC via IKVM is heavy and Sync-specific; keep it optional and out of the core package graph.
- Connection-string deny-lists must be maintained per engine; centralise them with tests.
- The Proxy-as-bridge decision affects Sync's investment in ODBC/JDBC; decide with usage data.

## 6. Acceptance
One connection definition schema; one composer used everywhere; no `Properties` credential override; TLS verify on by default;
ODBC parameterised queries work through the shared adapter; DuckDB sources are sandboxed read-only in Proxy; no product echoes raw driver text.
