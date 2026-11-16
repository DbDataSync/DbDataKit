# Configuration

[DbDataSync](../README.md) · [Install](install.md) · **Configuration** · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [Building from source](development.md)

What configures `dbdatasync` and the engine a Runner runs. The Sync server and its own settings (`server.yaml`, the `DbDataSync:*` keys, its
certificate, its state database, its service and its container image) were deleted (plan 33 SC23): the replications are run by a Runner, which
keeps their runs, positions and results, and they are authored in the Workspace or as the files of a configuration repository.

## The command line

`dbdatasync` works on a configuration repository: a folder whose `config/` holds the connections, the replications and their table mappings,
the scripts and the libraries (see [Getting started](getting-started.md)). Parsing is a simple `--flag value` scan (`CliOptions.Read`/`Has`):
no positional magic, no `=` syntax. Run `dbdatasync` with no arguments for the commands, and `--format json` on any of them for one JSON document.

### Repository root resolution

Every command that takes `--repo` resolves the repository root the same way (`DbDataSyncRoot.Resolve`):

1. An explicit `--repo <path>` wins outright (what the Workspace passes).
2. Otherwise, walk upward from the current directory looking for `server.yaml` (the file a repository of the deleted server kept at its root).
3. Otherwise, `DBDATAKIT_DATA_SYNC`, or the folder `sync` under `DBDATAKIT_DATA` (the install's data root). The old `DbDataSync__App__RepoRoot`
   is not read: a command that finds it set stops and names the new variable (there is no alias).
4. Otherwise, the machine-wide default folder of the platform (`CliOptions.DefaultRoot`): `%ProgramData%\DbDataKit\sync` on Windows,
   `/Library/Application Support/DbDataKit/sync` on macOS, `/var/db/dbdatakit/sync` on FreeBSD, `/var/lib/dbdatakit/sync` elsewhere.

## DbDataSync.TaskRunner process (Runner task only)

Started by a Runner as the task of a published replication set (`--runner`, plan 28c E03): the Runner is Sync's only scheduler, and
the program refuses to start without `--runner` (exit code 2). The flags of the old worker (`--repo-root`, `--state-db`, `--state-endpoint`
and the rest) are gone with it.

Within a drain the engine runs **two independent lanes**, each with its own bounded queue and its own
pool of consumers:

- `--degree-of-parallelism` sizes the **change-processing lane** — incremental (`Primary`) passes.
- `--bulk-load-parallelism` sizes the **bulk load lane** — bulk load segments and verifications.

A long-running reload on the bulk load lane can no longer take a slot an incremental pass needs. Both
come from `changeProcessing.degreeOfParallelism` and `changeProcessing.bulkLoadDegreeOfParallelism` in `task.yaml`; `4` is the
fallback for a replication whose configuration does not set them. Each number is table mappings processed at once within that lane;
a single mapping never occupies more than one slot in a lane regardless of the number, and the total
concurrency ceiling for the replication is the two added.

### The writer of `changeProcessing:` may be left out

`changeProcessing.writer` is optional. A file that leaves it out gets `ChangeApply` behind an incremental reader (`MsSqlChangeTracking`, `MsSqlCdc`, `Watermark`, `TriggerAudit`, `PgLogicalSlot`,
`OracleFlashback`) and `DeleteInsert` behind any other reader; the writer is named when the file is read, a table mapping that overrides the reader gets the default of its own reader, and saving the
replication from the Workspace writes the name into the file. A writer that is named is never changed. See [ChangeApply](replication-concepts.md#changeapply-the-change-writer-of-every-engine).

### The Bulk Load pipeline (`task.yaml` `bulkLoad:`)

A second pipeline beside `changeProcessing:`, used for an on-demand reload (a bulk load request of the
replication's drain on the Runner) and for an initial load:

```yaml
bulkLoad:
  reader:
    kind: BatchReload
  cache:      # optional — null inherits changeProcessing.cache
  writer:     # optional — null inherits changeProcessing.writer
```

- `reader` defaults to the `BatchReload` Kind every driver is expected to offer for a whole-table
  read, so a replication that writes nothing under `bulkLoad:` still gets a working pipeline.
- `cache` and `writer` default to unset (`null`) and, when unset, inherit the replication's resolved
  `changeProcessing.cache`/`changeProcessing.writer` — a bulk (re)load into the same target table
  naturally uses the same staging and write mechanism as change processing unless an operator wants
  something different (typically an upsert-only writer for a first load). When `changeProcessing.writer`
  is itself left out, a bulk load uses `DeleteInsert`, not the change writer.
- Same per-mapping override shape as `changeProcessing:` — a table mapping can set its own
  `bulkLoadReaderOverride`/`bulkLoadCacheOverride`/`bulkLoadWriterOverride`, each independent and each
  atomic (Kind and options together, never merged).
- The writer is warned about, not constrained, when it does not support reconciliation: an upsert-only
  writer is correct and cheaper for a first load into a table that was just created, since there is
  nothing yet to remove. Saving does not refuse it.
- Segmenting is **not** part of this — it stays on the table mapping's own `defaultSegmenting`, because
  how a table divides is a fact about the table, not about a pipeline.

**Breaking rename, no migration.** `Backfill` was renamed to `BulkLoad` everywhere — the
`RunKind`/`RunLane` enum members, the `BulkLoadBatches` table and `TaskRuns.BulkLoadBatchId` column,
and `changeProcessing.backfillDegreeOfParallelism` (now `changeProcessing.bulkLoadDegreeOfParallelism`,
see above). There is deliberately no schema migration and no old-key fallback: an existing state
database still has a table and column named `Backfill*`, which the renamed code no longer recognizes,
and an existing `task.yaml` carrying the old `backfillDegreeOfParallelism` key simply has that key
ignored by the deserializer. **Every existing state database, and any config using the old key, must
be recreated rather than upgraded.**

### Checks stored with a replication (`task.yaml` `checks:`)

A replication can carry the data checks of the datasets it writes, in its own `task.yaml`, so they travel with the replication when it is published and promoted. The section is the shared `check/1` list of `DbDataKit.Quality` (the kinds, their
parameters, `severity` and `on_error` are described in that library's README); the first one Sync is built for is **freshness**.

```yaml
schemaVersion: 2
name: sales
# ...
checks:
  # the orders table must not fall behind its source
  - id: orders_fresh
    kind: freshness
    dataset: dbo.Orders      # a target table of this replication, or a dataset's lineage identity (scheme://server/name)
    max_age: 6h              # no column: the age of the last successful load; with `column:` the newest value in it
    severity: block          # warn (the default) tells the owner; block stops what depends on the dataset
```

- **A replication's `schemaVersion` is 2; table-mapping and script-manifest files stay 1.** Format 2 of `task.yaml` only adds the optional `checks:` section. Sync reads a `task.yaml` of format 1 or 2, as it is (a format 1 file is not checked for the
  section: one edited by hand to hold it is read as written), and **every save writes the current format**: a file loaded as 1 and saved by Sync (the Workspace) says `schemaVersion: 2` afterwards, whatever it said before.
  A file of format 0 (or a value that is not a whole number) is refused naming the file and the line, and so is one newer than this Sync reads:
  "The file is format 3, which is newer than this version of Sync reads (format 2). Update Sync; it does not read part of a newer file."
  A file older than the range a kind reads is refused by name ("... no longer reads (current: N). Review the file against the current format, then set schemaVersion: N."); no kind has one today. A release that reads only format 1 refuses a `task.yaml`
  of format 2, and this one refuses a table mapping or script manifest of format 2 as newer. Nothing converts a file in the background and nothing reads half of one.
- **Only the replication has `checks`.** A table mapping or script manifest that has the key is refused as an unknown key. A connection file's own `checks` (the serving data source's) is not this section.
- **Strict, with the line.** A bad check (an unknown kind, a `range` with `min` above `max`, a freshness check with no `max_age`, a repeated id) is refused with the file, the line and column, and a suggestion for a near key or kind
  (`chekcs` is answered with "Did you mean `checks`?", `freshnes` with "Did you mean `freshness`?"). Codes `DBD-QLT-001` to `006`.
- **Not evaluated since the Sync server was deleted (plan 33 SC23).** The server evaluated them after a replication's runs; the engine on a Runner does not yet, so the section is kept and published with the replication and checked
  when it is read, and nothing evaluates it until the Runner does (open, plan 33).

## Connection files

A connection is `config/connections/<name>.yaml` in the configuration repository, a **`connection/1`** file: the format the Proxy and Build keep their connections in
too, so one reader checks all three. The current form is **`connection/2`**; a `connection/1` file is still read. The file name is the connection's `name`: a letter, then letters, digits, `-`, `_` and `.`, at most 64 characters.

```yaml
schema: connection/1
name: orders
engine: sqlserver            # sqlserver, postgres, mysql, oracle, duckdb, flightsql (with dialect: for a variant), or the id of an installed driver
address:                     # or connection_string: (an ODBC string, a JDBC URL, an Oracle EZConnect, ...), never both
  host: sql01
  port: 1433
  database: Orders
auth:                        # sql, integrated or none
  mode: sql
  user: sync
  password_secret: secret:dbdatakit:sync:connection:orders
timeouts: { connect_s: 20, command_s: 600 }   # unset: Sync's own defaults, 30 and 1800 seconds
extensions:
  sync:                      # what only Sync has; the keys are written like the shared ones, snake_case
    segment_order: Together
    properties: { applicationName: sync }
    test_query: select 1
```

`extensions.sync` also holds `run_user` and `run_password_secret` (a second login, see "Two logins: read and run" below), `scripts` and `hooks` (the bindings of a connection), `database` (for a connection with a `connection_string`; with an `address` the
database is `address.database`) and a `connect_timeout_seconds` or `command_timeout_seconds` the shared `timeouts` cannot say (`0` for unlimited, or more than a day).
The password is never in the file: `password_secret` is the reference `secret:dbdatakit:sync:connection:<name>` to the encrypted store (see "Secrets").
Unknown keys are errors with their position; `tls`, `read_login`, `write_login`, `connection_string_secret`, `access` and `managed_by` are refused because Sync
does not use them. Another product's `extensions.<product>` block is not read.

**The form of earlier releases is refused.** A file with `driverType`, `host` or `authMode` at the top and no `schema:` makes every command that reads
connections fail with a message naming each such file and this form (`dbdatasync package publish --to runner --check` lists them). Nothing is converted: rewrite the files by hand.

## Two logins: read and run

A connection has one login by default, and it does everything. It may have **two**, because the person who looks after a database may want a replication to use a login that can write where browsing and previews use one that can only read
(plan 28c, E20):

| Login | What uses it | Where it is kept |
| --- | --- | --- |
| **read** (`auth.user`, `auth.password_secret`) | browsing metadata and previews where they are made (the Workspace), a provisioning plan's reading | where the program that opens it keeps logins |
| **run** (`extensions.sync.run_user`, `extensions.sync.run_password_secret`) | what a replication does: reading the source and writing the target in a pass, and the steps a provisioning **apply** runs; the login a runner is given | the encrypted store, `dbdatakit:sync:connection:<name>:run` (on a runner, see below) |

Without a run login the read login serves both, exactly as before. `run_user` is optional (the read user when absent); `run_password_secret` is `secret:dbdatakit:sync:connection:<name>:run`, and a file that names another connection's secret is refused.

```yaml
auth: { mode: sql, user: sync_read, password_secret: secret:dbdatakit:sync:connection:orders }
extensions:
  sync:
    run_user: sync_run
    run_password_secret: secret:dbdatakit:sync:connection:orders:run
```

A file with a misspelled key (`run_pasword_secret`) is refused naming the file, the line and the right key; a file written before this setting loads as it did (the keys are optional).

### On a runner

A runner never has the read login. A replication set published to a runner declares a slot per connection; the administrator of the runner binds each slot to a connection of the runner's own (the Runner's Packages page, or `package publish --bind`), and the runner puts
that connection's login in the engine's environment under both `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_<NAME>` and `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_<NAME>_RUN` (the engine reads whichever its connection names; it opens with the run role only). The
value reaches the runner from the Runner's Packages page (a run login set there is kept in the group's state, plan 28c E20d; step 2 below is then not needed), or by hand. For each connection of the set:

1. Write `<runner directory>/connections/<name>.yaml`, a `connection/1` file of the same name as the Sync connection, whose login is the run login: `auth: {mode: sql, user: <run user>, password_secret: env:<NAME>_RUN_PASSWORD}` (or
   `secret:dbdatakit:sync:connection:<name>:run`, which the runner reads from its encrypted store when it was sent there, else from its environment as `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_<NAME>_RUN`, or from a file of that name in `CREDENTIALS_DIRECTORY`).
2. Put the password where that reference looks: in the environment of the runner's service (a systemd unit's `LoadCredential=` and `CREDENTIALS_DIRECTORY`, or an `EnvironmentFile=` readable by the service account only; on Windows the service's environment), never in the file and never on a
   command line. Restart the runner after setting an environment variable; a credentials file is read at each run.
3. Bind the slot to that connection (the Runner's Packages page) and check it: a run whose login is not found fails naming the slot, the connection and the reference, and never falls back to another login.

See the runner's README ("Logins of a Sync replication set") for the same steps from the runner's side.

## Secrets

A connection file names its password, never holds it: `password_secret` is a reference such as `secret:dbdatakit:sync:connection:<name>`
(its run login's `secret:dbdatakit:sync:connection:<name>:run`). The program that opens the connection resolves it: on a Runner its encrypted
store, then its environment, upper-cased with every character other than a letter or digit replaced by `_` and prefixed `DBDATAKIT_SECRET_`
(a connection named `orders-db` resolves to `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS_DB`), then a file named after the secret in the
directory `CREDENTIALS_DIRECTORY` names. A command of `dbdatasync` that opens a connection (`library validate`) reads the repository's
`secrets/` store and the environment the same way. The names from before the store (`dbdatasync:connection:...`) are not read.

## Next: Getting started

See [Getting started](getting-started.md) to set up your first replication.
