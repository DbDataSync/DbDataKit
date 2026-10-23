# Drivers and libraries

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · **Drivers and libraries** · [State database](state-database.md) · [Building from source](development.md)

Two separate concepts, easy to conflate because they usually show up together:

- **A library** is the ADO.NET provider a connection actually opens through — `Microsoft.Data.SqlClient`,
  `Npgsql`, `MySqlConnector`, `DuckDB.NET.Data`, and so on. DbDataSync doesn't ship any of these
  inside the tool itself (DuckDB is the one exception — see below); each is restored once, on the
  machine that needs it, the first time something asks for it.
- **A driver** is what actually runs a replication against one engine — the readers, staging providers,
  and writers DbDataSync's pipeline dispatches to for a given `DriverType`. There are three kinds: the
  **built-in** drivers compiled into the tool (`MsSql`, `Postgres`, `MySql`, `Oracle`, `DuckDb`, and the
  source-only Arrow Flight SQL family), any
  number of **descriptor** drivers (a YAML file, no rebuild needed), and any number of **compiled plugin**
  drivers (your own compiled `IDriver`, a heavier lift covered only briefly below).

Every driver — built-in or descriptor — names exactly one library its connectivity depends on
(`IDriver.RequiredLibraryId` in code; the descriptor's own `library:` field in YAML). Trying to use a
driver whose library isn't installed yet fails loudly, naming the exact command that fixes it — never a
silent fallback or a confusing connection-string error.

## Libraries

An installed library lives at `<RepoRoot>/libraries/<id>/` — `library.json` (its id, the resolved
`DbProviderFactory` type, and the exact NuGet package(s)/version(s) it was restored from) plus a `lib/`
directory holding the actual restored assemblies. `LibraryRegistry` reads every manifest at startup and
arms an `AssemblyLoadContext.Default.Resolving` hook, so the real assembly loads the first time
something actually asks for it rather than being loaded — or shipped — up front.

### The bundled catalog

A curated starter set of eight, each with a short id and a pinned starter version — `TryGetById`/`TryGet`
in `KnownLibraries.cs`. Installing a non-catalog package works exactly the same way; the catalog only
saves typing out a package id and factory type by hand.

| id | package | engine |
| --- | --- | --- |
| `mysql-connector` | `MySqlConnector` | MySQL / MariaDB |
| `microsoft-data-sqlclient` | `Microsoft.Data.SqlClient` | SQL Server / Azure SQL |
| `npgsql` | `Npgsql` | PostgreSQL |
| `microsoft-data-sqlite` | `Microsoft.Data.Sqlite` | SQLite |
| `oracle-managed-data-access` | `Oracle.ManagedDataAccess.Core` | Oracle |
| `system-data-odbc` | `System.Data.Odbc` | anything reachable through an installed ODBC driver |
| `firebird-client` | `FirebirdSql.Data.FirebirdClient` | Firebird |
| `duckdb` | `DuckDB.NET.Data` (plus the DuckDB 2.0.0 engine) | embedded, not a server |

`duckdb` is the one exception to "nothing ships inside the tool": DbDataSync's own verification and
DuckDB-kind custom segmenting strategies run on it regardless of which replication engines a deployment
ever configures, so it installs itself automatically on every `serve` start rather than waiting to be
asked for. Nothing else on this list installs itself.

The package carries only the managed bindings. The engine itself (DuckDB 2.0.0, the version this release is built for) is downloaded
from DuckDB's release page for the machine's platform at the same time, and checked against checksums recorded in the release: a
file that does not match is refused. It is available for Linux (glibc) on x64 and arm64, Windows x64 and macOS on x64 and arm64.
On a machine without internet access, download `duckdb-shared-libs-<os>-<arch>.tar.gz` for the platform from the DuckDB 2.0.0 release
elsewhere, put it in a directory, and set `DBDATASYNC_DUCKDB_ENGINE_SOURCE` to that directory before the first `serve`; the docker
images already contain it.

### `dbdatasync library install`

```
dbdatasync library install <packageId>[ <packageId>...] [--as <id>] --version <v> [--factory-type type] [--source feed]
```

| flag | required | notes |
| --- | --- | --- |
| `<packageId>...` | yes (positional) | one or more NuGet package ids — or a single catalog id (`npgsql`, `mysql-connector`, ...) as shorthand for its real package id and factory type |
| `--version <v>` | yes | pinned, never "latest" — required even for a catalog id |
| `--as <id>` | no | the library's own id, if different from the first package id (a multi-package library whose primary assembly isn't the first argument) |
| `--factory-type <type>` | only for a non-catalog package | assembly-qualified `DbProviderFactory` type name, e.g. `"Npgsql.NpgsqlFactory, Npgsql"`; omit it for a catalog id, or let restore-then-reflect try to find the one `DbProviderFactory` subclass in the freshly-restored assemblies on its own |
| `--source <feed>` | no | a NuGet source override |

```sh
# Catalog shorthand — factory type resolved from KnownLibraries, version still required.
dbdatasync library install npgsql --version 9.0.3

# A package not in the catalog, with the factory type spelled out explicitly.
dbdatasync library install SomeVendor.Data.Provider --version 3.1.0 \
  --factory-type "SomeVendor.Data.SomeFactory, SomeVendor.Data.Provider"
```

On a machine with no .NET SDK to restore with (the runtime-only container image), an install of a
catalog id at its exact pinned version is copied from an in-image cache instead — no SDK, no network.
Anything else is written as a manifest and left pending until `config library sync` runs somewhere with
an SDK.

### Other library commands

| command | does |
| --- | --- |
| `dbdatasync library sync [<id>]` | re-restores one library (or, with no id, every installed library) at its pinned version — how a pending install (above) gets completed, or how a hand-edited `library.json` version bump takes effect |
| `dbdatasync library list` | every installed library, its packages/versions, its factory type, and whether it currently resolves, doesn't resolve, or is still pending a `sync` |
| `dbdatasync library uninstall <id>` | deletes `<RepoRoot>/libraries/<id>` entirely |
| `dbdatasync library validate <id> --connection <name>` | for a library backing a *built-in* driver only: opens the named connection for real and drives that driver's actual staging/writer pipeline against a real scratch table, reporting rows written — proof the library resolves correctly, not just that the manifest parses |

### From the web console

The Admin → **Libraries** tab does the same thing as `config library install`/`list`/`uninstall` —
including a NuGet search box, gated by the same `DbDataSync:Nuget:Search:Mode` setting documented in
[Configuration](configuration.md). Both paths write the identical `<RepoRoot>/libraries/*` files, so
either one is visible to the other.

## Drivers

### Built-in drivers

Compiled into the tool — no install step for the driver itself, ever. Four of the first five still need their
library installed before they can actually connect to anything, exactly like a descriptor driver does:

| `DriverType` | library it needs | notes |
| --- | --- | --- |
| `MsSql` | `microsoft-data-sqlclient` | Change Tracking, CDC, and TriggerAudit readers |
| `Postgres` | `npgsql` | Watermark, TriggerAudit, BatchReload, KeyReconcile and `PgLogicalSlot` (logical decoding through wal2json) readers; binary `COPY` staging. `pgoutput` is not supported yet |
| `MySql` | `mysql-connector` | MySQL and MariaDB, one driver — Watermark, TriggerAudit, BatchReload and KeyReconcile readers; the binlog-based native alternative is not supported yet |
| `Oracle` | `oracle-managed-data-access` | Watermark, TriggerAudit, BatchReload, KeyReconcile, and Flashback Version Query readers; LogMiner is not supported yet |
| `DuckDb` | `duckdb` (installs itself — see above) | embedded, file path or `:memory:`, nothing to authenticate to |
| `ArrowFlightSql`, `ArrowFlightSqlMsSql`, `ArrowFlightSqlPostgres`, `ArrowFlightSqlMySql`, `ArrowFlightSqlOracle`, `ArrowFlightSqlDuckDb` | none — ships with the tool | source only, read over Arrow Flight SQL; one driver per SQL dialect — see [Arrow Flight SQL](#arrow-flight-sql) |

Configuring a connection with `DriverType: MsSql`/`Postgres`/`MySql`/`Oracle` before its library is
installed doesn't fail at save time — the config itself is valid — it fails the first time something
actually tries to open it, with:

```
Library 'microsoft-data-sqlclient' is not installed. Install it with `dbdatasync library install microsoft-data-sqlclient`.
```

Run that once (pin whatever version you want — see above) and every subsequent connection using that
driver just works, with no further setup.

### Arrow Flight SQL

A source-only driver for any server that speaks [Apache Arrow Flight SQL](https://arrow.apache.org/docs/format/FlightSql.html) —
typically a SQL proxy in front of another engine. It has one reader, a full reload of a table or query
(`BatchReload`), and no writer, so it is a place to read *from*.

**Pick the driver for the SQL the server accepts.** Flight SQL carries SQL text to whatever engine sits
behind it, so quoting identifiers, limiting rows and spelling literals follow that engine, not the
protocol. Each dialect is its own `DriverType`, listed in the connection editor by name:

| `DriverType` | SQL syntax used |
| --- | --- |
| `ArrowFlightSql` | plain ANSI SQL (`"quoted"` identifiers, `FETCH FIRST n ROWS ONLY`) — when the server is not any of the below |
| `ArrowFlightSqlMsSql` | SQL Server (`[bracketed]` identifiers, `TOP`) |
| `ArrowFlightSqlPostgres` | PostgreSQL |
| `ArrowFlightSqlMySql` | MySQL / MariaDB (`` `backtick` `` identifiers) |
| `ArrowFlightSqlOracle` | Oracle |
| `ArrowFlightSqlDuckDb` | DuckDB |

**Connection settings**

| Setting | Notes |
| --- | --- |
| Host & port, or Connection string | An address: `host:port`, or a URI — `grpc://` and `grpc+tcp://` (plain text), `grpc+tls://`, `http://`, `https://`. There is no default port. |
| Auth mode | **Basic or bearer token**, or **None** for an open server. Integrated authentication is not offered. |
| Method | **Basic** — a user id and password, exchanged for a session token by the Flight handshake (a server that issues none gets the Basic header on every call). **Bearer** — a pre-issued token sent as `Authorization: Bearer`. |
| Password / Bearer token | Kept in the secret store like any password, never in the committed config, and never part of the connection string or an error message. |
| Catalog | Optional. The catalog that table browsing looks in. |
| Use TLS | On by default. |
| Skip certificate validation | Off by default. Accepts **any** certificate — self-signed, expired, wrong host — for a proxy you can't give a trusted one. The traffic is still encrypted but the server's identity is not checked, so use it only on a network you trust. |
| Data source | Optional. Sent as the `x-datasource` header, for a proxy that fronts several databases and picks one by it. Empty uses the user's default. |
| Compression | Optional. Sent as the `x-compression` header (`none`, `gzip`, `zstd`, `lz4`) to override a proxy's default codec. Empty takes the server's choice. |
| Parameters | **Prepared** (default): values are bound to a prepared statement and never become part of the SQL text. **Inline**: values are written into the statement as literals — set it to skip the prepared attempt for a server that cannot bind parameters. Either way, if a server answers a bound parameter with *Unimplemented* (some proxies prepare only parameterless statements) the driver switches to literals for the rest of that connection. |

**What to expect.** Browsing tables and columns uses Flight SQL's metadata calls, and many proxies implement
none of them: the lists then come back empty and the mapping editor offers the query editor instead. The
connection test runs `SELECT 1` (`SELECT 1 FROM DUAL` for Oracle) — set the connection's own test query if
the server needs something else. Column types are reported in ANSI names (`bigint`, `timestamp with time
zone`, ...) whatever the engine behind the proxy calls them, because an Arrow schema carries no
engine-specific names. **Compressed results are read automatically** (Arrow's LZ4 and Zstd batch compression,
which is what a compressing proxy sends), as are dictionary- and run-end-encoded columns — nothing to configure.
A result's endpoints are read in order from the server the connection is already
talking to.

### Descriptor drivers

A descriptor is a YAML file at `<RepoRoot>/drivers/<id>/driver.yaml`, loaded at process startup by both
the API and `DbDataSync.TaskRunner` — no compiling, no restart-the-build. It describes an engine
DbDataSync has no hand-written driver for in terms the existing generic readers/staging/writers already
know how to drive: identifier quoting, parameter placeholders, row-limiting syntax, and a native-type →
canonical-type map. It reads from `information_schema` for catalog discovery, so it fits any engine that
exposes one; the `mysql.generic` starter template is the closest fully worked example.

| field | required | meaning |
| --- | --- | --- |
| `id` | yes | the driver's own id — what `DriverType` on a connection is set to |
| `displayName` | yes | shown in pickers |
| `library` | yes | the id of an installed (or about-to-be-installed) library — see [Libraries](#libraries) above. This is the field that ties a descriptor driver to the ADO.NET provider it actually runs on; the descriptor never repeats the package id or factory type, since the library's own `library.json` already carries both. |
| `dialect.quoteIdentifier` | yes | `backtick`, `doubleQuote`, or `bracket` |
| `dialect.parameterPrefix` | yes | `"@"`, `":"`, or `"?"` (positional) |
| `dialect.rowLimit` | yes | `limitOffset` (`LIMIT n OFFSET m`) or `offsetFetch` (`OFFSET m FETCH n`) |
| `dialect.catalog` | yes | only `informationSchema` is supported today |
| `dialect.supportsChangeDatabase` | no (default `true`) | `false` makes a mapping naming a different database a config-time error |
| `dialect.defaultDatabase` | no (default `""`) | what to connect to before a mapping names one |
| `dialect.connectionStringKeys` | no | override the connection-string key names (`host`/`port`/`database`/`username`/`password`/`connectTimeout`) if this provider spells them differently — defaults to `Microsoft.Data.SqlClient`-shaped names |
| `typeMap` | no (default empty) | native type name (optionally parameterized, e.g. `"decimal(p,s)"`, `"varchar(n)"`) → a canonical kind, either a bare name (`datetime: Timestamp`) or an object (`{ kind: String, length: n, unicode: true }`). Anything not listed maps to `Unmappable`, which provisioning reports as unsupported rather than guessing. |
| `capabilities.readers`/`.staging`/`.writers` | yes | which of `DbDataSync.Drivers.Generic`'s existing engine-neutral strategies (`Watermark`, `BatchReload`, `StagingTable`, `DeleteInsert`, ...) this driver offers — a descriptor can only opt into strategies that already exist, never introduce a new one |

**A descriptor driver never auto-provisions a target table.** `RenderColumnType` — the DDL-generation
path — deliberately throws rather than guess at a `CREATE TABLE` for an engine it only knows through a
type-name table. Use it as a source (or as a target whose tables you create yourself) freely; write a
compiled driver if you need real provisioning for a new engine.

A small, curated set of starter descriptors ships as embedded resource templates
(`KnownDrivers.cs`) — currently just `mysql.generic`, seeded via `--from mysql.generic` below. Anything
else starts from a blank shell and gets filled in by hand.

### `dbdatasync driver install`

```
dbdatasync driver install <id> [--library <name>] --version <v> [--factory-type type] [--from <knownDriverId>] [--display-name name]
```

| flag | required | notes |
| --- | --- | --- |
| `<id>` | yes (positional) | the new driver's id |
| `--version <v>` | yes | the library's pinned version (same meaning as `config library install --version`) |
| `--library <name>` | only without `--from` | which library this driver runs on; with `--from`, defaults to that template's own bound library |
| `--factory-type <type>` | only for a non-catalog library with no known factory type | same as `config library install` |
| `--from <knownDriverId>` | no | seed the descriptor from a starter template (today: `mysql.generic`) instead of a blank shell |
| `--display-name <name>` | no | defaults to `<id>` |

If the named library is already installed, it's reused as-is — trusted over a possibly-mismatched
`--factory-type`/catalog guess — rather than reinstalled. The command refuses to run at all if
`<RepoRoot>/drivers/<id>/driver.yaml` already exists; edit it directly instead of trying to overwrite it
through the CLI.

```sh
# From the starter template — installs the mysql-connector library too, if not already there.
dbdatasync driver install my.mysql --version 2.4.0 --from mysql.generic

# A blank shell for an engine with no template, reusing a library already installed under a
# different driver.
dbdatasync driver install my.other-engine --library mysql-connector --version 2.4.0
```

A minimal (`--from`-less) `driver.yaml` starts with an empty `typeMap: {}` and a comment noting that
every native type is `Unmappable` until you fill it in — deliberately not a guess.

### Compiled plugin drivers

```
dbdatasync driver install <id> --kind compiled --package <packageId> --version <v> --assembly <name.dll> --driver-type <FQTypeName> [--source feed]
```

Restores a package into a private `<RepoRoot>/drivers/<id>/lib/` (never the shared `libraries/` a
library lives in — a compiled driver's package is its own, not something another driver or the state
store might also resolve) and writes a `driver.json` naming the assembly and the compiled `IDriver` type
to load from it. This is a heavier path than a descriptor — you write and compile the `IDriver`
implementation yourself — and is out of scope for this page beyond the command shape above.

### Other driver commands

| command | does |
| --- | --- |
| `dbdatasync driver list` | every driver on disk — descriptor (`library: <id>`) or compiled (`assembly:`/`type:`) — plus a `[FAILED TO PARSE: ...]` line for one that doesn't, rather than crashing |
| `dbdatasync driver uninstall <id>` | deletes `<RepoRoot>/drivers/<id>`. Its library is untouched — `config library uninstall` separately if nothing else still needs it |

### From the web console

Admin → **Drivers** lists every registered driver — the five built-ins plus whatever descriptors or
compiled plugins are on disk — and offers an "Add" flow for the same starter catalog `--from` draws
from (`POST /api/drivers/from-catalog`). Admin → **Libraries** (above) is where the library a new
driver needs gets installed, either before or as part of adding the driver. A newly installed driver
needs a process restart to take effect — both the API and any already-running `TaskRunner` worker
loaded their driver set once, at startup.

## Worked example: a descriptor driver, with no rebuild

**For a real MySQL or MariaDB replication, use the built-in `MySql` driver above instead** — it has
real provisioning support, doesn't need a restart after installing its library, and offers the same
readers this walkthrough's descriptor does plus `KeyReconcile`. This section keeps MySQL as its example
engine anyway, because `mysql.generic` is the one starter template `KnownDrivers.cs` ships — but what
it is actually demonstrating is the *descriptor mechanism itself*, for an engine that has no built-in or
descriptor coverage yet (an engine reachable through `System.Data.Odbc`, Firebird, or any other ADO.NET
provider DbDataSync has never seen). The mechanics below are unchanged and still real: this is the same
path `DescriptorDriverTests`/`DescriptorDriverApiFactory` prove end to end, and `mysql.generic` genuinely
is a driver no part of DbDataSync's own compiled code references — it just happens to duplicate, through
a slower path, an engine that also has a compiled driver now.

1. **Install the driver from the starter template.** This installs the `mysql-connector` library (if
   not already installed) and writes `<RepoRoot>/drivers/mysql.generic/driver.yaml`:

   ```sh
   dbdatasync driver install mysql.generic --version 2.4.0 --from mysql.generic
   ```

2. **Restart** the API (and any running worker) so the newly-written descriptor is picked up.

3. **Create the source connection**, the same shape a built-in driver's connection takes, just with
   `DriverType` set to the descriptor's own id:

   ```json
   PUT /api/connections/mysql-src
   {
     "name": "mysql-src", "driverType": "mysql.generic",
     "host": "localhost", "port": 3306, "database": "app",
     "authMode": "SqlAuth", "userId": "app_user", "password": "..."
   }
   ```

   (The same form in the web console: Connections → New Connection → pick `mysql.generic` from the
   driver dropdown, exactly as you would `MsSql` or `Postgres`.)

4. **Create the target connection** the ordinary way, `DriverType: "MsSql"`.

5. **Create the replication.** The reader Kind comes from the descriptor's own `capabilities.readers`
   list (a plain generic name like `Watermark`, not an `MsSql`-prefixed one) with whatever options that
   reader needs (a `watermarkColumn`, for `Watermark`); cache and writer stay target-side, ordinary
   `MsSql*` Kinds.

6. **Add a table mapping** and refresh its metadata, exactly as for any other driver — column discovery
   goes through the same `information_schema` path every descriptor driver uses.

7. **Confirm it's really a descriptor driver, not a built-in one**: `GET /api/drivers` lists
   `mysql.generic` with `"builtIn": false, "source": "descriptor"` alongside the five built-ins — added,
   not substituted.

From here, running, backfilling, and monitoring this replication works exactly like any other — nothing
about descriptor vs. built-in is visible anywhere past the driver dropdown.

## Next: State database

Drivers and libraries need somewhere to record what a replication has done and what it's about to do —
see [The state database](state-database.md), which resolves its own `MsSql`/`Postgres` engine choice
through this exact same library mechanism.
