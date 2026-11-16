# Getting started

[DbDataSync](../README.md) · [Install](install.md) · [Configuration](configuration.md) · **Getting started** · [Replication concepts](replication-concepts.md) · [Drivers and libraries](drivers-and-libraries.md) · [Building from source](development.md)

This walks through setting up your first replication. The Sync server was deleted (plan 33 SC23): a replication is authored in a **sync project of the
Workspace** (or as the files of a configuration repository), published to a **Runner** as a package, and run by that Runner. The screens are the dbdatakit
console's: the Workspace's for authoring, the Runner's for runs.

## Your first replication

1. **A sync project.** In the console, open the Workspace and create a project of the kind `sync`. Its files are a configuration repository: `config/connections`,
   `config/replications/<name>/task.yaml` and the replication's table mappings, `config/scripts`.
2. **Connections.** Add a connection file for each database (`connection/2`, see [Configuration](configuration.md#connection-files)). The password is a reference
   (`password_secret`), never a value: the Runner holds the login.
3. **A replication.** Name it, pick its source and target connection and database, its schedule (`Continuous` or `Periodic` with a cron expression) and its
   pipeline (reader, cache, writer).
4. **Table mappings.** Pick the source and target tables and map the columns. The engine reads the mapping's column cache and nothing else, so refresh it from the
   catalog cache of the project before you publish (a mapping with no source cache is refused at publish).
5. **Publish to a Runner.** From the Workspace, or with `dbdatasync package publish --to runner --server <runner> --channel prod --bind <slot>=<connection> --serve`.
   Each connection of the set is a slot, bound on the Runner to a connection it holds with the run login.
6. **Run.** The Runner runs each replication's drain on its schedule; **Run now**, pause and resume are on its Tasks page, and its Runs page shows each run with its log.

A target table that does not exist yet is created by the set's manual task `provision` on the Runner (the replication, the mapping and the action
`createTargetTable`), planned beforehand with `dbdatasync provisioning plan <replication> --from-cache`.

### Telling DbDataSync a table's primary key

A table mapping uses the source's own primary key where it has one. A view, an ODBC source or a Flight SQL
proxy often reports none, so the target table would be created without a key. The mapping's `primaryKey` lists the columns that form the key. They replace whatever the
source reports, and they decide the `PRIMARY KEY` in a generated `CREATE TABLE` (those columns are always
created `NOT NULL`, even if the source calls them nullable), the key an SCD Type 2 writer versions rows by,
and the key a key-based delete reconcile matches on. Only columns the mapping carries across can be chosen.
The order matters — it is the order of the index — so a composite key is listed in order.

## Bulk loading a table

Incremental sync only applies changes made at the source. If the target drifts for some other
reason — a bad deploy, a manual edit, a mapping that was wrong for a while — use a bulk load instead.
A bulk load is a request of the replication's drain on the Runner (its `requests` input, or an item of its queue while it is up). It re-reads the source and makes the
target match it.

A bulk load applies to one table mapping, and optionally to just one segment of it: a list of values, a
range, an even split of a column's range into buckets, or a custom segmenting strategy (below). Each
segment runs as its own independently queued run. A bulk load never advances the incremental watermark,
so it can run against a live, scheduled replication without disturbing the ongoing sync. It shows up
in the same run history as the regular sync, tagged `BULKLOAD`.

Each mapping has a *Default reload segmenting* setting (`defaultSegmenting`) that states how it divides
for a reload. A bulk load request without segments uses that default, and a scheduled `BatchReload` pass
uses it too. Leaving it empty means the whole table, unsegmented.

### Custom segmenting strategies

`Auto` splits a column's value range into evenly sized buckets. That works until the boundaries need
to mean something — a calendar month, say, which isn't a fixed number of days. For that, a replication
can define named strategies (`segmentingStrategies` in `task.yaml`) that propose segments a different way.
There are four ways to author one:

- **DuckDB SQL** — runs against an ephemeral in-memory DuckDB and touches neither database. Good for
  a table whose write pattern you already know:

  ```sql
  SELECT strftime(d, '%Y-%m')       AS label,
         d                          AS range_start,
         d + INTERVAL 1 MONTH       AS range_end,
         d >= current_date - INTERVAL 3 MONTH AS selected
  FROM generate_series(DATE '2020-01-01', current_date, INTERVAL 1 MONTH) AS t(d)
  ```

- **Source SQL** / **Target SQL** — the same four columns, queried from the live source or from a
  control table on the target.
- **C#** — a bound `ISegmentingStrategy`, handed both connections and the source's metadata.

`label` names the segment in run history. `range_start`/`range_end` are half-open. `selected` decides,
for a mapping whose default is a strategy, which segments a scheduled pass reloads on its own. A strategy such as "the last three
months" is re-evaluated against the current date every time it runs, so it needs no separate
scheduling logic.

If a mapping's default strategy queries a real database, that query runs on every scheduled pass. Whether that's acceptable depends on your tables, so
DbDataSync leaves that judgment to you.

A reader, staging or writer kind must be one the connection's driver supports, including which readers can be segmented and which writers
*reconcile*: removing target rows the source no longer has, rather than only inserting and updating.

A replication can also be a standalone reload instead of an incremental sync. Set its reader to
`MsSqlBatchReload` and give each mapping a *Default reload segmenting* list; it re-reads that list on
its normal schedule.

> **Breaking change.** The `segments` reader option — a JSON array typed directly into the reader's
> settings — is no longer read. Segmenting now lives on the table mapping. A config that still has that option behaves as though it had none (full table, unsegmented)
> until you fill in the mapping's *Default reload segmenting*. There is no automatic conversion between
> the two.

## Next: Replication concepts

The walkthrough above used the defaults. See [Replication concepts](replication-concepts.md) for the
full picture — every reader kind, how a first-ever load actually runs, and delete reconciliation.
