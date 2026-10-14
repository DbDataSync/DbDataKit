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
./DbDataProxy.exe install           # or register the Windows service (elevated prompt), then: sc start DbDataProxy
./DbDataProxy.exe query --insecure --user me --password ... --datasource erp --sql "SELECT 1"
```

On first start the proxy writes `dbdataproxy-selfsigned.pfx` and `.cer` to its data directory
(`%ProgramData%\DbDataProxy` by default, `Proxy:DataDirectory` to change). Import the `.cer` into clients that
should trust it. The certificate covers localhost, the machine name/FQDN and `Proxy:Tls:Hostnames`, and is
regenerated automatically when those change or it nears expiry. Use `Tls:Mode = File` for your own PFX.

## Configuration

`appsettings.json` beside the executable, environment variables (`DBPROXY_Proxy__Listen__Port=9000`) or command
line (`--Proxy:Listen:Port=9000`). `appsettings.example.json` documents every option. Users, data sources,
compression and auth settings reload without a restart; the listener and certificate need one.

### Data sources

Clients never send connection strings. They name a configured data source with the `x-datasource` call header
(optional when the user has a default or exactly one permitted source).

| Provider | Notes |
|---|---|
| `sqlserver` | Microsoft.Data.SqlClient |
| `duckdb` | DuckDB.NET (handy for local testing) |
| `odbc` | System.Data.Odbc. `"Bitness": "x86"` runs the query in the 32-bit worker process |

Other ADO.NET providers: implement `IDbProvider` and register it. Statements are read-only unless the data
source sets `"AllowWrites": true` (this only gates statements sent as *updates*; the database account's own
permissions are the real boundary for what a query can do).

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
| `Grpc` | `gzip` | gRPC message | Any gRPC client that advertises gzip |
| `Ipc` | `zstd` / `lz4` | inside the Arrow stream | Best ratio on columnar data; client must read compressed Arrow IPC |

Clients can override per call with the `x-compression` header (`none`, `gzip`, `zstd`, `lz4`) unless
`AllowClientOverride` is false. Standard Arrow clients (C++/Python/Java/Go) read IPC-compressed streams natively.
The stock .NET Flight client does not, which is why this repo ships `DbDataProxy.Client.ProxyFlightClient`.

> IPC compression relies on setting writer/reader options that `Apache.Arrow.Flight` 23.0.0 only exposes
> through non-public members. The proxy verifies they exist at startup and fails fast rather than silently sending
> uncompressed data; tests guard it. If that ever breaks on a package upgrade, use `Grpc` mode until it is fixed.

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
   password fails at query time. Local users always win over pass-through for the same name.
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

## Clients

C# (`DbDataProxy.Client`, in `DbDataProxy.Core`):

```csharp
using var client = new ProxyFlightClient("https://proxy:8815");
await client.AuthenticateAsync("me", "password");
var result = await client.QueryAsync("SELECT * FROM sales",
    new QueryOptions { DataSource = "erp", Compression = "zstd" });
await foreach (var batch in result) { /* Apache.Arrow.RecordBatch */ }
```

Other languages use their normal Flight SQL client: authenticate with Basic credentials (the handshake), send the
returned bearer token, and add `x-datasource` / `x-compression` as call headers. For example with pyarrow:
`client.authenticate_basic_token(user, pw)` then `FlightCallOptions(headers=[token_pair, (b"x-datasource", b"erp")])`.
(Compatibility with non-.NET clients follows from using only standard Flight SQL mechanisms but has not been exercised
in this repository's tests.)

## Not supported yet

* Flight SQL metadata commands (`GetTables`, `GetCatalogs`, `GetSqlInfo`, ...) and prepared-statement parameters.
  Plain statements and parameterless prepared statements work (the .NET client prepares everything).
* Transactions, `DoPut` bulk ingestion.
* The Linux/Docker path (`Dockerfile`) has not been built or run in this repository's environment.
* Windows-account logon (`LogonUser`) is covered by tests through a fake; it has not been exercised against a real account.

## Development

```powershell
dotnet test            # unit + end-to-end tests (DuckDB; SQL Server tests skip when .\SQLEXPRESS is unreachable)
```

The tests publish the 32-bit worker on demand into `artifacts/worker-x86` and use the 32-bit-only Microsoft Text ODBC
driver to prove queries really run in a 32-bit process. Set `DBPROXY_TEST_SQLSERVER` to point the SQL Server tests at
another instance (Windows authentication).

Layout: `src/DbDataProxy.Core` (Arrow conversion, ADO.NET executor, client, worker protocol),
`src/DbDataProxy.Server` (Kestrel host, Flight SQL server, auth, TLS, service/CLI),
`src/DbDataProxy.Worker` (32-bit ODBC worker), `tests/DbDataProxy.Tests`.
