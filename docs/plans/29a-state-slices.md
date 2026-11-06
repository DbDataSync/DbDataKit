# Plan 29a: the slices of plan 29 (the state database rework)

Status: **proposed, being worked** (2026-11-06). This breaks [plan 29](29-unified-state.md) into pull requests, in the shape of
[plan 13a](13a-suite-slices.md): plan 29 says *why* and *what*; this says *in what pieces*, in what order, and what test proves each one.
The owner allowed breaking changes (2026-11-06), so every conversion is a **clean break** (plan 13 section 3): a database written by the
old structure is refused with a message naming it, never converted.

## Rules for every slice

- The rules of 13a apply: one small pull request, "done" is the test in the last column, CI green and independently revertable,
  behaviour changes named, docs and the status line in the same change.
- **Branch name** `suite/q<nn>-<short-name>`. (The first slices were worked on one session branch; the status column says where.)
- **Every slice runs on all three engines.** SQLite always; PostgreSQL and SQL Server through `testenv/` (`up mssql postgres`, then
  `eval "$(testenv/testenv.sh env dbdatakit)"`). A slice whose tests only ran on SQLite says so in its status, and is not "done" until CI
  has run the others.
- **Adapter first, delete second**: the old `StateSchema` path keeps working until the last product has moved (Q24), so each product is
  converted in its own slice and nothing in the middle leaves a product unable to start.
- **A module is added with its tests, its version table row and its retention rules** (Q05) or it is not done.

## Decisions made while planning (change them in the slice that is wrong)

| # | Decision | Why |
| --- | --- | --- |
| D1 | A module's **dependencies are object references** (`StateModule.DependsOn` holds the modules), not names | no cycles are possible, the order is a depth-first walk, and a missing dependency cannot be written |
| D2 | A template names a table as `{table:name}` (its own module) or `{table:module.name}` (a declared dependency's); a table is declared by being named, so there is no second list to keep in step | one place to say it; the layout finds every table by scanning the templates |
| D3 | An index is `{index:name}`, rendered with the namespace's **prefix** and no schema | SQLite index names are global to the file and PostgreSQL's live in the table's schema, so the prefix is what keeps them unique everywhere |
| D4 | `schema` and `prefix` settings may contain `{module}` and `{scope}`; a non-install scope with a setting that does not use `{scope}` gets the scope appended to the **default** only | the clash check then catches an administrator's own pattern that forgot it |
| D5 | The registry columns are `schema_name` and `prefix`, not `schema` | `schema` is a keyword on SQL Server; one spelling on every engine |
| D6 | The registry also carries `checksum`, the hash of the module's migrations applied so far | an edited migration that shipped is found at open, with the module and migration number, instead of two installs silently differing |
| D7 | Migrations are applied under a **database-wide lock** (`sp_getapplock`, `pg_advisory_lock`, SQLite's write lock) | two services starting together on one shared database must not both create the registry or run one migration twice |
| D8 | A module is **per scope**: install, group or service (`StateScopeKind`); the scope's name is part of the namespace | plan 29 section 3.2 |
| D9 | A module's tables are never altered by a product; a library adds a migration to its module | plan 29 section 3.1 |
| D10 | The registry is the only table outside a module. On SQL Server and PostgreSQL it is `dbdatakit.modules` (schema `dbdatakit`), on SQLite `dbdatakit_modules`; its schema and prefix are configurable (`registry` in the state section) | plan 29 section 3.1 says `dbdatakit_modules` in the `dbdatakit` schema; the table name inside that schema is `modules` so it is not said twice |
| D11 | `default` is the namespace settings' key for "every module", so no module is named `default` | one flat map in `server.yaml` |
| D12 | Retention deletes in one statement per rule (children first, in one transaction); batching a very large first purge is a later refinement | none of the first tables is large; add it with the `runs` module (Q12) if its first purge is |
| D14 | `definitions` and `app_storage` are kept per **group**, definition content stays opaque text (no `schema` column yet), and `definition_event` has no retention rule | a group serves its own definitions; the format of a definition is the kind's business until plan 30's registry of kinds; the log is both the poll feed and the record of who changed what a channel serves |
| D13 | A payload is verified (size, hash) when its reader reaches the end; a reader that stops early is not | a hash cannot be checked before the bytes have been seen |

## Milestones

| Milestone | Slices | What is true after it |
| --- | --- | --- |
| **Q-A: the mechanism** | Q01 to Q06 | `DbDataKit.State` can open a database from modules in namespaces, on three engines, with the registry, conventions, payloads, retention and the `state:` settings. No product uses it yet. |
| **Q-B: the shared modules** | Q07 to Q14 | notifications, app storage, definitions, identity, trust, scheduling, runs, audit and lineage exist as modules with their stores on `{table:}`. |
| **Q-C: the products move** | Q15 to Q19 | the console, the serving layer, the Build server and DbDataStore each start on modules; the interleaving constants are gone. |
| **Q-D: the new products** | Q20 to Q22 | the runner and its progress store; Build's `state`/`both` tracking; Sync. |
| **Q-E: operations and clean-up** | Q23 to Q25 | per-namespace rights, SQLite files per namespace, the old path deleted. |

## Q-A. The mechanism (`DbDataKit.State`)

| # | Slice | Needs | Size | Done when | Status |
| --- | --- | --- | --- | --- | --- |
| Q01 | **Modules, namespaces and `{table:}` rendering** (no database yet): `StateModule`, `StateScope`, `StateNamespaceSettings`, `StateLayout` (resolves every module in its scope to a namespace, orders by dependency, finds each table by scanning the templates, renders `{table:}`/`{index:}`, checks names for the engine's limits and for clashes) | none | M | unit tests: dependency order; defaults per engine and scope; `{module}`/`{scope}` placeholders; an invalid name, a name over 63 (PostgreSQL) or 128 (SQL Server), two namespaces rendering to one table, an undeclared cross-module reference: each stops with a message naming the setting or module | **done** (session branch; commit `State: plan 29a, and Q01`) |
| Q02 | **Registry and migrator** (built after Q03: the registry uses its tokens): `dbdatakit_modules`, applying each module's missing migrations in layout order under the migration lock, a module newer than the service refused (module and both versions), an edited shipped migration refused (D6), the **clean break** for a database with today's single version number; `StateDatabase` opens a `StateLayout`; `Command` renders `{table:}` | Q01 | M | on SQLite, PostgreSQL and SQL Server: a fresh database gets every module and its registry rows; reopening applies nothing; a module appended to is applied alone; two services opening at once apply once (concurrency test); an old `SchemaVersion` / `user_version` database is refused naming the database | **done on SQLite and PostgreSQL 16; SQL Server written, not run** (session branch) |
| Q03 | **Conventions**: tokens `{{uuid}}`, `{{time}}`, `{{json}}`, `{{bool}}`, `{{blob}}`; UUID v7 ids and UTC time helpers on `StateDatabase`; `row_version` update helper; a **convention test** over every module's templates (names lower snake case, ids and times by the tokens, `*_at` for times, no `NVARCHAR(MAX)`/`TEXT` in a key) | Q02 | S | the convention test runs over the registered modules and fails on a named table and column; round-trip tests of each token on all engines | **done on SQLite and PostgreSQL; SQL Server not run** (session branch) |
| Q04 | **Payloads** (plan 29 section 3.6): module `payloads` (`payload`, `chunk`), `PayloadStore.OpenWrite`/`OpenRead` as streams, size and SHA-256 checked on read, state *writing*/*complete*, owner (module and row), chunk size configurable (1 MiB) | Q03 | M | on all engines: a 20 MiB payload written and read through streams with peak memory of a few chunks (measured); a flipped byte is found on read; an incomplete payload is not readable; a payload larger than one chunk and one exactly a multiple of it round-trip | **done on SQLite and PostgreSQL; SQL Server not run** (session branch) |
| Q05 | **Retention**: modules declare `RetentionRule`s (table, time column, default days, owner-row link for payloads); one shared `RetentionJob` runs them; 30 days by default, in days and fractions, per module and per table; the audit has none until set; abandoned *writing* payloads and payloads whose owner is gone go | Q04 | M | tests with a fake clock on all engines: rows older than the setting go and newer stay; `0.25` is six hours; a module with no rule is untouched; an overridden table uses its own days; orphaned and abandoned payloads are removed | **done on SQLite and PostgreSQL; SQL Server not run** (session branch) |
| Q06 | **Settings and opener**: the `state:` section of `server.yaml` (`engine`, `connection`, per-namespace `schema`/`prefix`, `registry`, topology note), `StateOpener.Open(settings, modules, scope)`, strict parsing with the setting named in each message; the Engines README rewritten | Q02 | S | settings tests (defaults, overrides, an unknown module named in the section, a typo in a key); the opener test on all engines; docs updated | **done** (session branch) |

## Q-B. The shared modules (each: module, its stores on `{table:}`, tests on all engines, the old fragment kept until its product moves)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Q07 | **`notifications`** (feed and each reader's position) from `NotificationSchema` and `NotificationStore`; `service_id` on a shared database; retention 30 days | Q05 | S | `NotificationStoreTests` pass unchanged in meaning on the module; two services on one database each read their own and the console reads both | **Status: done on SQLite and PostgreSQL; SQL Server not run** (session branch). `NotificationsModule` and `NotificationFeedStore`; the old `NotificationStore` stays until its products move (Q15 to Q17). `MarkSeen` is one upsert that only takes the larger id (the old store read, then wrote). The dialects' `Upsert` gained `EXISTING.col` for the row already there. |
| Q08 | **`app_storage`** and **`definitions`** from `AppStorageSchema`, `DefinitionSchema`, `DefinitionStore`, `DefinitionFollower` | Q05 | S | the existing store tests pass on the modules. **Status: partly done** (session branch): `definitions` is done on SQLite and PostgreSQL (SQL Server not run): `DefinitionsModule` and `ModuleDefinitionStore` carry the old store's and follower's tests over; `DefinitionFollower` now reads an `IDefinitionSource`, which both stores are. `app_storage` is defined as a module (`AppStorageModule`, convention-checked) but `AppStorage` (in `Apps.Instances`) still uses the old table: it moves with the serving layer (Q16), where its host and tests are. Both modules are kept **per group**. The `schema` column plan 29 section 3.3 asks of definitions is **not added** (D14): content stays opaque text and the kind's format is plan 30's registry of kinds; the event log has no age limit (D14). |
| Q09 | **`identity`** (users, credentials, groups, roles, service accounts, OIDC clients and tokens, signing keys, sessions, invites, federation): the largest conversion; tables renamed to the conventions (ids UUID v7, times `*_at`) | Q05 | M, split in two (people; clients and tokens) | `IdentityStoreTests`, `AccountLinkingTests`, `OneTimeValuesTests` pass on the module on all engines |
| Q10 | **`trust`** (instance id, paired clients, pairing codes, the group's secrets and keys) from `GroupTrust` and Proxy's group tables | Q09 | S | the pairing tests pass on the module |
| Q11 | **`scheduling`** (schedules, job state, pauses, work queue, leases, run locks) from the Build server's and Sync's copies | Q05 | M | the queue's cross-engine tests (one in-flight row per mapping) pass on the module, with the partial-index predicate |
| Q12 | **`runs`** (`DbDataKit.Monitoring`): the one run model, outcome, counts, classified failure, logs | Q11 | M | a Build run and a Sync run read back through one query; logs paged by id |
| Q13 | **`audit`** (new, shared; kept until a limit is set) from Proxy's management audit | Q05 | S | who/what/through-what recorded and read back; no retention rule by default (test) |
| Q14 | **`lineage`** from `LineageSchema` | Q05 | S | the lineage store tests pass on the module |

## Q-C. The products move (one clean break each; the old database is refused with a message)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Q15 | **Console** on modules (`identity`, `notifications`, console settings) | Q07, Q09 | M | the console's tests pass; a pre-rework database is refused naming the file |
| Q16 | **Serving layer / Proxy**: its tables become modules; `HomeTemplatesBeforeGroup`, `...BeforeTrust`, `...BeforeSecrets` are deleted | Q08, Q09, Q10, Q13, Q15 | M | the Proxy suites pass; the three constants are gone (a test greps for them) |
| Q17 | **Build server** (work queue, schedule jobs, pauses) on `scheduling` | Q11 | S | the server's tests pass |
| Q18 | **DbDataStore** pairings from `server.yaml` into `trust` | Q10 | S | pairing tests; the old `server.yaml` pairing keys fail with a message naming the file |
| Q19 | **Console shows every service's runs, audit and notifications** from one database when the install shares one (topology C) | Q12, Q13, Q15 | M | a console and two services on one database: one query for runs; one service alone: the pulled view as today |

## Q-D. The new products and the follower pattern (these wait on plans 27 and 28)

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Q20 | **`runner` and `runner_progress`**: checkpoints with history and `row_version`, typed state values, file ledger, idempotency keys, dead letters, inbox; the declared-progress migration planner (plan 29 section 4.3) | Q04, Q12, plan 28 | M, split | the checkpoint-moves-with-the-outcome test (a failed run leaves it, a second overlapping run is refused); each declaration change of section 4.3's table has a test |
| Q21 | **`build_tracking` and the follower** (`target`, `state`, `both`): copy by checkpoint, divergence detected, identity of the connection | Q20 | M, split | a restored target is marked diverged; a development copy's rows stay under its own identity; a down copy never slows a deploy |
| Q22 | **Sync** on the modules, last, with plan 28 section 8 | Q20, Q21 | L, split by its own plan | Sync's integration suites on all engines |

## Q-E. Operations and clean-up

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Q23 | **Rights per namespace**: `dbdatakit state grants` prints (or applies) the `GRANT`s a service's login needs for the namespaces it uses | Q06 | S | a login granted only its schemas can open its modules and cannot read another's (SQL Server and PostgreSQL) |
| Q24 | **SQLite files per namespace** (`ATTACH`), for the modules that write heavily; modules that commit together stay in one file | Q06 | S | two namespaces in two files; a write to one does not wait on a long write to the other; a module group that must commit together is refused if split |
| Q25 | **Delete the old path**: `StateSchema`, `SchemaVersion`, `user_version` handling, the per-product `Migrations` lists | Q15 to Q22 | S | nothing references them; the schema tests are the module tests |

## What can start now

Q01 first (nothing else needs anything but it), then Q02, then Q03 to Q06 in any order as far as their "Needs" allow.
Q07 to Q14 are independent of one another once Q05 is done and can go in parallel.

## Working notes (updated as slices land)

- **2026-11-06, the sandbox has no Docker.** PostgreSQL slices were tested on a local PostgreSQL 16 (the repository's engine is 17 with
  `pgvector`; nothing here uses a 17-only feature). SQL Server code is written and has not been run: its first run is CI or the owner's
  `testenv` with the upgraded SQL Server 2025 image.
