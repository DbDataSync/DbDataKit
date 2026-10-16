# DbDataProxy

An Apache Arrow Flight SQL proxy for relational databases. Clients speak standard Arrow Flight SQL to the proxy;
the proxy runs their queries against existing databases through ODBC or an ADO.NET provider and streams the results
back as Arrow record batches, **compressed**. Its main job is shrinking result sets on the wire, but it does not
dictate how you use it.

```
Flight SQL client ──gRPC/TLS──▶ DbDataProxy (x64) ──▶ SQL Server / DuckDB / 64-bit ODBC drivers
                                      │
                                      └─ loopback Flight ─▶ DbDataProxy.Worker (x86) ──▶ 32-bit ODBC drivers
```

* .NET 10, Kestrel, runs as a Windows service (also systemd/Docker). Windows Server 2016+ is supported.
  IIS is not used: gRPC over IIS needs HTTP/2 response trailers, which Windows Server 2016 IIS lacks.
* Standalone: a self-contained single-file `DbDataProxy.exe` that generates its own self-signed TLS certificate.

## Quick start

```powershell
./build/publish.ps1                 # -> artifacts/publish/win-x64 (DbDataProxy.exe + worker\)
cd artifacts/publish/win-x64
./DbDataProxy.exe hash-password     # prompts; paste the hash into appsettings.json
# edit appsettings.json (see appsettings.example.json), then:
./DbDataProxy.exe                   # run in a console
./DbDataProxy.exe service install   # or register the Windows service (elevated prompt), then: DbDataProxy service start
./DbDataProxy.exe query --insecure --user me --password ... --datasource erp --sql "SELECT 1"
```

On first start the proxy writes `dbdataproxy-selfsigned.pfx` and `.cer` to its data directory
(`%ProgramData%\DbDataProxy` by default, `Proxy:DataDirectory` to change). Import the `.cer` into clients that
should trust it. The certificate covers localhost, the machine name/FQDN and `Proxy:Tls:Hostnames`, and is
regenerated automatically when those change or it nears expiry (a running proxy rotates it in the background, 30 days
before it ends, and serves the new one without a restart; `/health` reports its state). Use `Tls:Mode = File` for your own PFX.

The generated PFX is encrypted with a random password. The password is kept in `dbdataproxy-selfsigned.pfx.pwd`, protected
with DPAPI (machine scope) on Windows and readable only by the owner (mode 0600) elsewhere; the PFX, that file and a
data directory the proxy creates are restricted to the service identity, Administrators and SYSTEM. A PFX written by an
earlier version (empty password) is re-encrypted in place on the first start, keeping the same certificate. A
`Tls:CertificatePassword` set in the configuration is still honoured for the self-signed file; remove it to use the
protected password. Manage the certificate with `DbDataProxy cert show|ensure|renew|check`.

## Service, containers and health

```
DbDataProxy service install [--name N] [--account A] [--password-secret env:NAME|file:PATH] [--data-dir PATH] [--dry-run]
DbDataProxy service uninstall|start|stop|restart|status|doctor [--name N]
DbDataProxy service unit [--name N] [--exec /opt/dbdataproxy/DbDataProxy]     # hardened systemd unit (also: service --systemd)
DbDataProxy install|uninstall                                                  # same as service install|uninstall
```

The service is registered through the Service Control Manager API (no `sc.exe`). By default it runs as the virtual account
`NT SERVICE\DbDataProxy`, which has no password and no rights beyond what it is granted: read/execute on the install
directory and modify on the data directory (the only places it needs). Use `--account DOMAIN\name$` for a group managed
service account, or `--account DOMAIN\user` with a password. `--account LocalSystem` needs `--allow-localsystem`.
A password is never put on the service command line; give it with `--password-secret env:NAME` or `file:PATH`, or type it at the
prompt. `--password` is still accepted but prints a warning, because command lines are visible to other users.
`service doctor` checks the service settings and directory permissions. The service restarts after a crash (5 s, 5 s, 60 s;
the counter resets after a day) and starts at boot.

The proxy answers `GET /health`, `/health/live`, `/health/ready` and `/version` on its HTTP/2 listener, and on stop waits for
running queries (`Proxy:Hosting:DrainSeconds`, default 30) before shutting down. `Proxy:Hosting:MaxConcurrentRequests`
(default 1000) caps in-flight requests; further ones get 503. `DbDataProxy --healthcheck` probes the local proxy (HTTP/2, TLS
certificate accepted for loopback only) and exits 0 or 1, for a container HEALTHCHECK.

## Server info (GetSqlInfo)

`GetSqlInfo` is answered per data source, chosen with the `x-datasource` header (or the user's default). With no
data source selectable it describes the proxy alone.

| Flight SQL id | Value |
| --- | --- |
| `FLIGHT_SQL_SERVER_NAME` | The backend product: `Microsoft SQL Server`, `DuckDB`, or for ODBC what the driver reports (`DBMS_NAME`); `DbDataProxy` with no data source |
| `FLIGHT_SQL_SERVER_VERSION` | The backend version when the database can be reached (what the shared catalog reader reports: `SELECT version()` without the leading `v` for DuckDB, the connection's server version for SQL Server, `DBMS_VER` for ODBC); otherwise the proxy version |
| `FLIGHT_SQL_SERVER_ARROW_VERSION` | The Apache Arrow library version |
| `FLIGHT_SQL_SERVER_READ_ONLY` | `true` when the data source has no `AllowWrites` (see the caveat under Limitations) |
| `FLIGHT_SQL_SERVER_SQL` / `_SUBSTRAIT` / `_CANCEL` / `_BULK_INGESTION` | `true` / `false` / `false` / `false` |
| `SQL_IDENTIFIER_QUOTE_CHAR` | `"` for SQL Server and DuckDB; the driver's own character for ODBC (the 32-bit worker's too) |
| `SQL_IDENTIFIER_CASE`, `SQL_QUOTED_IDENTIFIER_CASE`, `SQL_NULL_ORDERING` | SQL Server and DuckDB only: case-insensitive; nulls sort low (SQL Server) or at the end (DuckDB) |

## Parameters

Prepared statements accept parameters the standard Flight SQL way: prepare, bind a record batch with `DoPut`, execute.
ADBC (`cursor.execute(sql, params)` / `executemany`) and the proxy's own `ProxyFlightClient.QueryAsync(sql, [..])` do
this for you.

* Mark parameters with `?`, bound in column order. SQL Server sources also accept `@name` markers; the Arrow column
  names are used as the parameter names. Markers inside string literals, quoted identifiers and comments are ignored.
* `CreatePreparedStatement` reports one parameter-schema field per `?` marker (`parameter_0`, `parameter_1`, ...). The
  proxy cannot know the types the database expects without running the statement, so the fields are Arrow `Null`
  ("any"); bind whatever types you have. `@name` markers are matched by the names you send and are not listed.
* A query takes one row of parameters. An update runs once per row (a batch insert) and returns the summed count.
* Supported Arrow types: booleans, integers, floats, decimals, strings, dates, timestamps, times, binary and nulls.
* Values are bound through the provider's parameter API, never spliced into the SQL. ODBC drivers that reject 64-bit
  parameters get values that fit in 32 bits as 32-bit integers.

## Releases and installation

CI (`.github/workflows/ci.yml`) builds, tests and smoke-packages every push and pull request on a Windows runner.
Pushing a tag such as `v1.2.3` runs `.github/workflows/release.yml`, which tests, runs `build/package.ps1` and creates a
GitHub release containing `DbDataProxy-<version>-win-x64.zip`, `SHA256SUMS` and `install.ps1` (tags containing `-`,
such as `v1.2.3-rc1`, are marked as pre-releases).

To install or upgrade from a release, run from an elevated PowerShell prompt:

```powershell
& ([scriptblock]::Create((irm https://github.com/DbDataSync/DbDataProxy/releases/latest/download/install.ps1))) -InstallService
```

The script verifies the download against `SHA256SUMS`, installs to `%ProgramFiles%\DbDataProxy` (`-InstallDir`),
keeps an existing `appsettings.json`, stops and restarts a running service around an upgrade, and registers the
service only with `-InstallService`. `-Version 1.2.3` selects a specific release.

## Configuration

`appsettings.json` beside the executable, environment variables (`DBPROXY_Proxy__Listen__Port=9000`) or command
line (`--Proxy:Listen:Port=9000`). `appsettings.example.json` documents every option. Users, data sources,
compression and auth settings reload without a restart; the listener and certificate need one.

### Validation, schema version and the config commands

The configuration stays JSON (`appsettings.json`, section `Proxy`, PascalCase keys, the same environment and command
line overrides). It is read through the shared `DbDataKit.Config` strict binder, so a typo is no longer ignored:

* **Strict at startup.** An unknown key (`Proxy:Listen:Prot`), a value of the wrong type, a number out of range, a
  user that names an undefined data source, `Tls.Mode` `File` without `CertificatePath`, `Bitness` `x86` with a
  provider other than `odbc`, `PassThrough` on DuckDB, an unknown provider, a duplicate or empty user name, a codec the
  compression mode does not support, JWT without `Audience`, ... all stop the service before it listens. Every problem is
  listed once with its key path in environment-variable form, where it came from (file with line and column, `env:NAME`,
  `cli:args`) and, for unknown keys, a "did you mean" suggestion. Secret values are never printed.
  The process exits with **code 2** (the same code as a command line usage error); scripts and service managers can rely on it.
* **Case-insensitive, unchanged keys.** Keys are matched ignoring case, exactly as before, so every existing file and
  override (`DBPROXY_Proxy__Listen__Port`, `Proxy__Tls__Mode`, `--Proxy:Listen:Port=9000`) keeps working, booleans may
  still be written `True`. `appsettings.json` and `appsettings.example.json` are checked by the test suite.
* **Reloads keep the last good configuration.** An edit of `appsettings.json` that is invalid (syntax error, unknown key,
  failed validation) is rejected with an error in the log naming the problems, and the running service keeps using the last
  good configuration; it never crashes on a bad edit. A later valid edit is picked up.
* **Forward compatibility, explicitly.** `"AllowUnknownKeys": true` (or `--allow-unknown-keys` for `config check`) turns
  unknown keys into warnings, for example while rolling a file back to an older release. The default is strict.
* **`SchemaVersion`** (optional, inside `Proxy`; absent means 1). Version 2 changes defaults only: a data source without
  its own `Classifier` is `strict` instead of `warn`. Files without `SchemaVersion` keep every old default, so upgrading
  changes nothing; a startup warning lists the read-only sources that still rely on the old default. The shipped
  `appsettings.json` and the example use `"SchemaVersion": 2` (new installs get the strict default). Moving v1 to v2 is a
  documented no-op (`ProxyConfigMigrations`: no key changes) other than that default: set `Classifier` on each source, then
  add `"SchemaVersion": 2`. A file with a newer version than the release understands is refused with an upgrade message.
  Future renames ship as additional migrations in the shared migration framework (see `MigrationTests`); `config migrate`
  is not provided because the shared migration engine edits YAML documents and cannot rewrite `appsettings.json` in place yet.

```
DbDataProxy config check [--file path]        validate (exit 0 valid, 2 invalid); warnings are printed to stderr
DbDataProxy config show [--origin] [--file p] effective values; --origin adds file:line, env:NAME, cli:args or "default"
```

Without `--file` both commands read the same sources as the service (appsettings.json beside the executable,
`DBPROXY_` environment variables, command line options); with `--file` only that file (plus `--Proxy:...` options) is read.
`config show` masks secrets: a secret reference (`env:`, `file:`, `secret:`, `dpapi:`) is shown as the reference,
a literal secret (`SigningKey`, `PasswordHash`, a literal `Password=` in a connection string, a literal certificate password)
as `********` or `***`.

### Data sources

Clients never send connection strings. They name a configured data source with the `x-datasource` call header
(optional when the user has a default or exactly one permitted source).

| Provider | Notes |
|---|---|
| `sqlserver` | Microsoft.Data.SqlClient |
| `duckdb` | DuckDB.NET (handy for local testing) |
| `odbc` | System.Data.Odbc. `"Bitness": "x86"` runs the query in the 32-bit worker process |

Connections are made by the shared `DbDataKit.Connections` providers (SQL Server, DuckDB, ODBC): the data source's
settings become a shared `ConnectionDefinition` (the Proxy-only ones, such as bitness, pass-through, metadata mode and
batch size, travel in its `extensions.proxy`), the composer builds the connection string, and failures are classified by
the shared failure model. The configuration keys are unchanged. Statements are read-only unless the data
source sets `"AllowWrites": true` (this only gates statements sent as *updates*; the database account's own
permissions are the real boundary for what a query can do).

What the shared composer does to a configured connection string (behaviour changes with the Connections adoption):

* **TLS for SQL Server** (the `sqlserver` provider and ODBC "ODBC Driver N for SQL Server" / Native Client drivers): a string
  with no `Encrypt` gets `Encrypt=Mandatory` and no `TrustServerCertificate` means certificate validation is on. An explicit
  setting in the string is kept as written (`TrustServerCertificate=True`, `Encrypt=False`, `Encrypt=Strict`). The legacy
  Windows ODBC driver named just `SQL Server` knows none of these keys and is left alone. ODBC PostgreSQL drivers get
  `sslmode=verify-full` unless the string sets `sslmode`.
* **Timeouts**: a connect timeout in the string is kept (default 15 s); every command now runs with the data source's
  `CommandTimeoutSeconds`, else a `Command Timeout` in the string, else 300 s (before: the driver's default, 30 s for
  SqlClient and ODBC, none for DuckDB).
* **Read-only sources** (no `AllowWrites`) add `ApplicationIntent=ReadOnly` for SQL Server (a routing hint toward a readable secondary,
  not enforcement: use a read-only login) and, for known ODBC drivers, their read-only keyword.
* **ODBC strings** are parsed strictly and checked by the shared policy: `FILEDSN` and `SAVEFILE` are refused, malformed strings fail
  with a configuration error instead of reaching the driver.
* **Pass-through credentials** go through the composer, which removes every integrated-security style key, writes the caller's login
  with the engine's own quoting and refuses control characters and values over 512 characters (answered as `Unauthenticated`).

#### Read-only statement check (`Classifier`)

On a source without `AllowWrites`, the text of every query and every prepared statement is checked with the shared
`DbDataKit.Dialect` classifier (dialect chosen from the provider: SQL Server, DuckDB, or generic ODBC): it must be
exactly one `SELECT` (or `WITH ... SELECT`; DuckDB also `FROM`-first, `DESCRIBE`, `SUMMARIZE`, `SHOW`) and must not use
a deny-listed word outside strings, comments and quoted identifiers (`OPENROWSET`, `xp_*`, `sp_*`, `BULK`, `COPY`,
`ATTACH`, `INSTALL`, `LOAD`, `PRAGMA`, `read_csv` and the other `read_*` functions, ...). Set it per data source:

| `Classifier` | Effect |
|---|---|
| `warn` (default without `SchemaVersion`, so existing configuration behaves as before) | A statement that would be refused is logged as a warning and still runs |
| `strict` (default from `"SchemaVersion": 2`, i.e. new installs) | The statement is refused with gRPC `PermissionDenied` |
| `off` | No check |

The check is lexical and refuses some harmless SQL (an unquoted column named like a deny-listed word: quote it). It is
defence in depth, not a security boundary; the database login is. Sources with `AllowWrites` are not checked on the
query path. Upgrade path: stay on `warn` (no `SchemaVersion`) for a release, check the log for would-be refusals, then set `"SchemaVersion": 2` (or `"Classifier": "strict"` per source).

Types map to Arrow as: integers/floats/bool directly (`tinyint` → UInt8), `decimal` → Decimal128 (precision/scale
from the provider, default 38/9 when unknown), `date` → Date32, `datetime/datetime2` → Timestamp(µs),
`datetimeoffset` → Timestamp(µs, UTC), `time` → Time64(µs), strings/GUIDs/anything else → Utf8, `varbinary` → Binary.
ODBC columns of types `System.Data.Odbc` cannot read (e.g. SQL Server `datetimeoffset`/`time` through ODBC) fail
with a message telling you to `CAST` them in the query.

### Compression

`Proxy:Compression` (global) and `DataSources:<name>:Compression` (override):

| Mode | Codec | Where | Notes |
|---|---|---|---|
| `None` | | | |
| `Grpc` | `gzip` | **gRPC compression**: the transport compresses each whole message | Any gRPC client that advertises gzip; weaker ratio |
| `Arrow` | `zstd` / `lz4` | **Arrow data compression**: each column buffer inside the Arrow stream is compressed | Best ratio on columnar data; the client's Arrow reader must support the codec |

Clients can override per call with the `x-compression` header (`none`, `gzip`, `zstd`, `lz4`) unless
`AllowClientOverride` is false. Standard Arrow clients (C++/Python/Java/Go) read Arrow data compression natively (it is part of the Arrow format, so no gRPC negotiation is involved).
The stock .NET Flight client does not, which is why this repo ships `DbDataProxy.Client.ProxyFlightClient`.

> Arrow data compression relies on setting writer/reader options that `Apache.Arrow.Flight` 23.0.0 only exposes
> through non-public members. The proxy verifies they exist at startup and fails fast rather than silently sending
> uncompressed data; tests guard it. If that ever breaks on a package upgrade, use `Grpc` mode until it is fixed.

### Secrets

Configuration values that are secrets can be references instead of literals (shared `DbDataKit.Secrets` library):

| Reference | Resolves to |
|---|---|
| `env:NAME` | the environment variable `NAME` |
| `file:C:\secrets\erp.txt` | the file's contents, trimmed (Docker/Kubernetes secrets, systemd `LoadCredential`) |
| `secret:dbdata:proxy:datasource:erp` | environment variable `DBDATA_SECRET_DBDATA_PROXY_DATASOURCE_ERP`; or, when `$CREDENTIALS_DIRECTORY` is set, the file `dbdata_proxy_datasource_erp` in it; or, on Windows, `<DataDirectory>\secrets\dbdata~proxy~datasource~erp.secret` (DPAPI) |
| `dpapi:<base64>` | an inline DPAPI blob (Windows, machine scope). Create one with `DbDataProxy protect-secret` |

Where they work: a data source `ConnectionString` (the whole string, or just the value of a password key:
`Server=SQL01;User Id=proxy_reader;Password=env:ERP_DB_PASSWORD`), `Proxy:Tls:CertificatePassword` and
`Proxy:Auth:Jwt:SigningKey`. References are resolved each time the setting is used, from the current settings, so a rotated
secret (and a reloaded `appsettings.json`) takes effect without keeping old values around. A reference that cannot be
resolved fails that data source with a message naming the reference (never a value) and is logged.

Literals still work, but the proxy logs a one-time warning naming the config key (never the value), e.g.
`Config key Proxy:DataSources:erp:ConnectionString (Password) holds a literal secret`. Set
`"RequireReferencedSecrets": true` to turn the warning into a startup failure (and to refuse a literal that appears after a reload).

Every resolved secret is registered with a redactor that scrubs all log output (messages and exception text) and the
text of errors returned to clients; `Password=...`-style fragments are scrubbed even when the value is unknown.

Password hashes (`Proxy:Users:*:PasswordHash`) use the shared PBKDF2 hasher in the format `PBKDF2-SHA256$iterations$salt$hash`,
the same format `hash-password` has always written, so existing hashes verify unchanged.

### Authentication

Everything arrives as a Flight call; the proxy denies any call without valid credentials (except the handshake).

1. **Local users with hashed passwords** (`Proxy:Users`, PBKDF2-HMAC-SHA256, 210k iterations). Standard Flight
   handshake: Basic credentials in, opaque bearer token out (`authorization: Bearer ...` response header). Each user
   lists the data sources they may use; the data source's connection string carries the database credentials
   ("mapped" accounts). Failed logins are throttled per user and client address.
2. **Windows accounts** (`"AuthType": "Windows"`, Windows only): same handshake, but the password is checked
   against the local/domain account (`DOMAIN\user`, `user@domain`, or a bare local name) and the user maps to
   data sources like any other.
3. **Pass-through database credentials** (`"PassThrough": true` on a data source): the username/password the
   client authenticates with are injected into that connection and the database is the authority, so a wrong
   password fails at query time. Local users always win over pass-through for the same name. A rejected database
   login (failure kind `Login`) is answered `Unauthenticated` and counts in the login throttle for that user and client
   address like a wrong password at the handshake does; once locked out (`Auth:MaxFailedLogins` within `Auth:LockoutMinutes`)
   the user is refused before any database connection is made, and a pass-through handshake does not reset the count.
4. **JWT bearer / OIDC** (`Proxy:Auth:Jwt`, e.g. Microsoft Entra ID): clients send `authorization: Bearer <jwt>` on
   every call. Validated against the authority's keys (or a shared HMAC key); `Mappings` turn claims (`groups`,
   `roles`, `scp`, ...) into data-source access. Tokens matching no mapping are refused.

Always run with TLS (`SelfSigned` or `File`) outside local testing: credentials are sent as Basic auth.

### 32-bit ODBC

A 64-bit process cannot load 32-bit drivers, so `Bitness: "x86"` ODBC sources are executed by
`DbDataProxy.Worker.exe` (published `win-x86`, in `worker\`). The proxy starts it on first use, talks to it over a
loopback Flight connection guarded by a random per-launch secret, restarts it if it dies and stops it with the
service; the worker also exits if the proxy disappears. If you publish the whole proxy as `win-x86`
(`./build/publish.ps1 -Runtime win-x86`) the sources run in-process and no worker is needed.

## Security

What the proxy enforces and where the responsibility stays with the database (details per feature are in the sections above).

* **Read-only is enforced at the connection where the engine can.** A data source without `AllowWrites` is opened read-only:
  * *DuckDB*: the file is opened `access_mode=READ_ONLY` and the session is sandboxed before the first statement runs:
    `enable_external_access=false` (no `read_csv`/`read_parquet`/`read_text` of any file, no `COPY ... TO`, no `ATTACH`),
    extension install and autoload off (`INSTALL`/`LOAD` fail), then `lock_configuration=true` so no `SET` can undo it.
    This holds with `Classifier: off`; the lexical classifier is a second line, not the control. A source that deliberately serves a folder of
    files lists it in `"AllowedDirectories": ["C:/data/parquet"]` (absolute paths): reads below those directories work, everything else stays blocked.
    An in-memory database (`:memory:`) cannot be opened read-only, so it only gets the session settings. A source with `AllowWrites` is **not** sandboxed
    (it is trusted with the host's files, as before).
  * *SQL Server*: `ApplicationIntent=ReadOnly` is added. It only routes to a readable secondary of an availability group and does nothing on a plain
    instance: **it does not stop writes**. Use a dedicated read login with only `db_datareader` for real enforcement.
  * *ODBC*: known drivers get their read-only keyword and `SQL_ATTR_ACCESS_MODE=SQL_MODE_READ_ONLY` is set after connecting (best effort; many drivers ignore
    it). The database login is the control.
* **TLS to SQL Server**: encrypt and verify the certificate unless the connection string says otherwise explicitly (see Data sources). Each source that
  opts out (`TrustServerCertificate=True`, `Encrypt=False`) is named in one startup warning; the setting keeps working.
* **Pass-through credentials** go through the shared composer: every integrated-security style key of the administrator's string (`Trusted_Connection`,
  `Integrated Security`, `Authentication=...`, Kerberos and GSS keywords, client-certificate keys) is removed before the caller's user and password are written with the
  engine's own quoting, control characters and over-long values (512) are refused, and the result is asserted free of integrated keys.
  `x};Driver={evil};UID={` stays one value (tests cover hostile logins for SQL Server and ODBC).
* **Clients never see database error text.** A failed connection or statement is answered with its kind, the engine's error number or SQLSTATE and a fixed
  sentence, for example `The login was rejected. [sqlserver 18456]` or `The statement was not accepted by the server. [duckdb Parser-Error]`:

  | Failure kind | gRPC status |
  |---|---|
  | Login (also counted in the throttle) | `Unauthenticated` |
  | Network, Tls | `Unavailable` |
  | Timeout | `DeadlineExceeded` |
  | Permission | `PermissionDenied` |
  | Syntax, Unsupported | `InvalidArgument` |
  | anything else | `Internal` (with the exception type name) |

  Messages the proxy writes itself (a `?`-marker count mismatch, an ODBC column type to `CAST`, the classifier's refusal) are passed on. The driver's own text
  (scrubbed by the redactor) goes to the server log; `Proxy:LogDriverErrors: false` logs only kind and code. 32-bit worker failures travel the same way.
* **Limits (off by default)**: `MaxConcurrentQueries` per data source and `MaxConcurrentQueriesPerUser` cap the queries running at once (a streaming result counts until it
  is read to the end or discarded); a request waits up to `AcquireTimeoutSeconds` (30) and is then refused with `ResourceExhausted`.
  Every command now has a timeout (`CommandTimeoutSeconds`, else 300 s).
* Not covered: row and byte limits, an audit log of failures per client beyond the log lines, and anything the database login allows.

## Clients

C# (`DbDataProxy.Client`, in `DbDataProxy.Core`):

```csharp
using var client = new ProxyFlightClient("https://proxy:8815");
await client.AuthenticateAsync("me", "password");
var result = await client.QueryAsync("SELECT * FROM sales",
    new QueryOptions { DataSource = "erp", Compression = "zstd" });
await foreach (var batch in result) { /* Apache.Arrow.RecordBatch */ }
```

Other languages use their normal Flight SQL client. With ADBC (Python shown; the same options exist for the other
languages):

```python
import adbc_driver_flightsql.dbapi as fsql
from adbc_driver_flightsql import DatabaseOptions

conn = fsql.connect("grpc+tls://proxy:8815", db_kwargs={
    "username": "me", "password": "...",
    DatabaseOptions.RPC_CALL_HEADER_PREFIX.value + "x-datasource": "erp",
    DatabaseOptions.RPC_CALL_HEADER_PREFIX.value + "x-compression": "zstd",   # optional
})
```

With plain pyarrow Flight: `token = client.authenticate_basic_token(user, pw)` then pass
`FlightCallOptions(headers=[token, (b"x-datasource", b"erp")])` to `get_flight_info` / `do_get`.

ADBC (Python, Go driver) and pyarrow Flight are exercised by `tests/interop/run-interop.ps1` against the published
exe: queries with every compression setting, updates, metadata and wrong-password rejection all pass.

## Not supported yet

* `GetExportedKeys`/`GetImportedKeys`/`GetCrossReference` and `GetXdbcTypeInfo`.
* Catalog metadata comes from the shared `DbDataKit.Metadata` readers, one implementation for every Flight SQL metadata call (catalogs, schemas,
  tables, primary keys, per-table schemas and `GetSqlInfo`). ODBC sources use the driver's catalog functions (`SQLTables`, `SQLColumns`,
  `SQLPrimaryKeys`, `SQLGetInfo`), so text/Excel/Access and other drivers without `INFORMATION_SCHEMA` work, including through the 32-bit
  worker; SQL Server sources use the `sys` catalog views and DuckDB its `duckdb_*()` functions, both of which answer exactly what `INFORMATION_SCHEMA`
  did (tests compare recorded responses of a DuckDB file and the text driver byte for byte, and SQL Server against `INFORMATION_SCHEMA` live). Set a
  data source's `"Metadata"` to `"odbc"` (ODBC sources only) or `"informationschema"` (generic `INFORMATION_SCHEMA` reader) to override the default
  (`"auto"`). Per-table schemas (`include_schema`) take decimal precision/scale and nullability (key columns are NOT NULL) from the catalog, because a zero-row
  query cannot always report them (one catalog read per table). Names are quoted with the driver's own identifier character and fall back to shorter names when
  the driver's catalog is not valid SQL. `GetPrimaryKeys` on an ODBC source asks the driver (`SQLPrimaryKeys`); drivers without keys (text files)
  or without the function report none. Table-name filter patterns are SQL `LIKE` patterns matched by a linear matcher (no regular expressions, so a hostile
  pattern cannot make the server backtrack) with `\` as the escape character for `%` and `_`, as in the ODBC and JDBC catalog functions. Differences from
  the previous implementation: `\` in a pattern is now an escape (it was a literal character); table types are normalised to `TABLE`, `VIEW`, `MATERIALIZED VIEW`,
  `SYSTEM TABLE`, `LOCAL TEMPORARY` and `OTHER` (an ODBC driver's `ALIAS`/`SYNONYM`/`GLOBAL TEMPORARY` used to pass through as written); the DuckDB server version is reported without the leading `v`.
* `AllowWrites` gates statements a client sends as *updates*. A client that runs `INSERT`/`DDL` through the query
  path (ADBC's `cursor.execute` does) is refused by the engine on DuckDB sources (read-only file, see Security); on SQL Server and ODBC
  sources only when the source's `Classifier` is `strict` (and then by a lexical check, not a parser) or the database account cannot write,
  so use read-only database accounts for read-only sources.
* Transactions, `DoPut` bulk ingestion.
* Pass-through and Windows-account logon have not been exercised against real accounts.
* The Linux/Docker path (`Dockerfile`) has not been built or run in this repository's environment.

## Development

```powershell
dotnet test            # unit + end-to-end tests (DuckDB; SQL Server tests skip when .\SQLEXPRESS is unreachable)
```

The tests publish the 32-bit worker on demand into `artifacts/worker-x86` and use the 32-bit-only Microsoft Text ODBC
driver to prove queries really run in a 32-bit process. Set `DBPROXY_TEST_SQLSERVER` to point the SQL Server tests at
another instance (Windows authentication).

Layout: the shared `DbDataKit.*` packages (ODBC catalog, Connections providers, ...; project references into the hub while the adoption pass is offline), `src/DbDataProxy.Core` (Arrow conversion, ADO.NET executor, client, worker protocol),
`src/DbDataProxy.Server` (Kestrel host, Flight SQL server, auth, TLS, service/CLI),
`src/DbDataProxy.Worker` (32-bit ODBC worker), `tests/DbDataProxy.Tests`.

## License

MIT; see [LICENSE](LICENSE).
