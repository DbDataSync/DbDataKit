# Plan 29: one structure for the suite's state databases

Status: **proposed, high level** (2026-11-04, at the owner's request: "start designing a more unified state database structure"). Not
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
dbdata_modules
  module          -- 'identity', 'scheduling', 'runs', 'runner.progress', 'sync', ...
  version         -- the last migration applied
  applied_at, applied_by  -- the service and its version that applied it
```

- A service declares the modules it needs. Opening the database applies each module's missing migrations, in module order, then in each
  module's own order. **No interleaving**, and a library adds a migration to its module without any product changing.
- **Dependencies** between modules are declared (`runs` before `runner.progress`), never implied by position.
- **Compatibility** (plan 24, N and N-1): a service refuses to start, with a message naming the module and both versions, when a module's
  version is newer than it can read; it never downgrades a module.
- **Clean break** (plan 13): a database with today's single version number is refused with a message naming the database. It is not
  converted.

### 3.2 Names: a namespace per module

Every table belongs to its module's namespace:

| Engine | Rendered as |
| --- | --- |
| SQL Server, PostgreSQL | a database schema per module: `identity.users`, `runs.run`, `runner_progress.cursor` |
| SQLite | a prefix: `identity__users`, `runs__run` (SQLite has no schemas within a file) |

Templates name tables as `{module}.{table}`, rendered by the dialect. Two services can then share one database without name clashes, and
an administrator can grant a login rights on one module's schema only.

### 3.3 Common conventions

| What | Convention |
| --- | --- |
| Table and column names | lower case with underscores, the same on every engine (no quoting needed) |
| Ids | UUID v7 (time-ordered), as `uniqueidentifier`, `uuid` and text; natural keys only where the thing has one (a module name) |
| Times | UTC, `datetimeoffset` / `timestamptz` / ISO 8601 text; columns named `*_at` |
| Documents | JSON as `nvarchar(max)` / `jsonb` / text, with a `schema` column naming its format and version where it is a definition |
| Who | `service_id` (which service wrote it) where a database is shared; `created_by` as the identity home's subject id |
| Optimistic concurrency | a `row_version` integer on tables that several instances update |
| Retention | each module declares its retention per table (days, or "keep"), run by one shared job |
| Large payloads | kept in the row up to a limit; above it, in a payload table in chunks, or an object store (question 5) |

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

## 4. The progress store (first user)

What DbDataIntegration tasks need to rerun without losing or repeating work (plan 27 section 4.3). Kept by the runner, reached through the
runner protocol (plan 28 section 4.1), so a task never holds a login to it. Every row is keyed by the **package, the channel and the
task**, so a preview's progress never mixes with production's.

| Table | Holds | Notes |
| --- | --- | --- |
| `cursor` | a named position: a watermark, a page token, a last-modified time | read and written with `row_version`, so two runs of one task cannot both move it; written in the same step as the run's outcome where the task allows |
| `state` | small named JSON values the task keeps between runs | size-limited |
| `file_ledger` | each file seen: connection, path, size, modified time, content hash, first seen, processed by which run, outcome | "have I processed this file", by hash, so a renamed or re-sent file is recognized |
| `idempotency` | a key for each outbound write and its result, with an expiry | a retried push does not create a second ticket |
| `dead_letter` | a record an outside system refused: payload, error, attempts, status (open, replayed, discarded) | replayed from the console or by a task |
| `inbox` | received webhooks waiting for a task: source, signature check, payload, received at, taken by which run | if the runner receives webhooks (plan 28 question 4) |

## 5. Where the databases are

The structure (sections 3 and 4) is the same whatever the topology. Which services share a database is the administrator's choice
(plan 28 section 5's posture), with a default:

| Topology | What it is | For | Against |
| --- | --- | --- | --- |
| **One per service** (default for a single instance) | each service its own SQLite file, as today | nothing to set up; a service's failure stays its own | cross-product views are pulled and merged; several files to back up |
| **One per group** (required for groups) | a serving group's or a runner group's instances share one SQL Server or PostgreSQL database | scale-out and availability (plan 20 section 6) | one more database per group |
| **One per install** (option) | every service uses one SQL Server or PostgreSQL database, each in its own modules | one backup (plan 22 section 4); the console reads runs, audit and notifications for every service with one query; one identity module | a shared dependency: that database's outage stops every service; upgrades of a module must suit every service that uses it (N and N-1 per module) |

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

| # | Question | Proposed |
| --- | --- | --- |
| 1 | The default topology, and whether "one per install" is offered | one per service for a single instance, one per group for groups, one per install as an option the administrator chooses |
| 2 | Namespaces on SQLite | a table-name prefix (`module__table`); attached database files per module are the alternative |
| 3 | Retention defaults | per module, in its definition; runs and logs 90 days, audit kept, progress kept while the package exists |
| 4 | Which conventions to apply to existing tables when they move | all of section 3.3; a moved table is a new table (clean break) |
| 5 | Large payloads (dead letters, webhook bodies, files) | in the row up to 1 MB; chunks in a payload table above that; an object store later |
| 6 | Engines | SQLite, SQL Server and PostgreSQL, as today; no moving between engines (an export and import is plan 22 section 4's backup work) |
| 7 | Build's tracking tables | stay on the target; the `runs` module reads them, as Monitoring does today |
