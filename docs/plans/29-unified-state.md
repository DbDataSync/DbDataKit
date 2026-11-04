# Plan 29: one structure for the suite's state databases

Status: **proposed, high level** (2026-11-04, at the owner's request: "start designing a more unified state database structure";
revised the same day with the owner's notes on topologies, configurable namespaces, SQLite files and large payloads). Not
sliced. The first new user is the runner's progress store (plan 28) for DbDataIntegration (plan 27).

**State** here means what the suite's own services keep to run: identity, sessions, pairings, published definitions, schedules, the
work queue, runs, notifications, audit, lineage, progress. It is not the customer's data (warehouses, DbDataStore entities and their
history), which lives in the customer's databases and is outside this plan.

## 1. What exists today

`DbDataKit.State` gives every product the same mechanism: a `StateSchema` (a list of SQL templates, applied in order, rendered for
SQLite, SQL Server or PostgreSQL), one integer schema version per database (`PRAGMA user_version` on SQLite, a `SchemaVersion` table on the
others), and `DbDataKit.State.Engines` to open it on the engine an operator names. Shared libraries supply **fragments** (a template or
two) that products include.

| Owner of the database | Its tables | How it is built |
| --- | --- | --- |
| **Console** | accounts, invites, notification feed, federated roles and sessions, account keys, one-time values | `StateSchema` of shared fragments only |
| **Proxy / serving layer** | notification feed; saved queries (3 tables); published definitions (`DefinitionVersions`, `DefinitionServing`, `DefinitionEvents`); app storage; the identity home (users, credentials, sessions, invites, groups, members, role assignments, service accounts, OIDC clients, codes, tokens, signing keys, external memberships, group mappings); the group's client sessions, keys, secrets, running instances, management audit; group trust (settings, paired clients, pairing codes) | `StateSchema`, with Proxy's own templates **interleaved by position** among the identity home's (`HomeTemplatesBeforeGroup`, `...BeforeTrust`, `...BeforeSecrets`) |
| **Sync** | tasks, task runs, logs, work queue, run locks, pause events, change watermarks, change-check history, bulk-load batches, verification results, notification feed | its own `Migrations` list in `DbDataSync.State`, including the shared work queue and notification fragments |
| **Build server** | work queue, schedule jobs, schedule job pauses | `StateSchema` of the scheduling fragments |
| **Build, on each target** | tracking tables (events, rows and duration per load) | `TrackingDdl`, on the customer's target by design: run history travels with the data |
| **DbDataStore server** | none: its instance id and pairings are files (`server.yaml`) | |
| **Shared fragments not yet in a product** | lineage (`LineageNodes`, `LineageEdges`, `LineageRuns`, `LineageVersions`) | `LineageSchema` |

## 2. What is wrong with it

1. **One version number per database.** A database knows its scripts by position, so a product that mixes fragments from several
   libraries must keep their order forever. Proxy already carries three constants to interleave its tables among the identity home's, and
   every new fragment in the home makes that harder. A library cannot add a table to its fragment without each product that includes it
   appending a script in the right place.
2. **Products cannot share a database.** The same table names appear in several products (`WorkQueue`, `Notifications`, the identity
   tables), so two products on one server need a database each (`DbDataKit.State.Engines`' README says so). An install with five services
   on SQL Server has five state databases to create, secure and back up.
3. **The same thing is kept several ways.** Sync has its own task runs and logs, Build its tracking tables, the run model in
   `DbDataKit.Monitoring` sits beside both, and the console pulls each server's runs to show them together. Notifications, work queues and
   audit are each copied per product. Pairing is in a state database for the serving layer and in files for DbDataStore's server.
4. **No owner per table.** Nothing says which library owns a table, so changing one means finding every product that includes it.
5. **Conventions vary**: how ids, times, JSON documents and long text are stored differs by product and by when a table was written.

## 3. The structure

### 3.1 Modules, each owned and versioned on its own

A **module** is a set of tables owned by one library, with **its own version** and its own append-only list of migrations. A database
holds several modules, and records each one's version in one table:

```
dbdatakit_modules
  module          -- 'identity', 'scheduling', 'runs', 'runner_progress', 'sync', ...
  scope           -- whose copy it is: the install, a group, or one service (section 3.2)
  schema, prefix  -- where its tables are (section 3.2), so any service that shares the database can find them
  version         -- the last migration applied
  applied_at, applied_by  -- the service and its version that applied it
```

The registry table itself lives in the `dbdatakit` schema (on SQLite, the table `dbdatakit_modules`), and that too is configurable.

- A service declares the modules it needs. Opening the database applies each module's missing migrations, in module order, then in each
  module's own order. **No interleaving**, and a library adds a migration to its module without any product changing.
- **Dependencies** between modules are declared (`runs` before `runner.progress`), never implied by position.
- **Compatibility** (plan 24, N and N-1): a service refuses to start, with a message naming the module and both versions, when a module's
  version is newer than it can read; it never downgrades a module.
- **Clean break** (plan 13): a database with today's single version number is refused with a message naming the database. It is not
  converted.

### 3.2 Names: a namespace per module, configurable

**Owner, 2026-11-04:** the schema and the prefix must be configurable for every namespace, and anything the suite prefixes starts with
`dbdatakit_`.

A **namespace** is one copy of one module: the module, and its **scope** (the whole install, one group, or one service). Two serving
groups that share one database each have their own namespace for the modules that are per group (sessions, the group's trust), and share
the install's namespace for the modules that are per install (identity, when the install chooses one home).

Each namespace has two settings, both configurable by the administrator:

| Setting | What it does | Default on SQL Server and PostgreSQL | Default on SQLite |
| --- | --- | --- | --- |
| `schema` | the database schema its tables are created in | `dbdatakit_<module>` | none (SQLite has no schemas in a file; see section 3.2a for files) |
| `prefix` | put before each table name | none | `dbdatakit_<module>_` |

A scope other than the install adds its name: the default for a group's sessions is the schema `dbdatakit_sessions_<group>` (or the
prefix `dbdatakit_sessions_<group>_`). Any arrangement an administrator prefers works, for example one schema `dbdatakit` for everything
with a prefix per module, or a schema per product.

- **Configured where the service is configured** (its `server.yaml` state section), per namespace, with the defaults above for anything
  not named.
- **Recorded in `dbdatakit_modules`**, so a service that shares the database (the console reading every service's runs, for example) finds
  a namespace without being told.
- **Checked when the database is opened:** names valid for the engine (PostgreSQL allows 63 characters, SQL Server 128), and no two
  namespaces rendering to the same table. A clash or a name that is too long stops the start with a message naming the setting.
- **Templates never write a table name.** They write `{table:users}`, and the dialect renders it from the namespace's schema and prefix.
- **Rights per namespace:** with a schema each, a service's login can be granted only the namespaces it uses.

### 3.2a SQLite files

**Owner, 2026-11-04:** several files are supported but not required; not a priority unless contention is a concern.

- **Default: one file per service**, every namespace in it, as today.
- **Option:** a namespace may name its own file, attached when the database is opened (`ATTACH`), for a module that writes heavily (the
  runner's progress, run logs) so its writes do not wait on another module's. SQLite allows one writer per file at a time, so this is the
  remedy if contention appears.
- **One limit to keep in mind:** in WAL mode SQLite commits a transaction atomically in each file, not across files. Modules that must
  commit together (a checkpoint and its run's outcome, section 4) stay in one file; the module definitions say which.

### 3.3 Common conventions

| What | Convention |
| --- | --- |
| Table and column names | lower case with underscores, the same on every engine (no quoting needed) |
| Ids | UUID v7 (time-ordered), as `uniqueidentifier`, `uuid` and text; natural keys only where the thing has one (a module name) |
| Times | UTC, `datetimeoffset` / `timestamptz` / ISO 8601 text; columns named `*_at` |
| Documents | JSON as `nvarchar(max)` / `jsonb` / text, with a `schema` column naming its format and version where it is a definition |
| Who | `service_id` (which service wrote it) where a database is shared; `created_by` as the identity home's subject id |
| Optimistic concurrency | a `row_version` integer on tables that several instances update |
| Retention | **7 days by default** (owner, 2026-11-04) for every table that holds history except the audit (runs, logs, notifications, settled dead letters, expired idempotency keys, payloads whose owner is gone), set in days and fractions of days (`0.25` is six hours), up or down, per module and per table, run by one shared job. The **audit** is kept until an administrator sets a limit (owner, 2026-11-04). Tables that hold **current** state (identity, definitions, a checkpoint's current value, Build's structural baseline) have no age limit: their rows go when what they describe goes |
| Large payloads | never a column of an ordinary table: stored in the `payloads` module (section 3.6) and referred to by id |

### 3.4 The shared modules

| Module | Owned by | Holds | Replaces |
| --- | --- | --- | --- |
| `identity` | `DbDataKit.Identity` | users, credentials, groups, roles, service accounts, OIDC clients and tokens, signing keys, sessions, invites, federation | the identity fragments in the console and the serving layer |
| `trust` | `DbDataKit.Web` | instance id, paired clients, pairing codes, the group's secrets and keys | `GroupTrust`, Proxy's group tables; DbDataStore's `server.yaml` pairings |
| `definitions` | `DbDataKit.State` | published definitions, which version each instance serves, events | `DefinitionSchema`; used by the serving layer **and** the runner |
| `scheduling` | `DbDataKit.Scheduling` | schedules, job state, pauses, the work queue, leases, run locks | Sync's and Build's copies of the work queue |
| `runs` | `DbDataKit.Monitoring` | one run model for every product: run, outcome, timings, counts, classified failure, logs | Sync's task runs and logs (later), the Build server's run records; Build's tracking tables stay on the target and are read into it |
| `notifications` | `DbDataKit.Monitoring` | the feed and each reader's position | the feed fragment in each product |
| `audit` | new, shared | who did what, through what (a person, an agent, an action), when | Proxy's management audit; elevated actions' records (plan 27 section 4.5) |
| `lineage` | `DbDataKit.Lineage` | nodes, edges, run lineage, versions | `LineageSchema` |
| `app_storage` | `DbDataKit.State`, used by the apps host | per-instance app storage (plan 26 section 2a) | `AppStorageSchema` |
| `payloads` | `DbDataKit.State` | large values in chunks, read and written as streams (section 3.6) | large columns in other tables |

### 3.5 Modules for the new products

| Module | Owned by | Holds |
| --- | --- | --- |
| `runner` | the runner (plan 28) | packages and their versions per channel, runner instances, labels, task leases |
| `runner_progress` | the runner, for DbDataIntegration and any package | **the progress store** (section 4) |
| `agents` | `DbDataKit.Agents` | conversations, transcripts, tool calls, approvals, usage and cost |
| `knowledge` | `DbDataKit.Knowledge` | the search index and its embeddings, where the install keeps them in the state database |
| `actions` | the actions library (plan 27 section 4.4) | action principals' grants, *run* grants, rate-limit counters |

Product modules (`sync`, `serving`, `console`, `build`) hold what is truly one product's: Sync's watermarks and change-check history,
saved queries, console settings.

### 3.6 Large payloads: a chunked object store

**Owner, 2026-11-04:** size is not the concern; modern databases hold large values well. The concerns are **streaming** data in and
out without holding it all in memory, and **accidental queries** that select a large column across many rows. If a chunked object store is
the most consistent answer, start there.

It is. A large value in an ordinary column fails both concerns in different ways per engine: SQL Server streams `varbinary(max)` only
with sequential access, PostgreSQL's `bytea` is read whole unless it is a separate large object with its own life cycle, SQLite streams
only through its blob handle; and any `SELECT *` on the table reads every value. So the suite keeps large values in one module of their
own:

```
payloads.payload  -- id, size, sha256, media type, created_at, created_by, owner (module and row it belongs to), state (writing, complete)
payloads.chunk    -- payload_id, sequence, data (one chunk, 1 MiB by default, configurable)
```

- **One stream API** (`OpenWrite`, `OpenRead`, in `DbDataKit.State`): a writer sends chunks as they arrive and marks the payload complete
  with its size and hash at the end; a reader reads chunk by chunk. Memory use is one chunk, on every engine, with the same code.
- **No large column anywhere else.** Other modules store a payload id. A query on a module's table can never pull megabytes by accident,
  and the payload tables are only read through the stream API.
- **Integrity:** the hash is checked on read; a payload left in *writing* (a writer that died) is removed by retention.
- **Life cycle:** each payload names its owner; when the owning row goes, so does the payload, through the same retention job.
- **Back ends:** the database's chunk tables by default. The same API can later write to a file system or an object store (S3, Azure Blob)
  for installs that want large files out of the database; the payload row stays in the database either way.
- **Small documents stay inline:** a definition's JSON or a small state value is an ordinary column with a size limit set by its module.

## 4. The progress store (first user)

What DbDataIntegration tasks need to rerun without losing or repeating work (plan 27 section 4.3). Kept by the runner, reached through the
runner protocol (plan 28 section 4.1), so a task never holds a login to it. Every row is keyed by the **package, the channel and the
task**, so a preview's progress never mixes with production's.

### 4.1 Checkpoints (what the first draft called cursors)

A **checkpoint** is where a task got to in an outside source, saved so the next run carries on from there instead of starting over. The
draft called it a "cursor"; "checkpoint" avoids confusion with a database cursor. Examples:

| Source | What the checkpoint holds | The next run asks for |
| --- | --- | --- |
| an API that filters by time | the last modified time seen: `2026-11-04T09:15:00Z` | "orders changed since 09:15" |
| an API with change tokens (Microsoft Graph delta queries, Salesforce, many others) | the token the API returned last time | "changes since this token" |
| an event feed or queue | the last sequence number or offset processed | "events after 1 042 377" |
| a paged export that takes several runs | the page or continuation token reached | "the next page after this one" |
| a folder listed by name or date | the last file name or time handled | files after it (the file ledger, 4.3, catches the rest) |

Sync's `ChangeWatermarks` are the same idea for databases. What makes a checkpoint more than a saved value:

1. **It moves only when the data is safely landed.** A task reads from the old checkpoint, writes what it read, and saves the new
   checkpoint **in the same step that records the work as done**. A run that fails half way leaves the checkpoint where it was, so the
   next run reads that part again (and the idempotency keys and file ledger stop it from writing twice). Saving it first would lose data on
   a failure; saving it much later would repeat work.
2. **One writer at a time.** It is read and written with a `row_version`, so two overlapping runs of one task cannot both move it; the
   second is refused and retried.
3. **It has a history.** Each change is kept (old value, new value, run, time), so an operator can see how far a task got and **rewind**
   it ("reload everything since Monday") from the console, as an audited action.
4. **It is per channel**, so a preview starting from scratch does not move production's position.

### 4.2 The tables

| Table | Holds | Notes |
| --- | --- | --- |
| `checkpoint` and `checkpoint_history` | each named position and its changes (4.1) | **typed** values, not JSON, in the shape the task declares (4.3); one checkpoint can hold several parts (a time and an id to break ties) and can be kept per key (per customer, per folder) |
| `state` | small named values the task keeps between runs | typed and declared like checkpoints (4.3); a `json` type exists for the rare value that has no better one; anything large is a payload |
| `file_ledger` | each file seen: connection, path, size, modified time, content hash, first seen, processed by which run, outcome | "have I processed this file", by hash, so a renamed or re-sent file is recognized |
| `idempotency` | a key for each outbound write and its result, with an expiry | a retried push does not create a second ticket |
| `dead_letter` | a record an outside system refused: error, attempts, status (open, replayed, discarded), its body as a payload id | replayed from the console or by a task |
| `inbox` | received webhooks waiting for a task: source, signature check, received at, taken by which run, the body as a payload id | filled by the serving layer, which receives webhooks (plan 28 section 7) |

### 4.3 Progress across versions: declared, typed, migrated by the runner

**Owner, 2026-11-04:** a new version of an integration may change what its progress looks like, and that needs a **standard versioning
and migration process**, so no integration rolls its own migration, and none wastes time serializing and deserializing its state.

**Declare the shape, let the runner plan the change** (the same idea as Build, which plans a table change from the declared shape
against the live one):

1. **A task declares its progress** in the package manifest: each checkpoint and state value, its **type** (text, whole number, decimal,
   time, date, true/false, bytes as a payload, `json` as a last resort), whether it is kept per key and the key's parts (`per: [customer]`),
   and a default for a new key.
2. **The store keeps typed values**, one row per value and key, in typed columns (`value_text`, `value_int`, `value_time`, ...). Nothing
   is turned into JSON and back; a module's tables never change shape per integration, so no DDL is run for an integration.
3. **The kit gives typed access.** In .NET the declaration generates typed accessors (`progress.OrdersModified` is a
   `DateTimeOffset`); the runner protocol carries the same types. A run reads all of its task's progress in one query at start, works on
   it in memory, and writes what changed in **one statement per commit point**, together with the run's outcome (section 4.1, rule 1).
4. **The version of the progress is the declaration.** The runner compares the new version's declaration with the old one's and works
   out the migration itself. The author numbers nothing.
5. **Standard changes need no code:**

| Change in the declaration | What the runner does |
| --- | --- |
| a value added | new keys get the default; existing keys get it too, or start empty if it has none |
| a value removed | dropped (its history kept for rollback) |
| a value renamed | moved, when the declaration says `renamed_from` |
| a type widened (whole number to decimal, date to time) | converted |
| a type changed otherwise (text to time) | converted with a declared format (`format: "yyyy-MM-dd"`), or refused at staging naming the value |
| a key part added (`per: [customer]` becomes `per: [customer, region]`) | each existing key copied to the new parts by a declared rule (`fill: all-regions`, or a default), or reset |
| a value reset on purpose | `reset: true` on that value in this version: the next run starts it from its default (a full reload) |

6. **When it happens:** while the version is **staged** (plan 30 section 3.1) the runner checks the migration and runs it as a dry run,
   so a change that cannot be made fails the staging, not a run. At **activation** it applies the migration per task, after the task's
   runs in flight finish, in one transaction with the switch, and per channel at that channel's activation.
7. **Loss is never silent.** A change that loses progress (a value removed, a reset, a key part dropped) is listed when the version is
   staged, and activation asks the administrator to confirm it (plan 28 section 5's posture). The values before the migration are kept in
   `checkpoint_history`, so a **rollback** to the previous version restores them.
8. **Code only as the exception.** A change the standard rules cannot express (one checkpoint split into two by parsing it) can supply a
   migration function, which runs at staging over the old values and whose output is checked against the new declaration.

**For Sync later:** its `ChangeWatermarks` are declared checkpoints of this kind, so Sync on the runner uses the same process.

## 4a. State that lives with the data, and a unified copy

**Owner, 2026-11-04:** a unified state is desirable, and state kept on the target with the data is desirable, so the suite supports
**both at the same time**. Build's tracking tables are the first case.

### 4a.1 Why Build keeps state on the target

Build's tracking tables (`DbDataBuild/DESIGN.md` section 12) are not only a run log. Build plans from them: the last recorded shape of
each object is the baseline for drift detection (`schema_version`), blocks and their acknowledgements gate a plan (`block_log`), loaded
intervals drive history consistency (`operation_interval`), an interrupted apply resumes from them (`migration_log`, `ddl_log`), and the
metadata documents (`metadata_current`, `object_info`, `metadata_dependency`) are read in the warehouse by people and tools. Kept on the
target, that state:

- **travels with the data:** a restored backup or a copied database carries the history that matches its structure;
- **works without a server:** the Build CLI on a laptop plans against a target with nothing else running;
- **is readable where the data is,** by anyone with access to the warehouse.

Build already lets the tracking connection be another connection than the data's ("central tracking"); unified state generalizes that.

### 4a.2 Three settings, per connection

| Setting | Authoritative record | Copy |
| --- | --- | --- |
| `target` (today) | the tracking schema on the target (or the project's tracking connection) | none |
| `state` | a `build_tracking` module in the unified state database: the same logical tables, keyed by connection, in their namespace | none |
| `both` | **one of the two, named** (`primary: target` by default) | the other, kept up to date by a follower |

**Default (owner, 2026-11-04): `both`, with the target as primary.** It keeps today's behaviour and adds the unified copy; an
administrator can choose another setting per connection (plan 28 section 5's posture).

### 4a.3 How `both` works

1. **One side is authoritative for decisions.** Build plans, blocks and resumes from the **primary** only. Two stores are never both
   consulted for a decision, so they can never disagree mid-plan.
2. **The primary is written as today**, synchronously, inside the apply's tracking gate ("started" before a step, the outcome after).
3. **A follower copies to the other side.** The tracking tables are append-only by design, so the follower reads the rows added since
   its own checkpoint (section 4.1: the same idea, for the suite's own state) and writes them, idempotently, keyed by connection and the
   row's own key or time. A copy that is down or slow never stops or slows a deploy or a refresh; it catches up.
4. **Divergence is detected, not hidden.** If the primary goes back in time (a target restored from a backup), the follower finds the
   copy ahead of it, marks that connection's copy as **diverged**, and the console shows it with the point where they part. An
   administrator chooses what to do (keep the copy's later history as an archive, or reset the copy to match), as a recorded action.
5. **Copies of a database are recognized.** A development copy of a warehouse carries production's tracking rows. Rows are copied
   under the connection's **identity** (the server and database it really is, plan 18 section 4), not only its name, so a copy's history
   is never mixed into production's.
6. **Switching the primary** (target to state, or back) is an explicit, recorded operation that first brings the new primary up to date,
   then changes the setting.

### 4a.4 What the unified copy gives

- the `runs` module reads Build's runs from it, with Sync's and the runner's, in one query (plan 13 W10's monitoring view);
- the catalog (plan 22 section 3) and DbDataKnowledge (plan 27) read Build's metadata documents for every connection in one place;
- retention, backup and access are the unified state's (sections 3.3 and 5), while the target keeps its own copy for as long as Build's
  rules say.

### 4a.5 The same pattern elsewhere

Any product can keep state where its work is and still appear in the unified state: a primary written where the product needs it, a
follower that copies to the other side, divergence detected. Candidates: Sync's change-tracking state when Sync moves to the runner (plan
28 section 8), and DbDataStore's history, which already lives in the customer's database.

**Sync defaults to `both` when it adopts this pattern** (owner, 2026-11-04). Its state moves into the unified state's modules and is also
kept on the target (plan 28 section 8, item 3). Its primary is the target, as for Build (owner, 2026-11-04).

## 5. Where the databases are

The structure (sections 3 and 4) is the same whatever the topology. Which services share a database is the administrator's choice
(plan 28 section 5's posture), with a default. **The owner confirmed all three (2026-11-04).**

| Topology | What it is | For | Against |
| --- | --- | --- | --- |
| **One per service** (default for a single instance) | each service its own SQLite file, as today | nothing to set up; a service's failure stays its own | cross-product views are pulled and merged; several files to back up |
| **One per group** (required for groups) | a serving group's or a runner group's instances share one SQL Server or PostgreSQL database | scale-out and availability (plan 20 section 6) | one more database per group |
| **One per install** (option; confirmed by the owner, 2026-11-04) | every service uses one SQL Server or PostgreSQL database, each in its own modules | one backup (plan 22 section 4); the console reads runs, audit and notifications for every service with one query; one identity module | a shared dependency: that database's outage stops every service; upgrades of a module must suit every service that uses it (N and N-1 per module) |

Each service's login can be limited to the modules it uses, so sharing a database does not mean sharing everything.

## 6. Moving onto it

1. **New products first:** the runner, its progress store and DbDataIntegration use this structure from their first slice.
2. **The shared library:** `DbDataKit.State` gains modules, the module version table, namespaced templates and the conventions; the
   existing fragments become modules.
3. **Existing services, one at a time,** each in its own slice: the console, the serving layer (removing the interleaving), the Build
   server, DbDataStore's pairings. Each is a clean break: an install sets up its state again (plan 13 section 3), and the old database is
   refused with a message.
4. **Sync last,** with or after the work that moves Sync to the runner (plan 28 section 8), since its state is the largest and its
   change-tracking state is what that plan has to design.

## 7. Questions for the owner

| # | Question | Status |
| --- | --- | --- |
| 1 | The topologies | **Decided** (owner, 2026-11-04): one per service, one per group and one per install, all offered (section 5) |
| 2 | Namespaces | **Decided** (owner, 2026-11-04): schema and prefix configurable for every namespace, `dbdatakit_` for anything prefixed (section 3.2); several SQLite files supported, not required (section 3.2a) |
| 3 | Retention defaults | **Decided** (owner, 2026-11-04): 7 days, in days and fractions of days, adjustable up or down per module and table (section 3.3). The audit is kept until an administrator sets a limit (owner, 2026-11-04) |
| 4 | Which conventions to apply to existing tables when they move | **Decided** (owner, 2026-11-04): all of section 3.3; a moved table is a new table (clean break) |
| 5 | Large payloads | **Decided** (owner, 2026-11-04): a chunked object store from the start, streamed in and out, never a column of another table (section 3.6). Chunk size 1 MiB by default (owner, 2026-11-04) |
| 6 | Engines | **Decided for now** (owner, 2026-11-04): SQLite, SQL Server and PostgreSQL; no moving between engines (an export and import is plan 22 section 4's backup work) |
| 7 | Build's tracking tables | **Decided** (owner, 2026-11-04): both, at the same time. Proposed in section 4a: per connection `target`, `state` or `both`; with `both`, one named primary decides and a follower keeps the other up to date. Default `both`, target primary (owner, 2026-11-04); Sync defaults to `both` when it adopts the pattern |
| 8 | "Checkpoint" for what the draft called a cursor | **Decided** (owner, 2026-11-04) |
