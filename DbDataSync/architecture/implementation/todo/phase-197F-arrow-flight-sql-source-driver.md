# Phase 197F — Arrow Flight SQL as a source driver (basic and bearer-token auth)

**Status**: Built and verified against an in-process Flight SQL server; not yet committed, and not yet run against a
real proxy. See "What was built" and "Retrospective" at the end — the design below is the original plan, and where
the build departed from it the departures are listed there. Open decisions are marked **Decision**.
**Plan reference**: none — requested directly ("a driver to support Arrow SQL as a connection type, basic and bearer token authentication").

## What is being asked

A new built-in driver, `ArrowFlightSql`, that reads from any server speaking
[Apache Arrow Flight SQL](https://arrow.apache.org/docs/format/FlightSql.html) (Dremio, InfluxDB 3,
Doris, Ballista, a DuckDB/SQLite Flight SQL server, ...). **Source only** — one reader, no staging
provider, no writer — the same shape as `DuckDbDriver`. Two authentication methods:

- **Basic**: user id + password, exchanged for a bearer token by the Flight handshake.
- **Bearer token**: a pre-issued token sent as `Authorization: Bearer <token>`.

## What was checked while writing this

| Question | Finding | Consequence |
| --- | --- | --- |
| Is there an ADO.NET surface for Flight SQL? | No. `Apache.Arrow.Adbc.Client` (0.24) wraps an *ADBC* driver as `DbConnection`/`DbCommand`, but the managed `Apache.Arrow.Adbc.Drivers.FlightSql` (0.21) is a ~13 KB shim: its only required option is `FLIGHT_SQL_SERVER_ADDRESS`, with no handshake/basic-auth, TLS or metadata options visible in the assembly. The full-featured ADBC Flight SQL driver is the Go one, loaded as a native library. | The ADBC route buys little here and adds a native dependency per platform. Rejected. |
| Is there a usable Flight SQL client in .NET? | Yes: `Apache.Arrow.Flight.Sql` 23.0.0 (depends only on `Apache.Arrow.Flight` 23.0.0). `FlightSqlClient` has `ExecuteAsync`, `GetExecuteSchemaAsync`, `GetCatalogsAsync`, `GetDbSchemasAsync`, `GetTablesAsync`, `GetPrimaryKeysAsync`, and takes `FlightCallOptions` (per-call headers). | **Build directly on it**, with a small ADO.NET adapter on top (below). |
| Does the repo's driver contract need ADO.NET? | Yes. `IDriver.CreateConnection` returns `DbConnection`; `IChangeReader.ReadChangesAsync` takes one; `BatchReloadReader` and `ResultSetSchema` (reused by `DuckDbDriver`) read through `DbDataReader`. | The adapter is what lets the existing generic reader be reused unchanged. It is also where most of the work is. |

## Design

### Project: `src/DbDataSync.Drivers.ArrowFlightSql`

Modelled on `DbDataSync.Drivers.DuckDb`: references Core, Drivers.Abstractions and Drivers.Generic.

- `ArrowFlightSqlDriver : IDriver, IConnectionTester, IDialectProvider` — `DriverType` = new
  `DriverIds.ArrowFlightSql` (`"ArrowFlightSql"`), `DisplayName` = "Arrow Flight SQL", `DefaultPort` null
  (there is no conventional one; Dremio uses 32010, others differ — the form leaves it blank rather than
  pre-filling a wrong number), `Readers = [BatchReloadReader]`, no staging providers, no writers. *(As built there
  is one such class registered once per SQL dialect — see "SQL dialects".)*
- `FlightSqlConnection : DbConnection`, `FlightSqlCommand : DbCommand`, `FlightSqlDataReader : DbDataReader`
  — a read-only ADO.NET adapter. `Open` creates the gRPC channel and authenticates; a command runs
  `FlightSqlClient.ExecuteAsync`, then streams every endpoint's `DoGet` as Arrow record batches; the reader
  walks batch by batch, row by row. Parameters, transactions and `Execute*NonQuery` throw
  `NotSupportedException` with a message saying the driver is read-only.
- `ArrowFlightSqlDialect : SqlDialect` — identifier quoting and row-limit rendering. Flight SQL leaves the
  SQL dialect to the server behind it, so this starts with ANSI double-quote quoting and `LIMIT n`, and
  `SupportsTieSafeRowLimit` false until a server is found where it holds (same posture as `MySqlDialect`).
- Type mapping: Arrow type → CLR value in `FlightSqlDataReader.GetValue`, and → a dialect type name for
  `ListColumnsAsync`. Covers ints, floats, decimal128/256, bool, utf8, binary, date32/64, timestamp (with
  and without zone), time, and falls back to `ToString()` for list/struct/map/union so a read never fails
  on a column nobody mapped.

### SQL dialects — added at build time

The target is a SQL proxy, and a proxy passes SQL through to whatever engine it fronts, so the dialect cannot be
fixed. It also cannot be chosen per connection without reworking the stack: readers are constructed with their
dialect once, when the driver is, and a dozen call sites resolve a dialect from the *driver*
(`IDialectProvider`), not the connection.

**Decision (taken at build time): one driver id per dialect.** `ArrowFlightSql` (ANSI), `…MsSql`, `…Postgres`,
`…MySql`, `…Oracle`, `…DuckDb`, all one class (`ArrowFlightSqlDriver`) parameterised by a `Variant`, registered
together (`ArrowFlightSqlDriver.CreateAll()`) and listed by name in the connection editor ("Arrow Flight SQL
(PostgreSQL syntax)"). No change to Core's contracts, the readers, or any call site. The cost is a longer engine
picker, and that a connection's dialect is part of its driver type (change it by changing the driver, not a field).
Moving to a per-connection setting later is possible but touches every `IDialectProvider` call site.

`FlightSqlDialect` wraps the named engine's dialect and forwards every overridable member of `SqlDialect`
(a reflection test fails if one is added without being forwarded). It differs from the engine in three ways:
selecting a database sends no statement; column types are read as ANSI names first (an Arrow schema has no
engine-specific names), then the engine's own; and the identifier quote characters are supplied explicitly because
the base class reads them through protected members. A new `AnsiDialect` in Core backs the plain variant and the
ANSI type names.

### Authentication

`AuthMode` today is `SqlAuth | IntegratedAuth | None` and says it is deliberately *not* a list of every
engine's mechanism. Bearer token fits that enum without changing it:

**Decision — how bearer is expressed.** Recommended: **a driver-declared `authMethod` parameter, not a new
`AuthMode` member.**

| Option | Notes |
| --- | --- |
| **A. `authMethod` dropdown (`Basic` / `Bearer`) via `ConnectionParameters` override** *(recommended)* | Basic = `AuthMode.SqlAuth` with user id + password, exactly as every driver already does. Bearer = `AuthMode.SqlAuth` with **no user id**, the secret-store credential *is* the token. The driver owns the distinction; Core, the config schema and every other driver are untouched. The token is stored and resolved like a password, so it is never in git-tracked config. |
| B. New `AuthMode.Bearer` | Honest in the enum, but touches Core, validation, the form, the CLI and every driver's `switch`, for one driver's benefit, against the enum's own stated intent. |
| C. Token in `Properties` | Rejected — `Properties` is plaintext in git-tracked config. |

With option A, `ConnectionParameters` returns the shared set minus `Database` handling that does not apply,
plus `authMethod` (Recalc = true, so the User ID field hides itself for Bearer) and `useTls` /
`skipCertificateValidation` (the latter with a warning in its description; default off). `IntegratedAuth`
and `None` are offered as `None` ("no authentication — open server") only.

- **Basic**: the Flight handshake with the credentials as a Basic `authorization` header on that one call; the
  bearer header the server answers with replaces the password on every later call. (There is no
  `AuthenticateBasicToken` helper in `Apache.Arrow.Flight` 23.0.0 — the handshake is written out by hand. A server
  that issues no token, such as a proxy checking Basic on every call, gets the Basic header on each call instead.)
- **Bearer**: header `authorization: Bearer <credential>` attached directly, no handshake.
- The credential is applied inside `CreateConnection` only and never appears in a connection string, a log
  line, or an exception message (the adapter's own `ConnectionString` property is the address only).

### Addressing

`host` + `port` (`grpc://` or `grpc+tls://` by `useTls`) or `connectionString` as a full URI; same two
modes as every driver. `Database` maps to the Flight SQL **catalog** (sent as the `catalog` header /
command option for servers that honour it) and is optional.

### Metadata

`ListDatabasesAsync` → `GetCatalogsAsync`; `ListTablesAsync` → `GetTablesAsync` (catalog filter, table
types `TABLE` and `VIEW`); `ListColumnsAsync` → `GetTablesAsync(includeSchema: true)` and read the Arrow
schema, with `GetPrimaryKeysAsync` for key-ness where the server implements it (a server that returns
`Unimplemented` yields no keys, not an error). **Unlike DuckDB these are real catalogs**, so the mapping
editor's table pickers are populated rather than replaced by the query editor. Servers that do not
implement the metadata RPCs degrade to the query editor path (empty lists), which the reader supports
already.

### Connection test

`TestAsync` runs `SELECT 1`, reports the server name/version from `GetSqlInfo` when the server provides it
(otherwise just "Flight SQL"), and maps `RpcException` to a `ConnectionTestResult(false, ...)` with the
gRPC status detail — `Unauthenticated` is reported as such, since "wrong password" and "wrong token" are
the failures an operator will actually hit. `DefaultTestQuery` = `SELECT 1`.

### Library packaging

This repo does not ship provider assemblies with the app: phase 109h/109i put them under
`<repo>/libraries/<id>/` and load them through `LibraryRegistry`. `Apache.Arrow.Flight.Sql` brings
`Apache.Arrow`, `Apache.Arrow.Flight` and `Grpc.*` — all managed, no native assets. The plan is to follow
the DuckDB treatment: `ExcludeAssets="runtime"` on the package reference, a new `KnownLibraries` entry
`arrow-flight-sql`, `RequiredLibraryId => "arrow-flight-sql"`, and the installer wired through the same
place `EnsureDuckDbInstalledAsync` is. **Decision (resolved at build time): plain package reference for v1.** The library
mechanism installs one package whose `DbProviderFactory` it can name (`KnownLibraries`); Flight SQL is a set of
managed packages (`Apache.Arrow`, `Apache.Arrow.Flight`, `Grpc.*`) with no factory, so it would need a multi-package
catalog entry that nothing has exercised, plus changes to the 109j surface check. The assemblies therefore ship with
the tool and `RequiredLibraryId` is null. Moving them behind `LibraryRegistry` is a follow-up — and one meant to be
done, since it is the standard for every other driver: see
`architecture/planning/todo/follow-up-phase-197-arrow-flight-sql-assemblies-ship-inside-the-tool.md`.

### Registration

Same places `DuckDbDriver` appears: `DriverIds`, `BuiltInDrivers.cs`, `TaskRunner/Program.cs`,
`DbDataSyncHost.cs`, `BuiltInDriverLibraries.cs`, `ReadinessChecks.cs`, `DbDataSync.slnx`, plus
`docs/drivers-and-libraries.md` and the driver tables in the README.

## Tests

- **Unit** (no server): Arrow→CLR value mapping per type; the `FlightSqlDataReader` over hand-built
  `RecordBatch`es (multiple batches, nulls, zero rows, a schema-only result); `ConnectionParameters`
  visibility for Basic vs Bearer; header construction for both methods; `ConnectionConfig` →
  address/URI rendering; credential never present in `ConnectionString` or in exception text.
- **Contract**: the repo's `ChangeReaderFirstPassContractTests` / driver-registration tests pick the new
  driver up by being registered.
- **Integration**: an in-process Flight SQL server built from `Apache.Arrow.Flight` test types serving a
  fixed table, started with (a) basic-auth required, (b) bearer required, (c) no auth — checking right
  and wrong credentials for each. This is the only way to prove the handshake and header paths for real;
  skipping it would leave the two features that were asked for unexercised. If the in-process server
  proves too heavy, the fallback is a docker-compose service (`docker/`), which the existing integration
  tests already use for the other engines.
- Per the project's CDC flake policy this adds no CDC tests.
- **As built**: 137 tests in `tests/DbDataSync.Drivers.ArrowFlightSql.Tests`. The integration tests run against
  `FakeFlightSqlServer`, an in-process server on a real loopback socket (Kestrel, HTTP/2, TLS with a throwaway
  self-signed certificate for the certificate tests) — the "docker-compose fallback" was not needed.

## Not in this phase

- **Writes / staging** (`ExecuteUpdate`, `CommandStatementIngest`): the source-only scope was chosen
  deliberately. Ingest is the natural follow-up and would reuse the connection and auth.
- **OAuth / token refresh / mTLS client certs**: a bearer token that expires is a re-save by the operator
  for now. Worth a follow-up if the target servers issue short-lived tokens.
- **Incremental reads**: `BatchReloadReader` is a full reload. Watermark-style incremental reads over a
  query work through the existing generic watermark reader if it can be paired with this dialect — to be
  checked against `BatchReloadReader`'s siblings, not assumed.
- **Parameterised and prepared statements**: the generic reader's binding path is not used by a reload,
  but this should be confirmed before the first real run against a server.
- **Large results across multiple endpoints in parallel**: endpoints are read sequentially, in order.

## Open questions for the author

1. Which server(s) will this be pointed at first? It decides the dialect details (quoting, `LIMIT`) and
   whether to test against a real instance in docker rather than only the in-process one.
2. Is TLS with a private CA the expected case? If so a CA-certificate setting is needed in v1, not just
   "skip validation".
3. Confirm option A for bearer (above), and the library-packaging approach.


## What was built

- `src/DbDataSync.Drivers.ArrowFlightSql` — `ArrowFlightSqlDriver` (six variants), `FlightSqlConnection` /
  `FlightSqlCommand` / `FlightSqlDataReader` / parameter classes (the read-only ADO.NET adapter),
  `FlightSqlSettings`, `FlightSqlDialect`, `FlightSqlMetadata`, `ArrowValues` (Arrow ⇄ CLR and ANSI type names),
  `ArrowParameters`, `ParameterMarkers`, `FlightSqlValueBinding`, `FlightSqlException`.
- Core: `AnsiDialect`, the six `DriverIds` constants and `DriverIds.ArrowFlightSqlVariants`.
- Registered in the API host and the task runner; added to the built-in sets in `DriversController` and
  `ReadinessChecks` (without which they would list as "descriptor" drivers and fail the readiness check that
  every connection's driver resolves). **Not** added to `Cli/BuiltInDrivers.cs`, which is only the drivers that
  have a `RequiredLibraryId`.
- Docs: `docs/drivers-and-libraries.md` (new "Arrow Flight SQL" section), `README.md`.
- Settings that are not in `ConnectionConfig` (`authMethod`, `useTls`, `skipCertificateValidation`,
  `parameterMode`) are stored in `ConnectionConfig.Properties` — the persisted shape has no other place — and
  declared as `properties.<key>` parameters, which is the only name the web form round-trips. An absent key means
  the default (basic, TLS on, validate certificates, prepared).
- **Parameters**: the readers write named markers in each dialect's own spelling; Flight SQL prepared statements are
  positional, so `ParameterMarkers` rewrites them to `?` (skipping string literals, quoted identifiers, comments,
  `::` casts, `@@vars` and `$tag$` bodies) and binds typed Arrow values. `parameterMode=Inline` renders literals
  through the dialect instead, for a proxy that has no prepared statements.

## Retrospective

**Departures from the plan**
- Dialect handling (above) was not in the plan.
- Plain package reference rather than the `LibraryRegistry` treatment (above).
- Basic auth is a hand-written handshake; there is no helper.
- The plan said `Database` is sent as a header for servers that honour it. It is not sent at all — no standard
  header exists to send. It is the catalog metadata calls look in, and nothing more.
- The connection test reports the server as "Arrow Flight SQL" without querying `GetSqlInfo`; it is optional,
  returns a union-typed result, and a property that can throw is how phase 171V's crash happened.

**Compressed data, added after the first build.** A SQL proxy exists to compress, so reading compressed batches is a
requirement, not a nicety. Two things were wrong, and installing a package fixed neither alone:
- `Apache.Arrow.Flight` 23.0.0 reads each `DoGet` message with an Arrow stream reader built **without a codec
  factory**, so an LZ4- or Zstd-compressed batch throws on read whatever is installed, and nothing in the Flight
  client's public surface takes one. `FlightDataReader` therefore makes the `DoGet` call itself on the channel's
  call invoker (the generated gRPC types are internal; the two messages involved are a one-field ticket and a
  `FlightData` of which three fields are read, so they are marshalled by hand), re-frames each Flight message as an
  Arrow IPC message, and reads it with `ArrowStreamReader` given `Apache.Arrow.Compression`'s codec factory. Each
  batch is read from `schema + dictionaries so far + that batch`; a dictionary message reads as a stream with no
  batch, which is how it is told apart. Every read — rows and metadata — goes through it.
- **Run-end-encoded** columns (a column stored as run ends plus one value per run) were not expanded and came back
  as a placeholder string. They are now read per row from their run, and report their value type's name.
Tests build real LZ4/Zstd/dictionary/REE IPC streams, split them into Flight messages the way a server would, and
read them back; one asserts the test data is genuinely compressed and unreadable without the codec, so a pass cannot
be an artefact of data too small to compress.

**Found by running it against a real gRPC peer, not by inspection** — each of these would have shipped:
- `FlightSqlClient` reports a failed call as `InvalidOperationException("Failed to get flight info")` with the
  `RpcException` *inside it*. Catching `RpcException` missed most failures, including a wrong password, so the
  connection test would have thrown instead of reporting. `FlightSqlException.TryWrap` walks the inner chain.
- `GetTablesAsync` turns an omitted filter into an **empty string** with the field marked present. An empty
  catalog means "tables that have none" and an empty pattern matches nothing, so an unfiltered browse returned an
  empty list. The metadata commands are now built directly with the fields left unset.
- A TLS failure arrives as gRPC `Internal`, not `Unavailable`, with the cause only in the detail text; the error
  now says so and names the "Skip certificate validation" setting.
- `cond ? DateTime : DateTimeOffset` has the natural type `DateTimeOffset`, so an unzoned timestamp silently
  became an instant in the machine's own zone. Two returns now, with a test.
- Dollar-quoted strings (`$$ … $$`) were not treated as quoting, so a marker inside one would have been
  replaced.

**Verified**: both auth methods (including that the issued token, not the password, is used after the handshake and
that a wrong password/token fails without appearing in the error), the no-token proxy fallback, TLS with and without
certificate skipping, streaming across record batches with nulls, the schema taken from the first batch when
`FlightInfo` omits it, prepared and inline parameters, metadata (and its absence), the connection form and the
save-time validation the API applies, and `BatchReloadReader` end to end through a variant. Existing Core (318),
Abstractions (68), Cli readiness/driver (29) and the API driver/contract tests that do not need a database pass; the
four API tests that failed need Postgres/MySQL servers this sandbox does not have (connection refused).

**Not verified**
- Against a real Flight SQL server or proxy. The fake implements the protocol as the spec describes it; a real
  server's quirks (what it does with `?` markers, what it reports as `table_type`, whether it supports prepared
  statements at all, how it spells timestamps) are the likeliest source of the first bug reports.
- The web form: the descriptors are validated server-side and the form's value mapping was read, but the page was
  not driven in a browser.
- Compressed batches **from a real server**. The in-process fake cannot send them (its Flight server API takes no
  compression options), so compressed reads are proven at the message level — real IPC bytes through the same
  assembler — not over the wire. The wire path itself (the hand-written `DoGet` call) is exercised by every other
  integration test, uncompressed.
- Which codecs a given proxy uses beyond LZ4-frame and Zstd (the two Arrow defines).
- Very large results and long-running reads (command timeout is applied as a gRPC deadline on the whole stream).
- Cancellation mid-stream.

**Follow-ups**
- Move the Arrow/gRPC/compression assemblies behind `LibraryRegistry` (multi-package catalog entry) — see the
  follow-up doc above.
- Report the server's compression in the connection test, so "your proxy sends Zstd and it was read" is visible.
- A CA-certificate setting, so a private authority can be trusted without skipping validation (the user said
  "skip" is enough for v1 and other options come later).
- Server version from `GetSqlInfo`, where the server offers it.
- A per-connection dialect, if one driver id per dialect proves awkward.
- Writes (`CommandStatementIngest`), token refresh/OAuth, mTLS — as listed under "Not in this phase".
