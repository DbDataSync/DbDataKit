# DbDataGateway

An Apache Arrow Flight SQL gateway for relational databases. Clients speak standard Arrow Flight SQL to the gateway;
the gateway runs their queries against existing databases through ODBC or an ADO.NET provider and streams the results
back as Arrow record batches, **compressed**. Its main job is shrinking result sets on the wire, but it does not
dictate how you use it.

```
Flight SQL client ──gRPC/TLS──▶ DbDataGateway (x64) ──▶ SQL Server / DuckDB / 64-bit ODBC drivers
                                      │
                                      └─ loopback Flight ─▶ DbDataGateway.Worker (x86) ──▶ 32-bit ODBC drivers
```

* .NET 10, Kestrel, runs as a Windows service (also systemd/Docker). Windows Server 2016+ is supported.
  IIS is not used: gRPC over IIS needs HTTP/2 response trailers, which Windows Server 2016 IIS lacks.
* Standalone: a self-contained single-file `dbdatagateway.exe` that generates its own self-signed TLS certificate.

## Quick start

```powershell
./build/publish.ps1                 # -> artifacts/publish/win-x64 (dbdatagateway.exe + worker\)
cd artifacts/publish/win-x64
./dbdatagateway.exe identity service-account-add --name me && ./dbdatagateway.exe identity token-create --account me --name laptop   # a home user and its personal access token (shown once)
# edit %ProgramData%\DbDataKit\gateway\config\server.yaml (see config.example\), then:
./dbdatagateway.exe server run         # run in a console (there is no default command)
./dbdatagateway.exe service install   # or register the Windows service (elevated prompt), then: dbdatagateway service start
./dbdatagateway.exe query run --insecure --user me --password ddk_... --datasource erp --sql "SELECT 1"
```

On first start the gateway writes `dbdatagateway-selfsigned.pfx` and `.cer` to its data directory
(`%ProgramData%\DbDataKit\gateway` by default, `/var/lib/dbdatakit/gateway` on Linux; `DBDATAKIT_DATA_GATEWAY` or the folder `gateway` under `DBDATAKIT_DATA` moves it, and `Gateway:DataDirectory` still wins). Import the `.cer` into clients that
should trust it. The certificate covers localhost, the machine name/FQDN and `Gateway:Tls:Hostnames`, and is
regenerated automatically when those change or it nears expiry (a running gateway rotates it in the background, 30 days
before it ends, and serves the new one without a restart; `/health` reports its state). Use `Tls:Mode = File` for your own PFX, or `Tls:Mode = Store` to take a certificate from the certificate store (`Tls:CertificateThumbprint`,
or `Tls:CertificateSubject` and the gateway uses the one that is valid for longest; `Tls:StoreLocation` is `LocalMachine` by default). The service account
needs read access to the private key. A running gateway picks up a renewed store certificate within hours; `DbDataGateway cert show|check` describes it.

The generated PFX is encrypted with a random password. The password is kept in `dbdatagateway-selfsigned.pfx.pwd`, protected
with DPAPI (machine scope) on Windows and readable only by the owner (mode 0600) elsewhere; the PFX, that file and a
data directory the gateway creates are restricted to the service identity, Administrators and SYSTEM. A PFX written by an
earlier version (empty password) is re-encrypted in place on the first start, keeping the same certificate. A
`Tls:CertificatePassword` set in the configuration is still honoured for the self-signed file; remove it to use the
protected password. Manage the certificate with `DbDataGateway cert show|ensure|renew|check`.

## JSON output

Every command takes `--format json` and writes one document instead of text: `{ schema, command, tool_version, exit_code, ok, data, messages, errors }`. `data` is what the command found or did
(its shape is the command's own, listed in `schemas/output.schema.json`), `messages` and `errors` are the lines the text form writes to standard output and standard error, and `exit_code` is the code text mode
exits with (0 done, 1 the answer is no or it failed, 2 used wrongly). The schema is generated from the commands and a test fails when it is out of date (`UPDATE_GOLDEN=1` rewrites it).

## Service, containers and health

```
dbdatagateway service install [--name N] [--account A] [--password-secret env:NAME|file:PATH] [--data-dir PATH] [--dry-run]
dbdatagateway service uninstall|start|stop|restart|status|doctor [--name N]
dbdatagateway service unit [--name N] [--exec /opt/dbdatagateway/dbdatagateway]     # hardened systemd unit that runs "server run"
```

The service is registered through the Service Control Manager API (no `sc.exe`). By default it runs as the virtual account
`NT SERVICE\DbDataGateway`, which has no password and no rights beyond what it is granted: read/execute on the install
directory and modify on the data directory (the only places it needs). Use `--account DOMAIN\name$` for a group managed
service account, or `--account DOMAIN\user` with a password. `--account LocalSystem` needs `--allow-localsystem`.
A password is never put on the service command line; give it with `--password-secret env:NAME` or `file:PATH`, or type it at the
prompt. `--password` is still accepted but prints a warning, because command lines are visible to other users.
`service doctor` checks the service settings and directory permissions. The service restarts after a crash (5 s, 5 s, 60 s;
the counter resets after a day) and starts at boot.

The gateway answers `GET /health`, `/health/live`, `/health/ready` and `/version` on its HTTP/2 listener, and on stop waits for
running queries (`Gateway:Hosting:DrainSeconds`, default 30) before shutting down. `Gateway:Hosting:MaxConcurrentRequests`
(default 1000) caps in-flight requests; further ones get 503. `dbdatagateway health check` probes the local gateway (HTTP/2, TLS
certificate accepted for loopback only) and exits 0 or 1, for a container HEALTHCHECK.

## Server info (GetSqlInfo)

`GetSqlInfo` is answered per data source, chosen with the `x-datasource` header (or the user's default). With no
data source selectable it describes the gateway alone.

| Flight SQL id | Value |
| --- | --- |
| `FLIGHT_SQL_SERVER_NAME` | The backend product: `Microsoft SQL Server`, `DuckDB`, or for ODBC what the driver reports (`DBMS_NAME`); `DbDataGateway` with no data source |
| `FLIGHT_SQL_SERVER_VERSION` | The backend version when the database can be reached (what the shared catalog reader reports: `SELECT version()` without the leading `v` for DuckDB, the connection's server version for SQL Server, `DBMS_VER` for ODBC); otherwise the gateway version |
| `FLIGHT_SQL_SERVER_ARROW_VERSION` | The Apache Arrow library version |
| `FLIGHT_SQL_SERVER_READ_ONLY` | `true` when the data source has no `AllowWrites` (see the caveat under Limitations) |
| `FLIGHT_SQL_SERVER_SQL` / `_SUBSTRAIT` / `_CANCEL` / `_BULK_INGESTION` | `true` / `false` / `false` / `false` |
| `SQL_IDENTIFIER_QUOTE_CHAR` | `"` for SQL Server and DuckDB; the driver's own character for ODBC (the 32-bit worker's too) |
| `SQL_IDENTIFIER_CASE`, `SQL_QUOTED_IDENTIFIER_CASE`, `SQL_NULL_ORDERING` | SQL Server and DuckDB only: case-insensitive; nulls sort low (SQL Server) or at the end (DuckDB) |

## Parameters

Prepared statements accept parameters the standard Flight SQL way: prepare, bind a record batch with `DoPut`, execute.
ADBC (`cursor.execute(sql, params)` / `executemany`) and the gateway's own `GatewayFlightClient.QueryAsync(sql, [..])` do
this for you.

* Mark parameters with `?`, bound in column order. SQL Server sources also accept `@name` markers; the Arrow column
  names are used as the parameter names. Markers inside string literals, quoted identifiers and comments are ignored.
* `CreatePreparedStatement` reports one parameter-schema field per `?` marker (`parameter_0`, `parameter_1`, ...). The
  gateway cannot know the types the database expects without running the statement, so the fields are Arrow `Null`
  ("any"); bind whatever types you have. `@name` markers are matched by the names you send and are not listed.
* A query takes one row of parameters. An update runs once per row (a batch insert) and returns the summed count.
* Supported Arrow types: booleans, integers, floats, decimals, strings, dates, timestamps, times, binary and nulls.
* Values are bound through the provider's parameter API, never spliced into the SQL. ODBC drivers that reject 64-bit
  parameters get values that fit in 32 bits as 32-bit integers.

## Releases and installation

DbDataGateway lives in the DbDataKit repository. Work happens on short feature branches and reaches `main` by pull request (rebase
merge); `.github/workflows/dbdatagateway-ci.yml` runs on the pull request and again on `main`: the Windows and Linux suites, plus the package
smoke test. A release is cut by dispatching `.github/workflows/dbdatagateway-release.yml` against `main` (or `-f sha=<commit>` for an older
commit on it), which needs a green CI run for that exact commit. It builds with `build/package.ps1` and publishes a GitHub release in this
repository containing `dbdatagateway-v<version>-win-x64.zip`, `SHA256SUMS` and `install.ps1`, tagged `dbdatagateway/v<version>`; it pushes no
branch. The version is the UTC build time, `YYYY.M.D.HHmm` (for example `2026.10.17.1530`); a `-beta` suffix marks a prerelease. See
`docs/releases.md` at the repository root.

Releases of the other products are published in the same repository, so a DbDataGateway release is recognised by its
`dbdatagateway/v` tag. GitHub's own "latest release" is not used (it is one marker for the whole repository).

**Installer.** Each release has `dbdatagateway-v<version>-win-x64.msi` (the newest at
<https://github.com/DbDataSync/DbDataKit/releases/download/dbdatagateway/dist/dbdatagateway.msi>): copy it to the machine and double-click, or
`msiexec /i dbdatagateway-v<version>-win-x64.msi /qn`. It installs to `%ProgramFiles%\DbDataGateway` and registers the `DbDataGateway` service, stopped
(`INSTALLSERVICE=0` skips that). Install a newer one over it to upgrade: the service is stopped, the files replaced, the registration kept, the service
started again. Add/Remove Programs removes it; settings and certificate in `%ProgramData%` stay. It is not signed yet, so Windows shows "Unknown publisher".
Then configure and start it as below.

To install or upgrade from a release with a script instead, run from an elevated PowerShell prompt (Windows PowerShell 5.1 or later). The install files are the
repository's standard ones ([`dist/README.md`](../dist/README.md)) and these URLs never change:

```powershell
& ([scriptblock]::Create((irm https://github.com/DbDataSync/DbDataKit/releases/download/dbdatagateway/dist/install.ps1))) -InstallService
```

The script finds the newest stable DbDataGateway release, verifies the download against `SHA256SUMS`, installs to
`%ProgramFiles%\DbDataGateway` (`-InstallDir`), stops and restarts a running service around an upgrade (the settings are in
`%ProgramData%`, so replacing the program files never touches them), and registers the service only with `-InstallService`. Also `-Version`, `-IncludePrerelease`, `-DryRun` (needs no
elevation) and `-Repo`; Scoop works too: `scoop install https://github.com/DbDataSync/DbDataKit/releases/download/dbdatagateway/dist/dbdatagateway.json`
(registering the service is then `dbdatagateway service install` from an elevated prompt).

## Configuration

Settings are YAML files in the `config` folder of the data directory: `%ProgramData%\DbDataKit\gateway\config` by default (`DBDATAKIT_DATA_GATEWAY`, `DBDATAKIT_DATA`, `Gateway:DataDirectory`,
`--data-dir` on `service install`; on Linux `/var/lib/dbdatagateway/config`), a folder a git repository can track:

```
config/
  server.yaml            listen, tls, secrets, compression, hosting, worker, logging switches, schemaVersion
  gateway/auth.yaml        sessions, lockout, pass-through, Windows sign-in, JWT and its mappings
  connections/<name>.yaml  one data source per file, in the shared connection format (connection/1); the file name is the data source name
```

A data source is the same file a Sync or Build connection is (`schema: connection/1`): `engine` (`sqlserver`, `odbc`, `duckdb`, `postgres` or `flightsql`), `connection_string` (or `address` for SQL Server, or a whole-string `connection_string_secret`), the login as `auth: { mode: sql, user: ..., password_secret: env:NAME }` (a reference, never the password; a plaintext credential in the connection string is refused), `tls`, `timeouts: { command_s: ... }`, and what only the gateway has under `extensions.gateway` (`pass_through`, `bitness`, `classifier`, `allow_writes`, `metadata`, `allowed_directories`, the limits, `batch_size`, `compression`; snake_case keys). **`allow_writes` and `allowed_directories` are not part of any published definition** (plan 13c D19, owner decision of 2026-11-09, corrected the same day): a publishing client's definition that carries either is refused (403, naming the field and the line), and a publish replaces the whole file, so a data source a client publishes is read-only and reads no directories outside its database. Editing a published data source file on the gateway is not supported (the next publish overwrites it); nothing guards against it. A source that cannot write connects with the read login and one that can with the write login. A data source file of earlier releases (no `schema:`) is refused with a message naming it.

`service install` creates `server.yaml` with only `schemaVersion: 2` if there is none. Defaults are in the code, not in a shipped file, and
`config.example/` beside the executable has one example of each file with every option. Keys are camelCase in the files (matched ignoring case).
Layers, lowest first: the files, `DBDATAKIT_` environment variables (`DBDATAKIT_Gateway__Listen__Port=9000`; the other `DBDATAKIT_*` variables of the suite, such as `DBDATAKIT_DATA`, are not under `Gateway` and are ignored here), the command line (`--Gateway:Listen:Port=9000`).
`DataDirectory` itself comes from the command line, the environment or the default, never from a file, since the files live in it. Data sources,
compression and auth settings reload without a restart; the listener and certificate need one.

**An `appsettings.json` from an earlier release is refused**, not read and not converted: the server stops with exit code 2 and a message that names the file
(in the data directory, its `config` folder, or beside the executable) and where its settings go now. Move the `Gateway` section's keys into the files above
(`DataSources` entries into `connections/`, `Auth` into `gateway/auth.yaml`, the rest into `server.yaml`), then delete the old file.

### The management API

A console manages a gateway through an API on a listener of its own (`management:` in `server.yaml`). **It is on by default** (plan 33 D15): https on `management.port` (8816) with the gateway's generated self-signed certificate (`Tls.Mode` SelfSigned, the default), bound to the loopback address `127.0.0.1` unless `management.address` says otherwise, never the Flight port. A fresh install is therefore reachable only from its own machine, and every call needs a token that a pairing issued, so nothing can use it until an administrator runs `dbdatagateway server pair`. A console pairs with `https://localhost:8816` (`dbdatakit instance add https://localhost:8816 --code <code>`), or with the address of this machine when `management.address` is `0.0.0.0` or a reachable address (a container image's compose file sets it so a console in another container can reach it). **To turn it off, set `management: { enabled: false }`** (`DBDATAKIT_Gateway__Management__Enabled=false`): nothing listens on the port then, and `server pair` says plainly that no console can reach a gateway whose management API is off. The API is https only: with `Tls.Mode: None` a configuration must also say `management.enabled: false`, or it is refused at start naming both fixes (choose another TLS mode, or turn the management API off).
The two are kept apart by the connection they arrive on, not by number: a management path on the Flight port is a 404, and the management port answers only its own paths and the shared `/health` and `/version`.

* **Pairing** is the one every DbData server has: `dbdatagateway server pair [--max-role ...]` prints a single-use code, a console pairs with it over https and pins this gateway's certificate, `server clients` lists the paired clients and `server unpair <client>` removes one. They are recorded in `server.yaml` (`instanceId`, `pairedClients`); the codes wait in the data directory, not in the folder under version control. `--repo` names the data directory.
* **Calls** carry the console's short-lived signed token (`X-DbData-Token`), and each call needs a role: `GET /api/v1/about` (viewer), `GET /api/v1/config` (admin: every effective setting with the file and line, environment variable or default it came from, secrets masked, and what the validator says) and `GET /api/v1/state` (viewer: the engine, the connection with secrets masked, the modules' versions in the registry against this binary, the migrations pending, where each setting came from and the other live instances) and `GET`/`POST /api/v1/state/maintenance` (viewer reads, admin sets `{"enabled": true, "reason": "..."}`: the server answers 503 with `Retry-After` and the reason to everything but these calls, the health endpoints and an administrator, and reports `idle` when what was in flight has finished; audited as `state.maintenance`, refusals too; nothing is persisted, a restart leaves maintenance). See `DbDataKit/src/DbDataKit.State.Api/README.md`. `POST /api/v1/config/validate` (admin: `{"files": {"connections/erp.yaml": "..."}}` says whether the configuration would be valid with those files in place of the current ones; nothing is written). No answer contains a secret: a test writes a marker into every place a secret can be set and looks for it in every answer.
* **Data sources** (admin): `GET /api/v1/data-sources` lists the files of `connections/` with their version (a hash of the text), `GET .../{name}` reads one, `PUT .../{name}` with `{"text": "schema: connection/1 ...", "expected_version": "<hash>" or null to create}` writes one, `DELETE .../{name}?expected_version=<hash>` removes one, `POST .../{name}/test` connects and runs `SELECT 1`. A write is validated with the rest of the configuration as it would be once written, and refused with the diagnostics (nothing is written) if that would make it invalid; a write based on a version that is not the current one is a 409 with the current version; a write is applied at once (no restart: the next query uses it), committed to the configuration folder's git history as the person who made it, and noted in the management audit (who, which console, what, the outcome; never content), which is in the state database. A failed test answers with the failure kind and code, never the driver's text.
* **Adopting a data source** (a person with the admin role): a data source made by hand, or by another client, is handed to a paired server client (a Build server) with `POST /api/v1/data-sources/{name}/adopt` (`{"client": "<client id>"}`), after which that client changes and removes it like one it made; `GET /api/v1/clients` (admin) lists the server clients that can take one over (id and name); a server client cannot be handed one this way, and a source with `extensions.gateway` is refused (409) unless the body says `"adopt": true` (the next publish by the client replaces the whole file with its definition, the file goes to the history first, and the answer lists `replaced_by_next_publish`). **A publishing client adopts a data source made by hand itself** (plan 13c D19) by publishing over it with `"adopt": true` (`PUT /api/v1/data-sources/{name}`, Build's Publish tab): without it the write is a 409 that says so; with it the definition replaces the file, the file as it was is committed to the configuration history first, and the answer has `adopted`, `changes` (every field the definition added, changed or dropped, by dotted name; connection values are named, not repeated) (a file's `allow_writes` and `allowed_directories` are among the dropped). A data source another client manages is not adopted by anyone. On this machine the same call is `dbdatagateway datasource adopt <name> <client id or name> [--repo <path>]`, made over the local channel (so `management.localChannel: true` and a running gateway); the client is named as `server clients` lists it, and the audit line names the operating-system user. The console's Data sources page and Build's Publish tab have a button for it.
* **Client identities** (admin): `GET /api/v1/identities` shows whether pass-through and Windows sign-in are on and the JWT settings and mappings, as in effect, with `has_signing_key` and never a key. Gateway's own users are retired (plan 13a P13): `users` and `windows_accounts` stay in the answer, empty, and `PUT`/`DELETE /api/v1/users/{name}` answer `410 Gone` (people are the identity home's: see "Authentication"). `PUT /api/v1/auth` (`{"allow_pass_through": false, "expected_version": ...}`) switches pass-through in `gateway/auth.yaml`. Writes are checked, committed, audited and applied like a data source's. The JWT mappings are changed in `gateway/auth.yaml` for now.
* **What it is doing** (admin): `GET /api/v1/sessions` (who is signed in, how, with which data sources; a session is named by the front of its token's hash, which is not a token) and `DELETE /api/v1/sessions/{id}` end one; `GET /api/v1/queries` lists the running statements (user, data source, kind, how long, and the text when `Gateway:LogSql` is on, cut to 500 characters) and `POST /api/v1/queries/{id}/cancel` cancels one, which ends it for its client as `Cancelled`; `GET /api/v1/limits` reports each data source's limits with the slots in use, waiting and refused, the login throttle's settings and who it is holding against, and `DELETE /api/v1/throttle?client=&user=` lets a locked-out client try again. Each of those that changes something is noted in the management audit.
* **The certificate and the failures** (admin): `GET /api/v1/certificate` describes the certificate in use (mode, subject, thumbprint, validity, days left, the names it is good for, whether it is due for renewal; never its key). `POST /api/v1/certificate/renew` with `{"confirm": true}` renews a self-signed certificate now, the way `cert renew` does, and puts the new one in use at once; the certificate every paired console pinned is then gone, so each has to be paired again, which is why it asks first. `GET /api/v1/failures` lists the last 200 database failures the gateway classified, newest first: the kind, the engine's code and the fixed sentence a client was told, never the driver's text.
* **State database:** `state.db` in the data directory (SQLite) unless the `state:` section of `server.yaml` names an engine. It is the section every DbData product reads (plan 29b C07): `state: {engine: postgres, connection: "Host=db;Database=dbdatagateway_state;Username=gateway;Password=secret:state-db", namespaces: {default: {schema: dbdatagateway}}, retention: {days: 30}}` (`engine` is `sqlite`, `mssql` or `postgres`; the password is a secret reference, resolved when the server starts; `registry`, `namespaces` and `retention` are optional). A setting that is not one is refused with the file and line and the nearest setting that is. The gateway's other layers still apply, in the order they always did: the file, then `DBDATAKIT_STATE_GATEWAY_ENGINE` and `DBDATAKIT_STATE_GATEWAY_CONNECTION` (or the install-wide `DBDATAKIT_STATE_ENGINE` and `DBDATAKIT_STATE_CONNECTION`), then the `DBDATAKIT_` and plain `Gateway__State__...` variables, then `--Gateway:State:Engine=` on the command line. **A file with the keys of the earlier release** (`state.connectionString`, or `state.definitionPollSeconds`, which is now `definitionPollSeconds` at the top level) **stops the start naming the file and the key** and showing the new shape; nothing is converted. The gateway needs a database of its own; a console on the same server uses another. A change needs a restart, and changing the engine starts an empty store. `dbdatagateway state init --topology service|group|install` (the shared verb, see `DbDataKit.State.Cli`) writes the `state:` section of `server.yaml` (in the `config` folder of the data directory) and prints or, with `--admin-connection` and `--apply`, runs the schemas and the login's rights for a group or install database. `dbdatagateway state show` prints the engine, the connection (the password masked), **where each value came from** (a flag, an environment variable, the file, or the default; `--json` has it as `sources`), where each module's tables are, each module's version in the registry and what is pending, and creates nothing; `dbdatagateway state test` connects and checks the registry, the rights, the engine version and a schema clash, and stops at the first failure with one sentence naming the fix (exit codes 10 to 18, one per kind); `dbdatagateway state grants --login <role>` prints the `GRANT` statements for the login the gateway runs as (`--migrate` for the one that upgrades the database). To change the engine and keep the state (the group's sessions, pairings, published definitions, notifications), stop every instance and run `dbdatagateway state move --to-engine <engine> --to-connection <string>` (it exports, imports into the empty target, verifies and then writes the `state:` section of `server.yaml`), or in steps `dbdatagateway state export --to <directory> [--include-secrets-check] [--repo <data directory>]`, set the new engine, and `dbdatagateway state import --from <directory>` against the empty new database; `dbdatagateway state verify --snapshot <directory> [--db]` checks a snapshot against itself or the database against it (`--other-engine`/`--other-connection` compare two databases, the connection with secret references only). The group's secrets stay encrypted in the snapshot; the key (`dbdata:gateway:state:session-key`) is in each instance's secret store and is not part of it.
* **A group of instances** (plan 20 O02): instances that share a SQL Server or PostgreSQL state are one group. A client's session is in the state, so any instance serves it (only the hash of its token is kept, and a pass-through session's database user name and password are encrypted with the group's key); the management audit is there too, one for the group (an older `management-audit.log` is read in once and renamed `.imported`). The key is the secret `dbdata:gateway:state:session-key`, made by the first instance; copy it into every other instance's store (`dbdatagateway secret set dbdata:gateway:state:session-key --stdin`). An instance with another key refuses to start and names the secret. **SQLite is one instance's**: a second instance started against the same `state.db` while the first runs is refused with a message that says to use SQL Server or PostgreSQL.
* **Rolling upgrade of a group** (plan 29b U02, plan 24): every instance writes its software version and the versions of its state modules into its row of the group's state (`serving_instance`), so `dbdatagateway state show` and `GET /api/v1/state` list the live instances with their versions. A new release that changes the state's tables migrates the state when its first instance starts, and **refuses to migrate while a live instance runs a version that cannot work with the new tables**, naming that instance (id, host, process, software and module version): the start fails and the old instances go on. The order: if the release notes say the previous version is compatible (the `serving` module declares it; the release that added these columns is), start the new instances one at a time while the old ones run, then stop the old ones; otherwise stop the old instances first (the group is down for the migration), or stop the one the refusal names and start again. An instance whose heartbeat lapsed (30 seconds) never blocks; an old instance that starts after the state was migrated finds the newer module version and refuses to start, naming itself (upgrade it).
* **Formats and generations in the instance row** (plan 24a UC01 and UC05): the same row (`serving_instance`) says which format of each kind of published definition the instance reads (`definition_formats`: `data-source`, `entity-model` and `semantic-model`, each 1 to 2) and the compatibility generation of its binary (`generation`, plan 24 section 2). A publish through the management API of a definition in a format that a live instance of the group does not read (a semantic model whose catalog has a `cache` or `checks` section, a data source with `schema: connection/2`, a model with `schemaVersion: 2`) is refused with `409` naming that instance, its host and software version; an instance that is handed a format it does not read keeps serving what it served and says why in its log. `state show` and `GET /api/v1/state` list each instance's `generation` and mark one more than one generation behind the group's newest (`outside_n_minus_1`).
* **One server to its clients** (plan 20 O04): a group is one server to the console and to Build. Its instance id, its paired clients and the pairing codes waiting are in the state, so `dbdatagateway server pair` on any machine of the group (it opens the state `server.yaml` names) makes a code that pairs through any instance, and a client paired through one is paired with all of them; `server clients` and `server unpair` work on the group. The first instance to run against the state gives the group its instance id (the one in its `server.yaml`), so a server that becomes a group keeps its pairings; an instance whose own `server.yaml` names another id serves as the group and logs that its own id's clients pair again. Clients listed in an instance's `server.yaml` keep working at that instance. With `tls.mode: selfSigned`, the group presents one certificate, which a console pins when it pairs: the first instance's, kept in the state encrypted with the group's key; when it is due, the first instance to renew it puts the renewed one there and the others take it at their next check (every 6 hours), and `POST /api/v1/certificate/renew` replaces it for the group. A certificate from a file or the certificate store is the operator's to give every instance.
* **The `store` module** (plan 20 O08a): list `store` in `modules:` and master data is read and written here (`store: { address, port }`, 8819 by default, https with the Gateway's certificate), through the models a DbDataStore server published to the group, straight to the model's tables, so it goes on with that server stopped. The paths are a DbDataStore server's, so a client is written once: `GET /api/v1/models`, `GET /api/v1/models/<m>/entities/<e>/row?key.<attribute>=<value>` (with `as_of=<time>` the version that held then, `history=true` every version), `.../rows?limit=&offset=`, `POST .../changes` (`{"changes": [...], "description"}`: written at once through every rule, version check and audit row, a refusal a 422 naming each rule broken), and the change requests (`GET .../requests[?state=]`, `.../requests/<id>`, `POST .../requests/<id>/approve|reject`), kept in the group's state so any instance takes, lists and decides them. The callers are the identity home's people and service accounts: a personal access token (`Authorization: Bearer ddk_...`, or as the password of Basic) or an access token the home issued (checked against its signing keys when this instance holds their key). The home's roles on this group's server id decide: viewer reads, author writes, and approving a change request is an author's, or an administrator's when the model asks for its owner; a service account is an agent, whose writes wait for a person unless the model says `approval: {agents: direct}`.
* **Apps** (plan 26, AP26): a paired client with the publisher role publishes apps with `PUT /api/v1/apps/<name>` (`{"manifest": "<app.yaml>", "files": {"<path>": "<base64>"}, "note": "..."}`, the same strict checks and trusted-app gate as `apps publish`), moves the served version with `POST /api/v1/apps/<name>/rollback`, makes instances with `PUT /api/v1/app-instances/<name>` and lists with `GET /api/v1/apps`; `POST /api/v1/app-approvals` approves exact bytes and needs the approver role (a pairing of its own). For the console's Apps screen (AP24a) the reads are open to a viewer (or publisher): `GET /api/v1/apps/<name>` (versions, the approval standing for each, and what each version's manifest declares it can reach), `GET /api/v1/app-instances` (the version each runs, pinned or following; its address on the apps host), `GET /api/v1/app-navigation`, `GET /api/v1/app-approvals`, each with `caller` (`can_publish`, `can_approve`, `you`); `DELETE /api/v1/apps/<name>` and `DELETE /api/v1/app-instances/<name>` stop serving one (publisher or admin; the store's refusal is a `409` with its message); `POST /api/v1/app-approvals/<id>/revoke` (`{"by": "<a home user who holds the Approver role>"}`) needs the approver role like approving does. The console's editors (plan 33, UE01 to UE03) add `POST /api/v1/apps/check` (the body of a publish; says what a publish would meet, the version it would be, the content hash and whether an approval stands, and stores nothing), `PUT /api/v1/app-navigation/<name>` (`{"title", "entries": [{"instance", "label", "icon"}], "rename_from"}`: make, reorder or rename a navigation set; the store refuses an unknown or repeated member in its words, `400`) and `DELETE /api/v1/app-navigation/<name>` (`404` when it is not served), all for the publisher or admin. Every change is in the audit (`app remove`, `app instance-remove`, `app approval-revoke`, `app nav-save`, `app nav-remove`). `dbdatagateway apps publish|instance-save|rollback|list|approve --to <address> [--code <pairing code>] [--client-name <text>] [--client-dir <folder>]` is the command line for it from another machine; the author recorded is the paired client's name.
* **The `views` module** (plan 32 DBR11b): list `views` in `modules:` and the dashboards and reports published to the group are read and rendered here (`views: { address, port, cacheSeconds, maxExportRows }`, 8820 by default, https with the Gateway's certificate), as the identity home's people: `GET /api/v1/dashboards` and `/reports` (what the caller may open), `GET .../<name>` (the file as published, the document, its layouts, the registry's version), `POST .../<name>/render` (`{parameters, layout, only, viewport, medium}` gives the rendered widgets), `GET .../<name>/export?format=pdf&layout=&p.name=` (a PDF drawn by the pinned Chromium, as that person; off, with a 501 naming the setting, unless `views: {export: {mode: pinned|system, pageAssets: <folder with print.js and print.css>, ...}}` is set; plan 32 DBR15, `docs/views.md` Serving) and `GET .../<name>/print`, `/health`. The token is a personal access token or an access token the home issued; every widget runs through the serving core **as that person**, so a person whose home groups may not read the data source gets an error entry (`denied`, in words) for each widget that needed it and the others still draw (row-level rules are plan 22's, not built: the access that differs today is the data source's groups). Every call is audited (who, what, version, outcome, the names of the parameters and never a value). The Gateway's one definition follower hands the module each served version, so a publish through any instance is served by all, and a version this instance cannot load keeps the one before and shows as **degraded** in the readiness component `views`. The same routes answer a console on the management API (`/relay/<gateway>/api/v1/dashboards...` from the console, as the person the console vouches for: a person who signs in through the identity home is that home user), plus `POST /api/v1/views/preview` (the live preview of an unsaved draft, DBR28b: in process, as that person, the viewer role, refusing what a publish would refuse, audited as `view.preview`), `GET /api/v1/views/status` (`{enabled, state, detail, address, dashboards, reports}`, `enabled: false` when the module is off) and `PUT /api/v1/dashboards/<name>` and `/reports/<name>` (`{text, expected_version?, note?}`; the author, publisher or administrator role) which publish. A document depends on the **semantic models published to the group** (by `dbdatabuild`, or `PUT /api/v1/semantic-models/<name>`; a model set up in `server.yaml` is not a published definition and cannot be depended on), pinned to the version the channel serves when it is published; the registry checks it (the loader, then the engine's validator over each model version, the saved queries and control attributes) and refuses a stale `expected_version` (409), a model it has no version of (`DBD-VIEW-060`) or a field the model lacks, naming it, and stores nothing. `dbdatagateway views publish --file <document> [--kind dashboard|report] [--name <name>] [--expected <version>]` does the same from the command line on the data directory (with the server stopped or running, and with the module on or off), `views list` shows what is served. An app's narrowed token reaches the module through the apps host in process (`ViewsApi` with a `ViewsAppScope`): not wired yet. **A person's own dashboards and reports** (plan 32 DBR12) are on the same module: `GET /api/v1/my/views` and `/api/v1/my/dashboards|reports/<name>` (open, save with `expected_version`, delete, render, history, restore, shares, promote), kept in the state module `user_views` (it joins the Gateway's modules; the migration runs at the first start), owned by the home user's id, shared to a person, a home group or everyone, and resolved as whoever opens them (their rows); audited as `view.my.*`. See `docs/views.md`.
* **Master data models** (plan 20 O10): a DbDataStore server paired as a publishing client publishes its models to the group with `PUT /api/v1/entity-models/<name>` (`{"model": "<model/1 yaml>", "data_source": "<name>", "expected_version": <n>}`): refused unless the model loads, has the call's name, and its data source is one of this server's (for a publishing client, one it made); `GET` lists them (with `served_version`, `failed_version` and `failure` for the instance asked) and reads one, `DELETE` stops serving it. Each is the definition `entity-model/<name>`, which every instance loads, keeping the last version it could; the store module (O08) reads and writes through it.
* **What its clients publish** (plan 20 O04b): a data source a publishing client (a Build server) makes through any instance of a group is the group's: kept in the state as a published definition, served by every instance, and written by each one to `group/connections/` of its data directory, which the configuration reads after `connections/` (a hand-made data source of the same name wins, and one made by hand stays the instance's own file and history). The secrets such a client keeps on the group (`PUT /api/v1/secrets`, its read login) are kept in the state too, encrypted with the group's key, so every instance can connect; an operator's secrets stay in each instance's store. `GET /api/v1/data-sources` marks the group's with `"group": true`. **The serving policy is the published definition's too** (plan 13c D19, owner decision of 2026-11-09): the connection file's `extensions.gateway` (`pass_through`, `bitness`, `classifier`, `metadata`, `default`, the limits, `batch_size`, `compression`) is sent with the data source and a republish replaces it whole, no merge, `allow_writes` and `allowed_directories` are not part of a definition (refused). `GET /api/v1/data-sources` and the data source screen show it (`policy: {origin, fields, blocked}`, read-only). **The Publisher role is therefore high-trust**: a client that holds it chooses the serving policy of the data sources it publishes (pass-through login, limits, classifier mode, batch size, compression), everything but those two fields, which a definition cannot carry; pair a Build server only when its operators may be trusted with that. A Gateway-side ceiling per client is a later option, not built. **Who may read it is the published definition's `grants` section** (plan 22a GP03, owner decision of 2026-11-08; `schema: connection/2`, written in a Build project's connection file): `grants: {groups: [finance]}` or `grants: {everyone: true}`. A republish replaces the whole file, so it replaces the grants: groups removed from the list lose access at once, groups added gain it, a publish with no change leaves access as it was, and a publish with no `grants` closes the data source to home users. The Gateway has no grants of its own to add and does not restrict the definition's (it still refuses a caller who is not signed in or not in a granted group); the data source screen and `GET /api/v1/data-sources` show them (`grants: {groups, everyone, source}`) and change nothing: 'Granted by the published definition; change them in the project and publish again'. A publishing client still may not send `extensions.gateway`, and a file that has `grants` and `extensions.gateway.groups` or `everyone` is refused (`grants` is the one place). A Gateway that rejects a publish contradicting its own settings is a possible later feature, not built. With the semantic models (O03), a package a Build server deploys once through one instance is served by all of them, and by one started later with the publisher stopped.
* **Notifications** (viewer): the gateway tells its notification feed (in `state.db`) when a client is locked out after failed logins (`LoginLockedOut`: the user and the client address), when a statement is refused on a read-only data source (`StatementRefused`: the user, the data source and the rule, never the statement), and when a data source turns a caller away because every slot is in use (`LimitReached`). The same kind about the same subject is told once per ten minutes. `GET /api/v1/notifications?since_id=N` returns what is newer than the id a console holds.
* **The overview and the history** (`GET /api/v1/overview`, viewer: version, instance, uptime, counts of data sources, users, sessions, running queries, clients locked out, failures in the last hour; `GET /api/v1/config/history[?limit=]` and `GET /api/v1/config/history/{sha}`, admin: the commits of the configuration folder, the first being "Configuration as found", and what each changed, both sides of each file with password hashes, signing keys and certificate passwords masked).
* **The local channel** (`management.localChannel: true`) answers the command line on this machine over a named pipe (Windows; SYSTEM and Administrators) or a Unix socket (the service's account), as an administrator, with messages `{"method":"GET","path":"/api/v1/config"}`.

### Modules

A gateway serves **modules** (plan 20, O01), listed in `server.yaml` as `modules: [flightsql, identity]`. `flightsql` (Arrow Flight SQL, the default), `identity` (the home below, which `identity: {enabled: true}` also turns on), `apps`, `store` and `views` exist so far; a name that is not a module is refused, naming `Modules` and the known ones. An instance without `flightsql` opens no Flight port. `apps` (plan 26, AP05) serves the app instances published in the Gateway's state at `https://<host>:<Apps.Port>/apps/<instance>/` (default port 8818, its own listener, https only): the entry document with a base path, the bundle's files, and on every answer the `Content-Security-Policy` of the instance's approved egress, with the `sandbox` directive for a sandboxed app. A person signs in through the identity home (`Apps.Home`: the client registered there with `identity client-add`, redirect URI `<public url>/apps/-/callback`; the home defaults to this instance's own) and needs a role on `app:<instance>` for the page at `/apps/<instance>/`, which frames the app's bundle (`/apps/<instance>/_app/`) with the `sandbox` attribute. The host asks the home again with the person's refresh token once its last answer is `Apps.RolesRefreshSeconds` old (300 by default), so a role taken away at the home stops working here within that time, and a person the home disabled is signed out; if the home cannot be reached the last answer stands and the host tries again after 30 seconds. The bundle itself is not behind the sign-in (the sandboxed frame sends no cookie); it is published code, and data needs a token. An app reads and writes master data through the host (`/apps/<instance>/_data/api/v1/models/<model>/entities/<entity>/row|rows` and `.../changes`, with the token the shell hands it): the host answers from the models published to the group (plan 20 O08c), in its own process, as the person with the roles the home gave them on the instance and only for the entities the manifest's `models` list (read, or read and write), with every rule of the model; it needs neither the `store` module's listener nor a DbDataStore server, and an instance bound to a copy other than `default` is refused until the group serves copies. The host no longer pairs with Store servers: `apps store-pair`, `store-list` and `store-remove` say so, and the path with a store's id is a 410 naming the one to use; publish an app with `dbdatagateway apps publish --dir <folder>` (the folder holds `app.yaml`, the manifest, and the files the host serves; each run is a new version) and serve it with `apps instance-save --name <instance> --app <app> [--pin <version>] [--setting k=v] [--bind model=storeInstance]` (`instance-list` shows them; the host serves a saved instance at once);. `apps nav-save --name <set> --title <text> --item <instance[=label]>,...` makes a navigation set (`nav-list` shows them): the shell's header shows, in every member instance, a menu of the members the signed-in person holds a role on, and the app's route (`postMessage` `route`) is kept in the address so the back button and a pasted link work; an app whose manifest says `chrome: none` gets no header. An app that declares `storage: {user: true, instance: true, quota_mb: 10}` in its manifest keeps small JSON values (a person's own, and the instance's) in the group's state through the shell's `postMessage` bridge, because a sandboxed document has no browser storage: 256 KB a value, 128 characters a key, the quota for the instance. Later modules (`store`, `rest`, ...) are listed the same way.

### The identity home

The gateway can serve the install's **identity home** (plan 20, O06): who people are and what they may do, for every console and app, so people sign in without reaching the private console. It is the shared
`DbDataKit.Identity` home, the same one `dbdatakit identity run` serves on its own. Turn it on with `identity: { enabled: true }` in `server.yaml`:

* **Its own listener:** https on `identity.port` (8817, every address unless `identity.address` says otherwise), HTTP/1.1 and HTTP/2 for browsers, with the gateway's certificate; never the Flight or management port (the
  home's paths are a 404 there). TLS is required (a passkey needs a secure origin).
* **Its address:** `identity.publicUrl` is the https address every console, app and browser reaches it at (a balancer's name for a group of gateways), without a path. It is the token issuer and the origin
  passkeys are bound to, so it must not change once people have signed in. Unset, it is `https://<the first tls.hostnames entry, or localhost>:<port>`.
* **What it serves:** OpenID Connect discovery (`/.well-known/openid-configuration`), the provider (`/identity/authorize`, `token`, `userinfo`, `jwks`, `end-session`; authorization code with PKCE), the passkey
  sign-in page (`/identity/sign-in`) and the invitation page (`/invite#<code>`).
* **The first administrator:** while the home has nobody, a first-run invitation is written to `identity/FIRST-RUN.txt` in the data directory; open its link on the home's address and make a passkey.
* **Administration, at the command line** (the server may be running or not; `--repo` names the data directory): `dbdatagateway identity invite --email <address> [--role admin|viewer]`, `identity user-list`,
  `identity client-add --name <name> --redirect <uri>` (a console or an app that signs people in through the home; the secret is shown once), `identity client-list`,
  `identity assign --user <id> (--server <id> [--project <name>] | --app <id>) --roles <roles|none>` and `identity map-group --source entra|windows --group <group> ... --roles <roles|none>`.
* **Personal access tokens and service accounts** (13a U20): a person makes, lists and revokes their own tokens at the home, signed in (`/api/access-tokens`), and uses the token as the password, with their
  address as the user name, wherever a client can send only a user name and password. A token lasts 90 days unless asked otherwise, at most `identity.accessTokenMaxDays` (365 unless lowered); it is shown
  once and kept only as a hash. For unattended work, `identity service-account-add --name <name>`, then `identity assign --user <its id> ...` and `identity token-create --account <name> --name <name>
  [--days <n> | --no-expiry]` (the account's name is the user name). `identity token-list [--user <id>]` and `identity token-revoke --id <id>` see and revoke anyone's; `identity service-account-list` and
  `identity service-account-remove --name <name>` (its tokens are revoked).
* **A console signs in through it** with `dbdatakit server run --identity-home <publicUrl> --identity-client <client id> --identity-secret-env <variable>` (the client registered with `identity client-add`,
  its redirect `https://<console>/api/identity/callback`).
* **State and keys:** its people, roles, clients and signing keys are in the gateway's state database (`state:`), so gateways that share one are the same home. The signing keys are protected with the key in the
  secret `dbdata:gateway:identity:signing-keys`, made on the first start; copy it into the store of every other gateway of the group (`dbdatagateway secret set dbdata:gateway:identity:signing-keys --stdin`). A gateway
  whose key cannot read the signing keys in the state refuses to start and names the secret. A backup of the home is the state database and that secret.
* **Signing in through Entra ID** (O06b): register an app for the home in the tenant, with the redirect URI `<publicUrl>/identity/upstream/entra/callback`, a client secret, and the `groups` claim in its id token;
  then `identity: { entra: { authority: https://login.microsoftonline.com/<tenant id>/v2.0, clientId: <app id>, clientSecret: secret:dbdata:gateway:identity:entra } }` (the secret set with
  `dbdatagateway secret set dbdata:gateway:identity:entra --stdin`). The sign-in page offers **Sign in with Microsoft Entra ID**; the person is the home's user found by their Entra id (or, with `trustEmail: true` for a
  tenant whose addresses its administrators set, by their address), and the groups their token carries get what `identity map-group --source entra` maps them to, again at every sign-in. A sign-in the tenant
  refuses, or whose token does not check out, signs nobody in and the page says why.
* **Signing in with Windows** (O06b): `identity: { windows: { enabled: true } }` adds **Sign in with Windows** (Negotiate). On a Windows host the account's SID is the credential, its user principal name the
  account key, and its group SIDs and `DOMAIN\group` names what `identity map-group --source windows` matches. On Linux it needs the home's keytab (`KRB5_KTNAME`); the Kerberos name is then the credential.
* **From a console** (13a U21): with `management:` on too, a paired console's Gateway screens have an **Identity home** item: people, role assignments, group mappings, groups, consoles and apps, service accounts and tokens, through the management API's `/api/v1/identity/...` (the admin role; a gateway with `identity` off answers 404).
* **In a group** (O02b): any gateway of the group serves the home, whichever one a browser or console reaches. What a sign-in holds between two requests (a passkey ceremony's challenge, an Entra sign-in's
  verifier and nonce) is in the state and used once, as are the ids of the management tokens a console sent, so a balancer needs no sticky sessions. Give every gateway the same `identity.publicUrl`, both keys
  (`dbdata:gateway:identity:signing-keys` and `dbdata:gateway:state:session-key`) and, for the console to see one server, the same `server.yaml` instance id and paired consoles. **Windows sign-in with NTLM**
  takes several requests on one connection: a balancer must keep a connection on one gateway (any layer-4 balancer does), or use Kerberos, which takes one.

### Validation, schema version and the config commands

The configuration is the files above, with the same environment and command line overrides as before. It is read through the shared `DbDataKit.Config` strict binder, so a typo is no longer ignored:

* **Strict at startup.** An unknown key (`Gateway:Listen:Prot`), a value of the wrong type, a number out of range, a
  user that names an undefined data source, `Tls.Mode` `File` without `CertificatePath`, `Bitness` `x86` with a
  provider other than `odbc`, `PassThrough` on DuckDB, an unknown provider, a duplicate or empty user name, a codec the
  compression mode does not support, JWT without `Audience`, ... all stop the service before it listens. Every problem is
  listed once with its key path in environment-variable form, where it came from (file with line and column, `env:NAME`,
  `cli:args`) and, for unknown keys, a "did you mean" suggestion. Secret values are never printed.
  The process exits with **code 2** (the same code as a command line usage error); scripts and service managers can rely on it.
* **Case-insensitive, unchanged keys.** Keys are matched ignoring case, exactly as before, so every existing file and
  override (`DBDATAKIT_Gateway__Listen__Port`, `Gateway__Tls__Mode`, `--Gateway:Listen:Port=9000`) keeps working, booleans may
  still be written `True`. The example and the starter file are checked by the test suite.
* **Reloads keep the last good configuration.** An edit of a configuration file that is invalid (syntax error, unknown key,
  failed validation) is rejected with an error in the log naming the problems, and the running service keeps using the last
  good configuration; it never crashes on a bad edit. A later valid edit is picked up.
* **Forward compatibility, explicitly.** `allowUnknownKeys: true` (or `--allow-unknown-keys` for `config check`) turns
  unknown keys into warnings, for example while rolling a file back to an older release. The default is strict.
* **`SchemaVersion`** (optional, inside `Gateway`; absent means 1). Version 2 changes defaults only: a data source without
  its own `Classifier` is `strict` instead of `warn`. Files without `SchemaVersion` keep every old default, so upgrading
  changes nothing; a startup warning lists the read-only sources that still rely on the old default. The example and the
  file `service install` writes use `schemaVersion: 2` (new installs get the strict default). Moving v1 to v2 is a
  documented no-op (`GatewayConfigMigrations`: no key changes) other than that default: set `Classifier` on each source, then
  add `schemaVersion: 2`. A file with a newer version than the release understands is refused with an upgrade message.
  Future renames ship as additional migrations in the shared migration framework (see `MigrationTests`); `config migrate`
  is not provided; the files are YAML now, so the shared migration engine can rewrite them when a migration is needed.

```
DbDataGateway config check [--file path]        validate a config folder (or the path of its server.yaml; default: the installed one); exit 0 valid, 2 invalid
DbDataGateway config show [--origin] [--file p] effective values; --origin adds file:line, env:NAME, cli:args or "default"
```

Without `--file` both commands read the same sources as the service (the settings files, `DBDATAKIT_` environment variables, command
line options); with `--file` only that file (plus `--Gateway:...` options) is read.
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
| `flightsql` | Another Arrow Flight SQL server, through the shared `DbDataKit.Connections.FlightSql` client; read only. The connection string is `grpc://host:port;user=...;password=...` or `grpc+tls://host:port;token=...` (see below) |
| `postgres` | Npgsql. Takes a `Host=...;Database=...` connection string (`address:` is SQL Server only); can pass the caller's credentials through |
| `odbc` | System.Data.Odbc. `"Bitness": "x86"` runs the query in the 32-bit worker process |

Connections are made by the shared `DbDataKit.Connections` providers (SQL Server, DuckDB, ODBC, PostgreSQL, Flight SQL): the data source's
settings become a shared `ConnectionDefinition` (the Gateway-only ones, such as bitness, pass-through, metadata mode and
batch size, travel in its `extensions.gateway`), the composer builds the connection string, and failures are classified by
the shared failure model. The configuration keys are unchanged. Statements are read-only unless the data
source sets `"AllowWrites": true` (this only gates statements sent as *updates*; the database account's own
permissions are the real boundary for what a query can do).

What the shared composer does to a configured connection string (behaviour changes with the Connections adoption):

* **TLS for SQL Server** (the `sqlserver` provider and ODBC "ODBC Driver N for SQL Server" / Native Client drivers): a string
  with no `Encrypt` gets `Encrypt=Mandatory` and no `TrustServerCertificate` means certificate validation is on. An explicit
  setting in the string is kept as written (`TrustServerCertificate=True`, `Encrypt=False`, `Encrypt=Strict`). The legacy
  Windows ODBC driver named just `SQL Server` knows none of these keys and is left alone. ODBC PostgreSQL drivers get
  `sslmode=verify-full` unless the string sets `sslmode`.
* **PostgreSQL** (the `postgres` provider, Npgsql): `SSL Mode=VerifyFull` unless the string sets `SSL Mode` (`Require` encrypts without verifying, `Disable` turns
  encryption off; both are named in the startup warning). A source that cannot write sets `default_transaction_read_only=on` on the session, which the
  session itself can turn off, so the statement check below and a read-only database role are the real controls. `?` markers are sent as named
  parameters (`@p1`, `@p2`, ...), which Npgsql binds; a `?` operator of PostgreSQL's `jsonb` is read as a marker, so write such a test with
  `jsonb_exists(...)`. A NULL parameter is sent untyped, so the server takes its type from the statement. A column of an array type arrives as text
  holding its JSON (`[1,2,3]`); types without an Arrow type of their own (`jsonb`, `uuid`) arrive as their text; a `numeric` wider than
  .NET's `decimal` (about 28 digits, or `NaN`) fails the query, so cast it (`::text` or `::numeric(20,4)`). Catalog metadata is read from `pg_catalog`
  and covers the connected database.
* **Flight SQL** (the `flightsql` provider): the connection string is the address and the login of the upstream server in one value: `grpc+tls://host:port` (or `grpc://` and `grpc+tcp://`, without TLS), then `;option=value` parts
  written like an ODBC string (a value in braces may hold `;` and `=`): `token` (a bearer token), or `user` and `password` (the server's handshake), `tls_skip_verify`, `ca_file` (a PEM file of the authority to trust) and
  `header.<name>` (sent with every call; `header.x-datasource=duck` chooses the data source of an upstream Gateway). Write a token or password as a secret reference (`token=env:UPSTREAM_TOKEN`, or the whole string as `connection_string_secret`).
  Defaults: `token_command` and `token_env` (which Build's own logins accept) are **refused** in a data source, because they would run a program or read a variable that the author of the file chose; the read-only classifier uses the **ANSI-only** deny list, because the system behind an upstream server is not known; the source is read only (`allow_writes` is a load error), caller credentials are never passed through, and its metadata comes from `INFORMATION_SCHEMA` queries on the upstream server (a server that does not answer them has no metadata through this Gateway). Microsoft Fabric is not an engine of the Gateway yet.
  `token_command` and `token_env`, which the same grammar has for Build's own logins, are **refused** here: the first would run a program a data source's author chose, the second would read an environment variable they named.
  The data source is read only (`allow_writes` is an error) and takes no pass-through credentials. A statement is checked with the ANSI deny list (the system behind the server is not known), and the upstream server's own login and
  checks are the real control. `?` markers are sent as a prepared statement with bound values (never written into the text); an upstream server that does not implement prepared statements cannot be given parameters.
  Catalog metadata is read with `INFORMATION_SCHEMA` queries on the upstream server, so a server that does not answer them has no metadata through this Gateway; a `decimal(p,s)` column of 28 digits or fewer arrives as a decimal, a wider one as text.
  `grpc://` and `tls_skip_verify=true` are named in the startup warning like the other TLS opt-outs.
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
| `strict` (default from `schemaVersion: 2`, i.e. new installs) | The statement is refused with gRPC `PermissionDenied` |
| `off` | No check |

The check is lexical and refuses some harmless SQL (an unquoted column named like a deny-listed word: quote it). It is
defence in depth, not a security boundary; the database login is. Sources with `AllowWrites` are not checked on the
query path. Upgrade path: stay on `warn` (no `SchemaVersion`) for a release, check the log for would-be refusals, then set `schemaVersion: 2` (or `classifier: strict` per source).

Types map to Arrow as: integers/floats/bool directly (`tinyint` → UInt8), `decimal` → Decimal128 (precision/scale
from the provider, default 38/9 when unknown), `date` → Date32, `datetime/datetime2` → Timestamp(µs),
`datetimeoffset` → Timestamp(µs, UTC), `time` → Time64(µs), strings/GUIDs/anything else → Utf8, `varbinary` → Binary.
ODBC columns of types `System.Data.Odbc` cannot read (e.g. SQL Server `datetimeoffset`/`time` through ODBC) fail
with a message telling you to `CAST` them in the query.

### Semantic queries

A semantic model (a catalog of dimensions, attributes and measures over tables, [`docs/semantic/catalog.md`](../docs/semantic/catalog.md)) is served as a source named `semantic:<model>`, where the model is the catalog's own `name`. In `server.yaml`:

```yaml
semantic:
  level: 5                                  # the engine's adoption level; the highest is the default
  models:
    - catalog: semantic/sales.yaml          # relative to the data directory
      sources: { sqlserver_sales: warehouse }   # each catalog source -> the data source that holds its tables
```

A client sends the statement as it sends SQL, with `x-datasource: semantic:sales_catalog`: the text is a JSON request (it starts with `{`, [`docs/semantic/query.md`](../docs/semantic/query.md)) or semantic SQL (`SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL`, [`docs/semantic/semantic-sql.md`](../docs/semantic/semantic-sql.md)). The answer is an Arrow table of the combined result; the engine's status, request hash, warnings and the steps it left (`remaining`) are in the schema's metadata under `qis.*`.

Every scan the engine plans is run as the signed-in client against the mapped data source, through the same rules as their own SQL: they need access to that data source, its read-only classifier and limits apply, and a failure is classified the same way. `GetTables` on the model source lists its dimensions, attributes, measures and hierarchies. Catalogs are read when the server starts (a catalog that does not load stops the start and names its file). A changed `server.yaml` is applied without a restart: a catalog that loads replaces the model serving, a broken one is logged and the old one keeps serving, and a model taken out of the file stops being served. SQL Server, PostgreSQL and DuckDB sources are supported; a source of kind `ssas` (Analysis Services, DAX) runs on Windows only, and is given its server under the model: `ssas: { ssas_finance: "Data Source=ssas01;Catalog=Finance;Password=env:SSAS_PASSWORD" }` (a secret reference works for the whole string or for the password). **That connection is the server's, not the caller's**: the access rules of the data sources do not apply to it, so whoever may query the model reads what the connecting account can read; give that account only the model's cube. On another system the config is refused at start. A combine (the step that joins the results of several scans) is limited by `semantic: combine:` (`memory_mb` 1024, `timeout_seconds` 60, `max_input_rows` 5,000,000); a request that exceeds one fails with a resource-exhausted error. Only queries are served: ad hoc tables, bindings and value maps are not.

#### Publishing a model

A model can also be published to a running server (plan 19, J13), by an administrator or a paired Build server (a publishing client): `PUT /api/v1/semantic-models/<name>` with `{"catalog": "<the catalog's yaml>", "sources": {"<catalog source>": "<data source>"}, "ssas": {"<catalog source>": "secret:..."}, "expected_version": <n>}` (`expected_version` is absent to create). Nothing is written unless the catalog loads and has the name of the call, every source maps to a data source of this server that answers a test query and can read every table the source's facts and joins name (an empty `SELECT ... WHERE 1 = 0` on each), and a publishing client's sources are data sources that client made (a model never reaches data its publisher could not) and its Analysis Services connection is a secret reference. The version is a published definition of the group (plan 20 O03), kept in the state: stored and made current in one step, so a failed publish leaves the serving version alone, and every version is kept. Publishing the catalog and mapping the group already serves makes no new version: the answer names the version served and says `"unchanged": true` (the audit says `unchanged`), so a retried publish is safe. The instance the call reached serves it at once; every other instance sharing the state serves it when it next looks (`definitionPollSeconds`, 5 by default), and one started later, even with the publisher stopped, serves it as it starts. Each instance loads a version before it serves it, so one it cannot load leaves the version it served (logged, and in the listing as `failed_version` and `failure` beside `served_version`). Models kept in `semantic-models/` of the data directory by an older version are read into the state once, and the folder is renamed `semantic-models.imported`. `POST .../<name>/rollback` (`{"to_version": n}`, or the version before) makes an earlier one current at once; `GET` lists, reads (with the versions) and `DELETE` stops it being served (its versions are kept). Each instance still needs the data sources a model maps to in its own configuration. A publishing client sees and changes only its own models. **On the command line** (plan 19, J15): `dbdatagateway semantic describe|query|explain|members <model> ... --address URL --user U` (the password is the `DBDATAKIT_GATEWAY_PASSWORD` environment variable) runs one statement on a model as that user and prints the answer, or with `--format json` one document (`model`, `columns`, `rows`, `row_count`, `truncated`, `notes`); `ui mcp` offers the same to an agent. **Saved queries** (plan 19, J26, J27): a model's catalog may carry `queries` (published with it), and people may keep saved queries of their own on a model in the Gateway's state database (the store, with history and sharing by user, group or everyone, exists; the API that lets a person save, edit and share one comes with the apps). Both are called by selecting from them on the model's source, `SELECT * FROM name(args)`, and run as the caller, so sharing a query shares no data; the model's own names win over a person's. A name set up in `server.yaml` is not replaced by a publish.

#### Other statements on a model, parameters, and the Models screen

* **Statements on the model source** (all as the caller, all read through the host's own rules): `{"kind":"describe"}` lists the dimensions, attributes, measures and saved queries; `{"kind":"members","attribute":"customers.region","search":"ea","limit":50}` answers the distinct values of an attribute (one `member` column, `qis.truncated` in the metadata) under the caller's own access; `EXPLAIN <semantic SQL>`, or a request of `"kind":"explain"`, answers one row per scan (`source`, `engine`, `text`) and sends nothing to a source. There is no custom Flight action.
* **Parameters** (plan 19, J25): a value in a filter, or a number, a member of a fixed set or a boolean argument of a measure or transform, may be a parameter, one value or a list: `{"param": "regions"}` with `parameters` beside the request, or `?`, `$n`, `$name`, `:name` and `IN :list` in semantic SQL. A client can **prepare** the statement on the model source: the answer to prepare has a field per parameter (typed, lists as Arrow lists, `qis.for` in the metadata naming the attribute the value is for) and the client binds by name or position. The rows and the request hash equal the literal form's, and every bound value is checked before any source is read (`param_missing`, `param_type_mismatch`, `param_unknown_member`, ...; the empty list is refused unless the definition says `empty: matches_none`).
* **Numbers in the answer** are Arrow `double` (a measure of type `number`), whole numbers `int64`, dates and times text. A decimal measure is not kept as a decimal.
* **Which tables are checked at publish:** the tables a catalog's sources name, not their columns; a mapped column that does not exist fails at the first query that reads it.
* **Models screen** (`/models` in the Gateway's screens, rail **Models**; shown by the console at `/servers/<server>/models`): the semantic models this group serves with their versions (who published each, when), what this instance serves against the group's current version, a version that did not load with the reason, serve an earlier version, and retire a model after a second click; and the entity models read-only. It calls `GET /api/v1/semantic-models`, `.../{name}/rollback` and `DELETE`, so a viewer sees the Gateway's refusal in words (these calls are an administrator's or a publisher's). It lists the catalog by name and version; it does not yet show its dimensions and measures, and there is no screen that runs a semantic query (plan 14a).
* **Nothing is served** of the engine's ad hoc features (registered tables, bindings, value maps, clarify sessions) and no run record is kept: the Gateway answers governed queries only, and keeps a saved query's version, never a query's run.

### Compression

`Gateway:Compression` (global) and `DataSources:<name>:Compression` (override):

| Mode | Codec | Where | Notes |
|---|---|---|---|
| `None` | | | |
| `Grpc` | `gzip` | **gRPC compression**: the transport compresses each whole message | Any gRPC client that advertises gzip; weaker ratio |
| `Arrow` | `zstd` / `lz4` | **Arrow data compression**: each column buffer inside the Arrow stream is compressed | Best ratio on columnar data; the client's Arrow reader must support the codec |

Clients can override per call with the `x-compression` header (`none`, `gzip`, `zstd`, `lz4`) unless
`AllowClientOverride` is false. Standard Arrow clients (C++/Python/Java/Go) read Arrow data compression natively (it is part of the Arrow format, so no gRPC negotiation is involved).
The stock .NET Flight client does not, which is why this repo ships `DbDataGateway.Client.GatewayFlightClient`.

> Arrow data compression relies on setting writer/reader options that `Apache.Arrow.Flight` 23.0.0 only exposes
> through non-public members. The gateway verifies they exist at startup and fails fast rather than silently sending
> uncompressed data; tests guard it. If that ever breaks on a package upgrade, use `Grpc` mode until it is fixed.

### Secrets

Configuration values that are secrets can be references instead of literals (shared `DbDataKit.Secrets` library):

| Reference | Resolves to |
|---|---|
| `env:NAME` | the environment variable `NAME` |
| `file:C:\secrets\erp.txt` | the file's contents, trimmed (Docker/Kubernetes secrets, systemd `LoadCredential`) |
| `secret:dbdata:gateway:datasource:erp:password` | the **encrypted secret store** of this installation, `<DataDirectory>/secrets/secrets.yaml` (set with `DbDataGateway secret set`, below); a name the store does not have falls through to the environment variable `DBDATAKIT_SECRET_DBDATA_PROXY_DATASOURCE_ERP_PASSWORD`, then, when `$CREDENTIALS_DIRECTORY` is set, to the file `dbdata_gateway_datasource_erp_password` in it |
| `dpapi:<base64>` | an inline DPAPI blob (Windows, machine scope), for one field where a store is not wanted. Create one with `dbdatagateway secret protect` (or `protect-secret`) |

#### The secret store

`secret:` names live in an encrypted file, `<DataDirectory>/secrets/secrets.yaml`: every value AES-256-GCM, with the secret's name as associated data (a value copied to another name does not decrypt),
outside any git-tracked configuration. The key is separate from the file:

* **Windows**: protected by DPAPI (machine scope, product entropy) in `<DataDirectory>\secrets\keys`, the directory restricted to the service account, SYSTEM and Administrators.
* **Linux and macOS**: a file in `<DataDirectory>/secrets/keys` that only its owner can read (mode 0600 in a 0700 directory, owned by the user the gateway runs as). **The gateway refuses to start if that file or directory can be reached by anyone else** (the way ssh refuses a private key), and says which `chmod` fixes it.
* **From outside**: `Gateway:Secrets:KeyFile` (or the environment variable `DBDATAKIT_SECRETS_KEY_FILE`) names a key the platform provides: a Docker or Kubernetes secret, a systemd credential; 32 raw bytes, or base64 or hex text. Images never contain a key.

The first start makes the key. **Back up the key separately from `secrets.yaml`: either one alone is useless.** A DPAPI key cannot be copied to another machine; to move a server, `secret export` and `secret import`.

```
DbDataGateway secret set dbdata:gateway:datasource:erp:password    # the value from a hidden prompt, or piped on stdin; never the command line
DbDataGateway secret list [prefix]                               # names, when each was written, the key it uses; never values
DbDataGateway secret check                                       # every secret reference in the settings resolves? (exit 1 if not)
DbDataGateway secret delete <name>
DbDataGateway secret rotate-key                                  # a new key; every value encrypted again under it; the old key deleted
DbDataGateway secret export backup.yaml                          # to move a server: encrypted under a passphrase (PBKDF2-SHA256, 600,000 rounds)
DbDataGateway secret import backup.yaml [--overwrite]
DbDataGateway secret protect                                     # Windows: an inline dpapi: value for one field
```

No command prints a secret's value. The old per-secret DPAPI files (`secrets\dbdata~gateway~...secret`) are no longer read: set the values again.

Where they work: a data source `ConnectionString` (the whole string, or just the value of a password key:
`Server=SQL01;User Id=gateway_reader;Password=env:ERP_DB_PASSWORD`), `Gateway:Tls:CertificatePassword` and
`Gateway:Auth:Jwt:SigningKey`. References are resolved each time the setting is used, from the current settings, so a rotated
secret (and a reloaded configuration file) takes effect without keeping old values around. A reference that cannot be
resolved fails that data source with a message naming the reference (never a value) and is logged.

Literals still work, but the gateway logs a one-time warning naming the config key (never the value), e.g.
`Config key Gateway:DataSources:erp:ConnectionString (Password) holds a literal secret`. Set
`"RequireReferencedSecrets": true` to turn the warning into a startup failure (and to refuse a literal that appears after a reload).

Every resolved secret is registered with a redactor that scrubs all log output (messages and exception text) and the
text of errors returned to clients; `Password=...`-style fragments are scrubbed even when the value is unknown.

### Authentication

Everything arrives as a Flight call; the gateway denies any call without valid credentials (except the handshake).

1. **The identity home's people** (plan 13a P13; plan 16, "Sign-in decisions" item 2). Gateway has **no users of its own**: a client signs in as a person or service account of the identity home, one of three ways. Failed logins are throttled per user and client address.
   * **A home user name with a personal access token as the password**, in the standard Flight handshake (Basic credentials in, opaque bearer token out). The user name is the person's address, a service account's name or the home's user id; the token is
     `ddk_...`, made by the person at the identity home, or for a service account with `dbdatagateway identity token-create --account <name> --name <label>`. A password that has the shape of a token and is not the right one for that user is refused
     for good (it never falls through to pass-through); a password that is no token is the database's, for pass-through.
   * **A home token** as `authorization: Bearer <token>` on every call, with no handshake: a personal access token, or an access token the home issued (checked against the home's signing keys when this instance holds their key, as for the `store` module).
     A revoked token stops working at once.
   * **Windows, resolved by UPN** (`Auth: { windows: true }`, off by default; Windows only for the operating system's check): the user name is the person's UPN (`ada@corp.example`), the password is checked by the operating system, and the account is the
     home user who holds that address as a verified one (an address an administrator entered at the home). A UPN no home user holds is left to pass-through; `DOMAIN\user` and bare names are not resolved. No database login of its own is needed.

   **Which data sources a home user may use is decided by home groups named in the data source files** (owner decision of 2026-11-07; for a data source a client publishes, by the definition's `grants` section, which is the same list under another key: `grants.groups` and `grants.everyone` are `extensions.gateway.groups` and `everyone`, and a file names one place or the other, never both): `extensions.gateway.groups` lists the home groups whose members may use the data source, and the home's groups of the person
   decide (names match ignoring case). `extensions.gateway.everyone: true` opens a data source to every user of the home, deliberately. **A data source that names no group and is not open to everyone is used by no home user** (the safe default; the
   configuration check warns about it, and a refusal says how to open it); a pass-through data source needs neither, the database decides there. Both at once is an error. `extensions.gateway.default: true` (at most one data source) is the one a person gets when
   their client names none, if they may use it; it opens nothing by itself (it replaces the `DefaultDataSource` of the retired users). Groups are made and filled at the home (the console's people screens, or `POST /api/v1/identity/groups` and `.../members` of its admin API).
   A session keeps the groups the person had when they signed in (at most `SessionMaxHours`), and the groups of the files are read again at every use. The log, the session list and the running-query list name the home user (the name the home shows, e.g. `Query by Ada Lovelace on erp`), never the token.

   ```yaml
   # connections/erp.yaml
   extensions:
     gateway:
       groups: [finance-reporting, auditors]   # members of these home groups may use erp
       default: true
   ```

   **A configuration that still lists Gateway users** (`users:` in `gateway/users.yaml` or `server.yaml`, `Gateway:Users` anywhere, a Windows `AuthType` entry) is **refused** with exit code 2 and a message naming the file and line (or the environment variables or command line) and the key: a clean break. Remove the users,
   make the people home users, and name their groups in the data source files. `secret hash` is gone with them.
2. (retired: Gateway's own Windows accounts. Windows is now the third way above.)
3. **Pass-through database credentials** (`"PassThrough": true` on a data source): the username/password the
   client authenticates with are injected into that connection and the database is the authority, so a wrong
   password fails at query time. A home token or a home user name with its token always wins over pass-through. A rejected database
   login (failure kind `Login`) is answered `Unauthenticated` and counts in the login throttle for that user and client
   address like a wrong password at the handshake does; once locked out (`Auth:MaxFailedLogins` within `Auth:LockoutMinutes`)
   the user is refused before any database connection is made, and a pass-through handshake does not reset the count.
4. **JWT bearer / OIDC** (`Gateway:Auth:Jwt`, e.g. Microsoft Entra ID): clients send `authorization: Bearer <jwt>` on
   every call. Validated against the authority's keys (or a shared HMAC key); `Mappings` turn claims (`groups`,
   `roles`, `scp`, ...) into data-source access. Tokens matching no mapping are refused.

Always run with TLS (`SelfSigned`, `File` or `Store`) outside local testing: credentials are sent as Basic auth.

### 32-bit ODBC

A 64-bit process cannot load 32-bit drivers, so `Bitness: "x86"` ODBC sources are executed by
`dbdatagateway-worker.exe` (published `win-x86`, in `worker\`). The gateway starts it on first use, talks to it over a
loopback Flight connection guarded by a random per-launch secret, restarts it if it dies and stops it with the
service; the worker also exits if the gateway disappears. If you publish the whole gateway as `win-x86`
(`./build/publish.ps1 -Runtime win-x86`) the sources run in-process and no worker is needed.

## Security

What the gateway enforces and where the responsibility stays with the database (details per feature are in the sections above).

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
* **TLS to SQL Server and PostgreSQL**: encrypt and verify the certificate unless the connection string says otherwise explicitly (see Data sources). Each source that
  opts out (`TrustServerCertificate=True`, `Encrypt=False`, `SSL Mode=Require` or `Disable`) is named in one startup warning; the setting keeps working.
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

  Messages the gateway writes itself (a `?`-marker count mismatch, an ODBC column type to `CAST`, the classifier's refusal) are passed on. The driver's own text
  (scrubbed by the redactor) goes to the server log; `Gateway:LogDriverErrors: false` logs only kind and code. 32-bit worker failures travel the same way.
* **Limits (off by default)**: `MaxConcurrentQueries` per data source and `MaxConcurrentQueriesPerUser` cap the queries running at once (a streaming result counts until it
  is read to the end or discarded); a request waits up to `AcquireTimeoutSeconds` (30) and is then refused with `ResourceExhausted`.
  Every command now has a timeout (`CommandTimeoutSeconds`, else 300 s).
* Not covered: row and byte limits, an audit log of failures per client beyond the log lines, and anything the database login allows.

## Clients

C# (`DbDataGateway.Client`, in `DbDataGateway.Core`):

```csharp
using var client = new GatewayFlightClient("https://gateway:8815");
await client.AuthenticateAsync("me", "ddk_...");   // a home user name and its personal access token
var result = await client.QueryAsync("SELECT * FROM sales",
    new QueryOptions { DataSource = "erp", Compression = "zstd" });
await foreach (var batch in result) { /* Apache.Arrow.RecordBatch */ }
```

Other languages use their normal Flight SQL client. With ADBC (Python shown; the same options exist for the other
languages):

```python
import adbc_driver_flightsql.dbapi as fsql
from adbc_driver_flightsql import DatabaseOptions

conn = fsql.connect("grpc+tls://gateway:8815", db_kwargs={
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
driver to prove queries really run in a 32-bit process. Set `DBGATEWAY_TEST_SQLSERVER` to point the SQL Server tests at
another instance (Windows authentication).

Layout: the shared `DbDataKit.*` packages (ODBC catalog, Connections providers, ...; project references into the hub while the adoption pass is offline), `src/DbDataGateway.Core` (Arrow conversion, ADO.NET executor, client, worker protocol),
`src/DbDataGateway.Server` (Kestrel host, Flight SQL server, auth, TLS, service/CLI),
`src/DbDataGateway.Worker` (32-bit ODBC worker), `tests/DbDataGateway.Tests`.

## License

MIT; see [LICENSE](LICENSE).
