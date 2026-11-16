# DbDataSync

[![NuGet](https://img.shields.io/nuget/v/DbDataSync.svg?label=NuGet)](https://www.nuget.org/packages/DbDataSync)

**DbDataSync** · [Install](docs/install.md) · [Configuration](docs/configuration.md) · [Getting started](docs/getting-started.md) · [Replication concepts](docs/replication-concepts.md) · [Drivers and libraries](docs/drivers-and-libraries.md) · [Building from source](docs/development.md)

DbDataSync is a cross-database replication tool. You define a replication in a sync project of the Workspace (in the
[dbdatakit console](https://github.com/DbDataSync/DbDataKit/blob/main/DbDataKit/README.md)) or as the files of a configuration repository — the source table,
the target table, column mappings, a schedule, and how changes are processed — publish it to a **Runner**, and the Runner keeps the target in sync with
DbDataSync's engine. It does a full initial load, then applies incremental changes on a schedule or on demand. The Sync server was deleted (plan 33 SC23):
`dbdatasync` is a command line on a configuration repository, and the engine (`DbDataSync.TaskRunner`, the package `dbdatasync-runtime`) is what a Runner runs.

A replication's source and target can be different engines — a SQL Server source can feed a
PostgreSQL target, for example. Support varies by engine:

<!-- engine-support:begin sync -->
| Engine | Sync source | Sync target |
| --- | --- | --- |
| Arrow Flight SQL | yes | no |
| DuckDB | yes | no |
| Microsoft SQL Server | yes | yes |
| MySQL and MariaDB | yes | yes |
| Oracle Database | yes | yes |
| PostgreSQL | yes | yes |

**yes** built and run against a real engine, **unverified** built and never run against one, **planned** not built. Generated from the engine registry; the notes behind each cell are in [engine-support.md](../docs/engine-support.md).
<!-- engine-support:end -->

| Feature                       | SQL Server                     | PostgreSQL                | MySQL / MariaDB           | Oracle                            | DuckDB           |
| ------------------------------ | ------------------------------- | -------------------------- | --------------------------- | ----------------------------------- | ------------------ |
| Source (read from)            | Yes                             | Yes                        | Yes                         | Yes                                  | Yes (query only) |
| Target (write to)             | Yes                             | Yes                        | Yes                         | Yes                                  | No               |
| Incremental sync              | Native (Change Tracking / CDC)  | Native (logical decoding)  | Watermark column             | Native (Flashback Version Query)    | N/A              |
| Change writer for an incremental pass (inserts, updates, deletes) | `ChangeApply` (default) or `MsSqlMerge` | `ChangeApply` (default)    | `ChangeApply` (default)      | `ChangeApply` (default)             | N/A              |
| Backfill / batch reload       | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |
| Bulk staging                  | Native (SqlBulkCopy)            | Native (binary COPY)       | Generic (batched insert)     | Generic (batched insert)            | N/A              |
| Direct bulk load (no staging) | Yes (`PassThrough` + `MsSqlBulkInsert`) | Not yet                    | Not yet                      | Not yet                             | N/A              |
| Delete detection (reconcile)  | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |
| SCD2 target                   | Yes                             | Yes                        | Yes                         | Yes                                  | N/A              |

Direct bulk load writes a full load or reload straight into the target instead of staging it first, with
a clear-first choice of delete or truncate — see
[Loading straight into the target](docs/replication-concepts.md#loading-straight-into-the-target). The
other engines still stage first; each needs its own direct-load writer.

Every engine above also offers `TriggerAudit`, a portable trigger-based option — see
[Replication concepts](docs/replication-concepts.md#reader-kinds).
DuckDB is a source only, for query-based sources like Parquet, CSV, an S3 glob, or an attached
database. It is not a replication target. **Arrow Flight SQL** is also a source only — a reload of a
table or query from any server speaking the protocol, usually a SQL proxy, with basic or bearer-token
authentication; see [Drivers and libraries](docs/drivers-and-libraries.md#arrow-flight-sql). See `architecture/planning/done/overview.md` for the
broader ambition and `architecture/detailed-design.md` for the full system design.

The table above is the five built-in drivers. Anything else reachable through an ADO.NET
provider — SQLite, Firebird, anything ODBC — plugs in as a source without a DbDataSync rebuild, through
a YAML descriptor. See [Drivers and libraries](docs/drivers-and-libraries.md).

## Install

```powershell
irm https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/install.ps1 | iex
```

```bash
curl -fsSL https://github.com/DbDataSync/DbDataKit/releases/download/dbdatasync/dist/install.sh | bash
```

This installs the `dbdatasync` command line into your own profile. It needs the .NET 10 runtime. See [Install](docs/install.md) for Scoop and a machine-wide
install. There is no service, installer or container image of Sync any more: the install of the suite is four services (console, Workspace, Gateway, Runner).

## Where the screens are

The screens are the dbdatakit console's: a sync project's in the Workspace (connections, replications, mappings, scripts, provisioning plans, publishing, and
the **Runs** tab: run now and a bulk load of what the draft published, each run read back with what every pass did) and the Runner's for the rest of what runs
(its Tasks, Runs and Packages pages: the schedule, pause and resume, cancel, the history and logs, the logins bound to the set's slots). The two link to each
other: the Runs tab links to the package's tasks and runs on the Runner, and a version the Workspace published names its project, which the Runner's task
details and version list link back to.

### A set on a Runner: the order of its first runs

A published set's drains run as soon as the version is served, and its database setup (the target tables) is the manual task `provision`, which a person applies
(the Provisioning tab's **Apply on the Runner**). A mapping whose target table does not exist yet, may not be created by a pass (`createTargetTableIfMissing`
off) and has no cached target columns refuses its first pass before anything is queued: the pass fails naming the table (`TargetTableMissing`), the mapping
keeps no position and no hold, and the next pass after the setup is applied (its next scheduled time, or **Run now**) captures a position and starts the initial
load. A segment of an initial load that finds its table gone gives the load up the same way rather than leaving the mapping held as loading.

### A bulk load as a person asks it

The drain's `requests` input takes a bulk load as asked, and the drain divides it against the source (as the Sync API did before the server was deleted):
`[{"kind": "BulkLoad", "mapping": "orders", "segments": "[{\"mode\":\"auto\",\"column\":\"Id\",\"bucketCount\":8}]"}]`. `segments` is the list of
segments as a mapping's `defaultSegmenting` writes them (`full`, `list`, `range`, `auto`, `custom`); `[]` is the mapping's own default segmenting (or the whole
table). The segments become one batch of the drain's queue with the source's row estimate; a bulk load of a mapping already in flight is refused, naming it.
The Workspace's Runs tab sends this request; a request without `segments` is queued as it is written (the form a cutover hands over).

## Next steps

- [**Configuration**](docs/configuration.md) —
  the command line, the engine, connection files and logins.
- [**Getting started**](docs/getting-started.md) —
  set up your first replication.
- [**Replication concepts**](docs/replication-concepts.md) —
  change detection, bulk loading, and delete reconciliation.
- [**Drivers and libraries**](docs/drivers-and-libraries.md) —
  add another engine (MySQL, Oracle, ...) without a rebuild.
- [**Building from source**](docs/development.md) —
  the dev loop, tests, and repository layout.
