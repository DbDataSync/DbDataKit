# The state database

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · [Getting started](getting-started.md) · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · **State database** · [Building from source](development.md)

DbDataSync keeps two entirely separate kinds of data: your **config repository** (replications, table
mappings, connections — a git-tracked directory, see [Configuration](configuration.md)) and its own
**state database** — run history, the work queue, watermarks, users and sessions. This page is about
the second one. Nothing here is a replication's source or target; it's DbDataSync's own bookkeeping
about what it has done and is about to do.

## What it stores

The state is built from **modules**, each with its own version and its own list of migrations, created (and kept current: see "Migrations" below) on first open regardless of which engine backs it. Sync's own
module is `sync_state`; the others are the suite's shared ones, the same tables the other products keep.
`dbdatasync state show` lists them and the version each is at.

| Module | Table | What it's for |
| --- | --- | --- |
| `sync_state` | `run_detail` | What a run has beyond a run of any service: its kind (a mapping's `Primary` pass, or one `BulkLoad` segment), mapping, status, row counts, timing, the position it started from and left off at, and the error detail. One row for each run, beside its row in `runs.run` |
| | `change_check` | The polling gate's audit trail — one row per source group per scheduler tick, also backing reader-lag reporting |
| | `verification_result` | Indexes a verification run's parquet result file (the comparison itself isn't stored here) |
| | `bulk_load_batch` | Groups a bulk load's segment runs so progress can be shown as one whole |
| `runs` | `run`, `run_log` | The suite's one run model (the replication is the job) and the log lines of each run |
| `scheduling` | `work_item`, `run_lock`, `job_pause` | The durable, cross-process work queue (the API enqueues, `TaskRunner` workers claim and drain), the lock a unit of work holds while it runs, and a paused replication with who paused it and the note |
| `notifications` | `notification`, `reader_position` | The global, append-only notification feed, and the highest id each user has acknowledged |
| `audit` | `entry` | Who paused or resumed a replication or held a table mapping, and when |
| `runner_progress` | `checkpoint_head`, `checkpoint_part`, `checkpoint_history` | The read position of each table mapping, with its `ReadIntent`/`ReadHold` (see [Replication concepts](replication-concepts.md)): a declared checkpoint called `watermark`, kept per mapping and source table, whose every change is in the history |

## An older state database

A state database made by an earlier release (one schema version for the whole database: the tables `TaskRuns`, `ChangeWatermarks`, `WorkQueue` and so on) is **not converted**. DbDataSync refuses to start on it with a
message that names the file (or the database on the server) and the ways forward, and it does not change it:

1. **Replay from the earliest position the source still has.** Start DbDataSync on a new state database (a new `StateDbPath`, or a new database on the server) and set **Default read intent** to `ChangesFromEarliest`
   on the replications. Each mapping then reads every change its source still keeps (the log or the change-tracking retention) without a full load, and a table whose position is no longer available is reloaded alone.
2. **Set each mapping's position by hand** on a new state database, with the position the old release showed.
3. **Stay on the previous release**, which still reads the old database as it is.
4. **Start with a new state database** and let every mapping begin from its configured initial load.

In every case the old database is left where it is, so going back is always possible.

## Choosing an engine

SQLite by default — a file at `StateDbPath` (`<RepoRoot>/state.db`), nothing to configure. Set
`StateEngine` to `MsSql` or `Postgres` instead to run it on infrastructure you already operate and back
up, with `StateConnectionString` saying how to reach it (never with a password in it — see
[Configuration](configuration.md#secrets)). Full flag reference, including the state store's own
retention keys, is in [Configuration](configuration.md#state-store-engine-stateenginestateconnectionstring).

Two things worth being explicit about here, beyond what Configuration already covers:

- **An unrecognized `StateEngine` value refuses to start** — `Unknown state engine '<value>'. Built-in
  engines are 'Sqlite', 'MsSql' and 'Postgres'...` — rather than silently landing on SQLite.
- **`MsSql`/`Postgres` resolve their connection the same way a driver does** — through a
  [library](drivers-and-libraries.md), not a package this build carries. `DbDataSync.State`'s own project
  file references no `Microsoft.Data.SqlClient`/`Npgsql` package at all; it resolves
  `microsoft-data-sqlclient`/`npgsql` through `LibraryRegistry` at connect time, exactly like the
  built-in `MsSql`/`Postgres` replication drivers do. Run
  `dbdatasync library install microsoft-data-sqlclient` (or `npgsql`) once; a `StateEngine` set
  to either without its library installed fails at startup naming that exact command. See
  [Drivers and libraries](drivers-and-libraries.md) for the general mechanism.

**No cross-engine migration exists.** Pointing an existing deployment at a different `StateEngine`
starts an empty store — it does not move anything. Choose once, at stand-up.

## Migrations

Schema changes are numbered lists of DDL scripts, one list for each module, applied incrementally and recorded in a registry table (`dbdatakit_modules`: the module, its scope and how many migrations are applied).
**Migrations run when the database is opened** — there's nothing to run by hand after an upgrade: stop the process, update it, start it again. A database whose module is newer than the release refuses to start,
naming the module.

All three engines are meant to behave identically. The DDL is written once, as one script per migration with small engine-specific tokens (how an auto-assigned key is declared, what text, time and UUID columns are
called) substituted per engine; anything that differs *structurally* — upsert syntax, row-limiting, identity handling — lives in a separate per-engine implementation rather than a token, on the same principle
[Drivers and libraries](drivers-and-libraries.md#descriptor-drivers) documents for a descriptor's own dialect.

## Retention

| key | default | prunes |
| --- | --- | --- |
| `DbDataSync:State:Retention:RunDays` | `30` | runs older than this, with their log lines and their detail (`0` = keep forever) |
| `DbDataSync:State:Retention:RunMaxPerMapping` | `1000` | most recent N finished runs kept, per table mapping (`0` = no cap) |
| `DbDataSync:State:Retention:PruningIntervalMinutes` | `60` | how often the sweep runs |
| `DbDataSync:State:Retention:ChangeCheckDays` | `7` | how long `change_check` rows live — this table writes constantly (one row per source group per scheduler tick) regardless of replication activity, so it has its own, much shorter window |

One sweep, run by the API process itself (never `TaskRunner` — see "Single writer" below), prunes
runs (with their log lines and their `run_detail`), `change_check`, and notifications together — a notification can't
outlive the run it's about — and then runs the suite's shared retention job over what the modules keep as history
(the history of a position is kept 30 days, a settled dead letter or an expired key goes by its own rule). The audit of
who paused what has no age limit. A position itself (the current state of a mapping), a pause, and the queue are never aged out. It runs once immediately at
startup, then on the configured interval, so an API that was down for a while doesn't wait an hour to catch up.
**A run that hasn't finished is never pruned by the run caps, regardless of age.**

One known gap: a verification run's `run_detail` is pruned on the same schedule as everything else,
but its parquet result file and `verification_result` index entry are not — nothing currently cleans
those up.

## One process owns the store

**`DbDataSync.TaskRunner` never opens the state database directly, even for `Sqlite`.** It reaches the
API's own state over a loopback-only HTTP endpoint (`127.0.0.1` bound, defended again by a per-process
token passed through the environment, never the command line). This is deliberate, not a missing
optimization: with several workers spawned per install, having every one of them write the same SQLite
file directly was a real design that needed retries and lock timeouts to survive; the actual fix was
making it structurally impossible rather than tuned to be rare. If the API becomes unreachable, a
worker retries for a grace period, then spills its outcomes to a local JSON-Lines journal and exits
cleanly — the API replays that journal (idempotently) the next time it starts.

This single-writer design is why there's no supported way to run two API instances against one shared
`MsSql`/`Postgres`-backed store today — pointing a server engine at "infrastructure you already operate
and back up" is about deployment preference, not about enabling more than one API process to share it.

## Operating it

There's no dedicated backup/restore/migrate command. For the SQLite default, the state database is
just a file beside your config repository — [Install](install.md)'s container section notes that one
volume covering both is a complete backup of everything that isn't the image itself. For a server-engine
deployment, back it up the way you already back up that database; DbDataSync doesn't do this for you.

What does exist:

- **`dbdatasync state show`** lists the modules the state is built from, the version this release has of each and the version the database is at, and where the database is.
- **`dbdatasync config check`** opens the state database as part of its readiness checks (which also
  runs the full migration set) and reports `State store: OK — <engine> — schema current`, or a `FAIL`
  naming the fix — a writable-directory check for SQLite, or a connection-string/secret check for
  `MsSql`/`Postgres`.

## Next: Building from source

If you're modifying DbDataSync itself rather than configuring a deployment of it, see
[Building from source](development.md).
