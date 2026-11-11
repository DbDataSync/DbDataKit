# DbDataKit.State

What the suite's own services keep to run: identity, sessions, pairings, published definitions, schedules, the work queue, runs, notifications, audit, lineage and the runner's progress. It is not the customer's data (warehouses, DbDataStore entities and their history), which lives in the customer's databases. The library is the mechanism; `DbDataKit.State.Engines` opens it on an engine, `DbDataKit.State.Cli` and `DbDataKit.State.Api` are the `state` verbs and endpoints every product has (their READMEs say what they do).

## Modules

A **module** is a set of tables owned by one library, with its own version and its own append-only list of migrations (`StateModule`). A database holds several modules and records each in one registry table (`dbdatakit.modules` on SQL Server and PostgreSQL, `dbdatakit_modules` on SQLite): module, scope, schema, prefix, version, a checksum of the migrations applied, who applied them.

* A service declares the modules it needs; opening the database applies each module's missing migrations under a database-wide lock (`sp_getapplock`, `pg_advisory_lock`, SQLite's write lock), so two services starting together never run one migration twice. A library adds a migration to its module and no product changes. There is no interleaving by position.
* `StateModule.DependsOn` holds the modules themselves, not names, so there are no cycles and the order is a depth-first walk.
* A module **newer than the service** is refused, naming the module and both versions; a module is never downgraded. An **edited migration that already shipped** is refused too (the checksum), naming the module and the migration number. A database made with one version number for the whole database (the structure before modules) is refused naming the file: clean break, no conversion.
* A module is kept per **scope**: the install, a group or one service (`StateScopeKind`); the scope's name is part of its namespace.
* A product never alters a module's tables; the owning library does, with a new migration.

The modules, among others: `identity`, `trust` (instance id, paired clients, pairing codes, the group's keys and secrets), `definitions` (versions, artifacts, bindings, what each instance serves and reports; used by the serving layer and the runner), `scheduling` (schedules, job state, pauses, the work queue with leases and pooled resources, run locks), `runs` (one run model for every product, with logs), `notifications`, `audit` (kept until an administrator sets a limit), `lineage`, `app_storage`, `payloads`, `runner`, `runner_progress`, `build_tracking`, `build_runs` (the runs of a Build server's scheduled plans: when, how it ended; 180 days), `sync_state` `agents` (conversations, the words kept 30 days, and the audit rows that stay: model turns with tokens, cost and what each turn was given, events, approvals; `AgentStore`) and `knowledge` (a channel's indexed items, their subjects and chunks, found by their words with the engine's own text search; `KnowledgeStore`, `DbDataKit.Knowledge.Index`; vectors will be a module of their own). `state show` lists what a database holds. Product modules hold what is truly one product's.

## Names: a namespace per module

A namespace is one copy of one module in one scope. Its `schema` and `prefix` are configurable (the `state:` section, `DbDataKit.State.Engines` README); the defaults are the schema `dbdatakit_<module>` on SQL Server and PostgreSQL and the prefix `dbdatakit_<module>_` on SQLite, with the scope's name added for a group or a service. `{module}` and `{scope}` may be used in both settings.

* A template never writes a table name: it writes `{table:name}` (its own module) or `{table:module.name}` (a declared dependency's), and an index `{index:name}`; the dialect renders them. A table is declared by being named, so there is no second list.
* The layout checks names when the database is opened: valid for the engine (63 characters on PostgreSQL, 128 on SQL Server), not reserved on any engine (`ReservedWords`: a table called `checkpoint` is refused on SQL Server), and no two namespaces rendering to the same table. A clash stops the start with a message naming the setting.
* Because every namespace is recorded in the registry, a service that shares the database finds another's modules without being told (the console reading every service's runs, audit and notifications with one query).
* **Rights per namespace:** `state grants` writes the `GRANT`s a login needs for the namespaces a service uses, so sharing a database does not mean sharing everything.
* SQLite: one file per service by default. A namespace may name its own `file:`, attached when the database is opened, for a module that writes heavily; modules that must commit together (a checkpoint and its run's outcome) stay in one file and a split is refused.

## Details that are easy to get wrong

* **Index names** are `{index:name}`, rendered with the namespace's prefix and no schema: SQLite index names are global to the file and PostgreSQL's live in the table's schema, so the prefix is what keeps them unique everywhere.
* A non-install scope with a `schema` or `prefix` setting that does not use `{scope}` gets the scope appended to the **default** only, so the clash check catches an administrator's own pattern that forgot it. `default` is the namespace settings' key for every module, so no module is named `default`.
* The registry's columns are `schema_name` and `prefix` (`schema` is a keyword on SQL Server); its own schema and prefix are the `registry` setting.
* `definitions` and `app_storage` are kept per **group**; a definition's content is opaque text (its format is the kind's business, `DefinitionKinds`), and `definition_event` has no retention rule because it is both the poll feed and the record of who changed what a channel serves.
* Retention deletes in one statement per rule, children first, in one transaction.
* The old single-version path (`StateSchema`, `SchemaVersion`, per-product `Migrations` lists, the older stores such as `UserStore` and `NotificationStore`) still exists beside the modules until plan 29a's Q25 deletes it; new code uses modules.

## Conventions

Every column has one of a few logical types written as tokens in the templates (`{{uuid}} {{time}} {{json}} {{bool}} {{blob}} {{decimal}} {{int}} {{key}} {{text}}`), so one logical schema runs on three engines and a row can be written out in a neutral form (this is what makes the snapshot format possible). A convention test runs over every registered module and fails on a named table and column.

| What | Convention |
| --- | --- |
| Names | lower case with underscores, the same on every engine, never a reserved word (`account`, `user_group`) |
| Ids | UUID v7 (time-ordered) by the token; ids that are shared across installs (the identity home's subject ids) stay opaque text |
| Times | UTC, columns named `*_at` |
| Documents | JSON by the token; small ones inline, anything large is a payload |
| Concurrency | a `row_version` integer on tables several instances update |
| Retention | one shared `RetentionJob`, rules declared by each module (`RetentionRule`): 30 days by default for history (runs, logs, notifications, settled dead letters, expired idempotency keys, orphaned payloads), in days and fractions (`0.25` is six hours), per module and per table; the audit has none until set; tables of current state (identity, definitions, a checkpoint's current value) have no age limit, their rows go when what they describe goes |

## Payloads: large values

A large value is never a column of an ordinary table (a `SELECT *` would pull it, and the engines stream large columns differently). The `payloads` module holds `payload` (size, SHA-256, media type, owner module and row, state `writing` or `complete`) and `chunk` (1 MiB by default). `PayloadStore.OpenWrite` and `OpenRead` are streams with memory use of one chunk on every engine; the hash and size are checked when a reader reaches the end (a reader that stops early is not verified), an incomplete payload is not readable, and retention removes a payload left in `writing` and one whose owner row has gone. Other modules store a payload id. `PayloadOptions.MaxBytes` caps a writer.

## The progress store (`runner_progress`)

What a task needs to rerun without losing or repeating work, kept by the runner and reached through its protocol, so a task never holds a login to it. Every row is keyed by package, channel and task, so a preview's progress never mixes with production's.

* **Checkpoints**: where a task got to in an outside source (a modified time, a change token, a sequence number). Values are typed (text, int, decimal, time, date, bool, bytes as a payload id, json), one row per value and key, in typed columns; a checkpoint can hold several parts and be kept per key. It moves **only with the run's outcome, in one transaction** (`Advance`): a failed run leaves it where it was. It is read and written with a `row_version`, so a second overlapping run is refused. Every change goes to a history, so an operator can see how far a task got and rewind it as an audited action (the runner's API `.../checkpoints`, `.../history`, `.../rewind`; the console shows them in a task's details).
* **State values** (a cursor token, a last-seen id): the same typed store, declared the same way; history and rewind apply to checkpoints.
* **File ledger** (a file by connection, path, size, modified time and content hash, so a renamed or re-sent file is recognised), **idempotency keys** with their results and an expiry, **dead letters** (the refused record as a payload; open, replayed or discarded), and the **inbox** (received webhooks, each taken by exactly one run).
* **Steps and units** (`step_result`, `unit_result`): a step is identified by task, name and key within an attempt chain and keeps its result (JSON under 64 KiB, or a streamed payload); a unit is one item of a fan-out. Their semantics are in the `DbDataKit.Runner.Kit` README.

### Progress across versions

A new version of a package may change what its progress looks like. The author numbers nothing: the task **declares** its progress in the manifest (`ProgressDeclaration`: each checkpoint and state value, its type, `per: [customer]` key parts, defaults), and `ProgressMigration.Plan` works out the change from the old and the new declaration.

| Change in the declaration | What the planner does |
| --- | --- |
| value added | new and existing keys get the default, or start empty with none |
| value removed | dropped (history kept for rollback): a loss |
| value renamed | moved when the declaration says `renamed_from` |
| type widened (int to decimal, date to time) | converted |
| other type change (text to a date or time) | converted with a declared format, or refused naming the value |
| key part added | each existing key copied by a declared rule (`fill`), or the checkpoint reset |
| reset on purpose (`reset: true`) | the next run starts that value from its default |

`ProgressMigration.DryRun` converts every stored value in memory and writes nothing (the runner does it while a version is staged); `Apply` converts everything first (one value that does not convert stops the whole change before anything is written), applies a plan with losses only when confirmed, and records the migration in the history under the actor, so `Restore` puts the previous values back on a rollback. A loss is never silent: staging lists it and activation asks for a confirmation. There is no migration function for a change the rules cannot express (one checkpoint split in two by parsing); the planner refuses it naming the value.

## State that lives with the data: tracking (`build_tracking`)

Build keeps its tracking tables on the target by design: they travel with a restored backup, work with no server running, and are readable where the data is. `DbDataKit.State.Tracking` adds a unified copy beside them. Per connection the setting `TrackingSettings` is `target` (today), `state` (the `build_tracking` module only) or `both`; **`both` with the target as primary is the default**.

1. One side is **authoritative for decisions**: Build plans, blocks and resumes from the primary only, so two stores never disagree mid-plan. The primary is written as before, synchronously.
2. A **follower** (`TrackingFollower`) copies to the other side by a checkpoint, idempotently, keyed by connection identity and the row's own key. A copy that is down or slow never slows a deploy or refresh; it catches up.
3. **Divergence is detected, not hidden:** a primary that went back in time (a target restored from backup) is found when the copy is ahead, the connection's copy is marked diverged with the point where they part, and an administrator chooses to keep the copy's later history as an archive or reset it, as a recorded action. Divergence compares positions and keys, not content (Build adds a row hash for the two streams that are updated in place).
4. **Copies of a database are recognised:** rows are copied under the connection's identity (server and database it really is), so a development copy of a warehouse is never mixed into production's history.
5. **Switching the primary** is an explicit, recorded operation that brings the new primary up to date first (`SwitchPrimaryAsync`).

The same pattern (a primary where the product needs it, a follower, divergence detected) is the intended way for any product to keep state with its work and still appear in the unified state; Sync's change-tracking state is the next user.

## Where the databases are

The structure is the same whatever the topology; which services share a database is the administrator's choice (`state init --topology`, `DbDataKit.State.Cli` README).

| Topology | What it is | For | Against |
| --- | --- | --- | --- |
| One per service (the default for a single instance) | each service its own SQLite file | nothing to set up; a failure stays its own | cross-product views are pulled and merged; several files to back up |
| One per group (required for a serving or runner group) | the group's instances share one SQL Server or PostgreSQL database | scale-out and availability | one more database per group |
| One per install (an option) | every service uses one database, each in its own modules | one backup; the console reads runs, audit and notifications of every service with one query; one identity module | that database's outage stops every service; a module upgrade must suit every service that uses it (N and N-1 per module) |

## Moving state between engines

**Backups.** `StateBackupJob` (`DbDataKit.State.Engines`) writes the same snapshot into `state-<UTC time>` folders of a directory, verified against its own manifest before the folder is named, the newest N kept; the console runs it as the system task `state-backup`. A restore is the import below into an empty database.

Moving is **offline**: the service is stopped (a restart is accepted, it is rare), the state is exported, imported into an empty database, verified and the settings are switched. The old database is left untouched, so going back is a settings change.

* **The unit is a snapshot** (`StateSnapshotWriter`, `StateSnapshotReader`): a directory with `manifest.json` (format `state-snapshot/1`: the modules with versions and checksums, per-table row counts and SHA-256 digests; written last, so a half-written directory has none), one gzipped JSON-lines file per table (`tables/<module>.<table>.jsonl.gz`, rows in the logical types) and the payload chunks as `payloads/<id>.bin`. It is readable, diffable and engine-neutral. The same format is the backup format.
* **Import goes into an empty target only** (a target holding any module is refused naming it): each module's migrations are applied up to the snapshot's version, then rows load in dependency order. A snapshot **newer than the binary** is refused naming the module; an older one is imported at its version and migrated up by the normal migrator.
* **Every move is verified** (`StateVerifier`): row counts and per-table digests computed in key order over the logical values, so two engines holding the same data compare equal; payload hashes; the first differences are reported.
* **Secrets stay secrets:** encrypted values (group secrets, signing keys) are copied as stored; the keys that decrypt them live in the secret store and are not part of a snapshot. `state export --include-secrets-check` searches a snapshot for the connection's resolved credentials.
* **A live instance refuses an export** (a `serving_instance` or runner `instance` row inside its lease, or a SQLite file held by another process), so there is nothing to capture while running.
* The console's own state is moved from the command line (it cannot stop itself); other servers' moves can be started from the console (`DbDataKit.State.Api`).
* **The old database is left behind** and the tool never offers to remove it; it prints how to go back (a settings change) and the administrator removes it. Native per-engine backups stay the administrator's tool: the snapshot is engine-neutral, not a backup of one engine's files.
* **Known limits:** a payload file's hash is checked while it is read on import, so a tampered payload is found after the modules before it committed (they stay, named, and `ResetOnFailure` removes them); a blob outside the payloads module is read as one array; the SQLite check for a file held open does not see a reader in WAL mode (the state is not in WAL); a decimal wider than .NET's 28 digits is not covered; identity columns of the older kind are reseeded on import.

Engine support, the features a module may need, rolling upgrades of a group and the settings order are in the `DbDataKit.State.Engines` README.
