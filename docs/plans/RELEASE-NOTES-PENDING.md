# Release notes: what has changed behaviour since the last release

Written as each slice of plan 13 lands, so the first suite release does not rely on anyone's memory. Each entry says what to do about it. Add
to it in the pull request that makes the change; a release moves the entries into its notes and empties this file.

## DbDataSync

- **Secrets are in a new store and are set again.** Connection passwords and the state database's password were in the operating system's
  credential store (through `ClrKernel.Core.Secrets`) under `dbdatasync:...` names. They are now in an encrypted file store, `secrets/` under
  the repository root, under `dbdata:sync:...` names, and nothing is imported. Set each connection's password again (save the connection
  with its password, or `dbdatasync config secret set dbdata:sync:connection:<name> <value>`) and the state database's
  (`dbdata:sync:config:stateConnectionString`). `secrets/` is added to the repository's `.gitignore`; back it up with its key. Names are
  lower-cased, so two connections whose names differ only in case share a password. The environment fallback is now
  `DBDATA_SECRET_DBDATA_SYNC_CONNECTION_<NAME>` (was `DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_<NAME>`); `DBDATASYNC_SECRETS_FILE` is gone. (A04, A05)
- **Every config file needs a `schemaVersion`, and unknown keys are an error.** A file without one, or with an older one, is refused with a
  message naming the file. The retired per-stage `parallelism` keys under `reader` and `writer` in a replication's `task.yaml` are refused,
  naming each key: delete them. (A02, A03)
- **Column types for some engines changed.** Oracle `uuid` stays `varchar2(36)`. Oracle `NUMBER(n,0)` up to 18 digits is read as an integer
  type. MySQL's time-zone timestamp is created as `timestamp(s)` (it ends in 2038). Postgres creates `timestamp(6) with time zone`.
  DuckDB's unsigned integers are read as the next wider signed type. (A07)

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

