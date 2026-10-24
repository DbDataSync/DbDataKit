# Release notes: what has changed behaviour since the last release

Written as each slice of plan 13 lands, so the first suite release does not rely on anyone's memory. Each entry says what to do about it. Add
to it in the pull request that makes the change; a release moves the entries into its notes and empties this file.

## DbDataSync

- **Secrets are in a new store and are set again.** Connection passwords and the state database's password were in the operating system's
  credential store (through `ClrKernel.Core.Secrets`) under `dbdatasync:...` names. They are now in an encrypted file store, `secrets/` under
  the repository root, under `dbdata:sync:...` names, and nothing is imported. Set each connection's password again (save the connection
  with its password, or `dbdatasync secret set dbdata:sync:connection:<name> <value>`) and the state database's
  (`dbdata:sync:config:stateConnectionString`). `secrets/` is added to the repository's `.gitignore`; back it up with its key. Names are
  lower-cased, so two connections whose names differ only in case share a password. The environment fallback is now
  `DBDATA_SECRET_DBDATA_SYNC_CONNECTION_<NAME>` (was `DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_<NAME>`); `DBDATASYNC_SECRETS_FILE` is gone. (A04, A05)
- **Every config file needs a `schemaVersion`, and unknown keys are an error.** A file without one, or with an older one, is refused with a
  message naming the file. The retired per-stage `parallelism` keys under `reader` and `writer` in a replication's `task.yaml` are refused,
  naming each key: delete them. (A02, A03)
- **Column types for some engines changed.** Oracle `uuid` stays `varchar2(36)`. Oracle `NUMBER(n,0)` up to 18 digits is read as an integer
  type. MySQL's time-zone timestamp is created as `timestamp(s)` (it ends in 2038). Postgres creates `timestamp(6) with time zone`.
  DuckDB's unsigned integers are read as the next wider signed type. (A07)
- **The command line has new names.** `dbdatasync serve` is `dbdatasync server run`, `setup` is `server setup`, `config cert|secret|library|driver ...` are `cert`, `secret`, `library` and `driver` (`config check|get|set`
  stay), and `health` is `health check`. The old forms exit 2 and print the new one. The Windows service, the systemd unit and the Docker image run `server run`, so **run `service install` again** on an
  existing installation (a service still registered with `serve` cannot start), and update container health checks to `health check`. `invite`, `update`, `launcher` and `tool` are unchanged for now. (C06)
- **SQL Server and PostgreSQL connections verify the server certificate by default.** The built-in `TrustServerCertificate=true` is gone: SQL Server connections are encrypted (`Encrypt=Mandatory`) and the
  certificate is checked, and PostgreSQL uses `SSL Mode=VerifyFull` (Npgsql's default was `Prefer`, which did not verify). A server with a self-signed certificate needs `TrustServerCertificate=True` in the
  connection string or in the connection's properties; a PostgreSQL server without TLS needs `SSL Mode=Disable` (or `Require`, which encrypts without verifying). `Encrypt`, `SSL Mode` and `Root Certificate` in a
  connection string or in properties are still honoured. A connect timeout in properties still wins. Any other property the shared policy reserves (credentials, host and database keys, `Application Intent`,
  `Multi Subnet Failover`, `Options`, `Search Path`, command timeouts) is now refused with a message naming it; put it in the connection string instead. (A08)
- **Sync commands accept `--format json`.** `server`, `service`, `cert`, `secret`, `library`, `driver`, `config` and `health` print one JSON document (`schema`, `command`, `exit_code`, `ok`, `data`, `messages`, `errors`); an unknown command now exits 2 with "'x' is not a command" (it was 1 with "Unknown command 'x'"). (C07)
- **Database errors show a fixed message and the engine's code, not the driver's text.** A failed connection test, run, preview, provisioning step or reconcile now reports for example
  `The login was rejected. [sqlserver 18456]` or `The server could not be reached. [postgres 08006]`; a run's stored error detail is empty for a driver failure. The driver's own text, with
  secrets scrubbed, is written to the server log. Set `Diagnostics:DriverDetails` to `true` (environment `Diagnostics__DriverDetails=true`) to append it to the response and keep it in the run
  detail again. Sync's own validation messages are unchanged. (A10)
- **MySQL, MariaDB and Oracle connections are verified and encrypted by default.** Like SQL Server and PostgreSQL before them, they are built by the shared composers. MySQL and MariaDB now use `SSL Mode=VerifyFull`
  unless the connection string or `properties` name another `SSL Mode` (`None`, `Preferred`, `Required`, `VerifyCA`); `SSL CA` is the root certificate. An Oracle connection given as a host, port and service name is written as
  `tcps://host:port/service`; put `Encrypt` = `false` in `properties` for a listener that has no TLS. A connection string is still the base and its own data source (an alias, a descriptor, a wallet) is not rewritten.
  Credential, host, port, database and timeout keys in `properties` are refused with a message naming the key (Oracle's `Data Source` included). Failures from these engines now show the shared classified message. (A09)
- **`dbdatasync ui mcp` serves the commands that read to an agent** (Model Context Protocol on standard input and output). Each command that only reads is a tool that returns its `--format json` document; `--allow-writes` also offers the ones that change files. Secrets cannot be set and installed libraries cannot be loaded through it. (C08)
- **MySQL, MariaDB and Oracle table columns are read by the shared catalog readers.** MySQL and MariaDB column types now carry the full engine type (`bigint unsigned`, and MariaDB's `bigint(20) unsigned`; `text` instead of `text(65535)`), so an unsigned column is no longer mistaken for a signed one. Oracle reports the same types as before. (A12)

## DbDataBuild

- **Login variables are `DBDATA_BUILD_<CONNECTION>_<READ|WRITE>`.** The old `DBDATABUILD_*` names are not read, and the
  `secret:dbdata:build:connection:...` fallback is gone. A connection name with characters other than letters and digits has underscores in
  its variable (`my-dw` is `DBDATA_BUILD_MY_DW_READ`). A connection with no variable set is DDB-501, naming the variable. (B02)

## DbDataProxy

- **The executables are lower case.** `DbDataProxy.exe` is `dbdataproxy.exe` and the 32-bit worker `DbDataProxy.Worker.exe` is `dbdataproxy-worker.exe` (`worker\dbdataproxy-worker.exe`); the Docker image runs `dbdataproxy.dll`. The
  Windows service is registered as `dbdataproxy` by default (the service control manager ignores case, so `service install` updates an existing registration in place, and its virtual account is the same one).
  A script, a shortcut, an `ExecStart` or a `worker` setting that names the old file must be changed. The data directory is still `DbDataProxy`. (C03)
- **The command line is `dbdataproxy <noun> <verb>`, and there is no default command.** Running `dbdataproxy` with nothing prints the help and exits 2; `dbdataproxy server run` runs the proxy. The Windows service,
  the generated systemd unit and the Docker image now run `server run` (re-run `service install` to update an existing service, which adds the argument to its command line). Renamed: `hash-password` is
  `secret hash` (the password comes from a prompt or standard input, no longer an argument), `protect-secret` is `secret protect`, `query` is `query run`, `--healthcheck [url]` is `health check [--url url]`,
  and `install`/`uninstall` are `service install`/`service uninstall` (the MSI and `install.ps1` call the new forms). The old forms are not commands. Anything that starts the proxy with options only
  (`dbdataproxy.exe --Proxy:Listen:Port=9000`) must say `server run`. (C04)
- **`dbdataproxy ui mcp` serves the commands that read to an agent**, the same way. Secrets and `query run` (which returns rows) are never offered. (C08)
