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
