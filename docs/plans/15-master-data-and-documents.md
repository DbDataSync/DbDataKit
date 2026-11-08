# Plan 15: DbDataStore, what is left

Status: **in progress**. What is built (the `model/1` format, rules, history, storage on SQL Server and PostgreSQL, the write path, change requests, publishing to the serving layer) is described in [DbDataStore/README.md](../../DbDataStore/README.md). This plan holds only unbuilt work and open questions. The slice list is plan 19's Z rows ([19-next-wave.md](19-next-wave.md)) and plan 20's `store` and `webdav` modules ([20-serving-layer.md](20-serving-layer.md)).

## Not built, from the original goals

| Item | What is left |
| --- | --- |
| Rules | none of the original rule kinds is missing: `no_overlap` and `no_cycle` are built (checked in `ModelWriter`) |
| Native temporal tables | SQL Server and MariaDB system-versioned tables as a second history implementation behind one conformance suite (as-of reads, history of a row, corrections, concurrent edits). Only the application-managed one exists, so there is nothing for the suite to compare |
| Entity roles | `role: dimension\|mapping\|calendar` is parsed and validated; nothing reads it. The semantic layer should use such an entity as a governed dimension, value map, bridge or calendar through the generated physical layer |
| Structure changes | the expand step is built (new tables, new nullable columns). The contract step is not: NOT NULL, drop, retype, and changes of history or instance mode, each a planned change with a copy of the data (renaming or retyping) or an approval (dropping) |
| Change events | not built: no event on an applied change (model, entity, change id) and no "on change" trigger next to cron in the scheduler, so a Sync or Build run cannot follow a change |
| Rules at proposal time | a change request's rules run at approval, not when it is proposed |
| Agent tools | model, row and change-request tools on the shared command surface (an agent proposes a model, proposes rows, fixes a validation error); the console's review screen |

## M4. Other engines

SQLite, then MariaDB, MySQL and Oracle, in that order (SQL Server and PostgreSQL are built). Each needs the shared pieces it lacks: the connection provider (SQLite is new; Sync's state database already uses `Microsoft.Data.Sqlite`), the `DbDataKit.Metadata` reader, the `DbDataKit.Schema` DDL for the dialect and a `DbDataKit.Transfer` bulk writer. The engine registry's "master data store" role is `planned` for each (`docs/engine-support.md`).

## M5. Documents and WebDAV

Design, kept as decided (served by the serving layer's `webdav` module, plan 20 O09):

- **A document store**: files with versions, owners, permissions (roles per folder from the console), metadata and tags. **Files are always stored in a database** (owner decision), on the same engines as the models: content in chunks in a binary column (`varbinary(max)`, `bytea`, `BLOB`), so a large workbook is read and written in pieces, with the version, hash and size beside it. Backups, permissions and replication are then the database's. Sync's planned user-provided files store (`DbDataSync/architecture/implementation/todo/phase-173V-user-provided-files-store.md`) and the console's Files page become this, rather than a second store.
- **WebDAV for Excel.** Office opens, locks, edits and saves a document over WebDAV (`PROPFIND`, `GET`, `PUT`, `LOCK`, `UNLOCK`, `PROPPATCH`) on HTTPS with Windows or Basic authentication. Library options found:

| Library | Licence | Office editing | Notes |
| --- | --- | --- | --- |
| Dav.AspNetCore.Server | open source | not documented | RFC 4918, ASP.NET Core, pluggable stores and lock managers (SQL Server, PostgreSQL, SQLite packages); last updated 2023 |
| NWebDav.Server | open source | not documented | ASP.NET Core variant; 0.2.0 beta since 2024, little activity |

  **Open source only** (owner decision: a commercial server is not an option). Prototype with Dav.AspNetCore.Server against real Excel (Windows and Mac) on the same VM as the Excel catalogue of plan 14 R7; NWebDav is the second candidate. Office's WebDAV behaviour (lock tokens, `OPTIONS` and `PROPFIND` probing, `MS-Author-Via`, authentication prompts) is where these servers fail, and only Excel shows it. Where the library falls short, fix it in our fork or upstream rather than switching to a commercial engine; record what Excel sends so the fixes have tests. The WebDAV listener is its own, off by default.
- **Every save is a version.** A `PUT` (or an upload from the console) creates a new version; nothing is overwritten.
- **Excel's WebDAV traffic is recorded for tests**, as for XMLA: on the Excel VM, a request-and-response recording of opening, locking, editing, saving and closing a workbook (Windows and Mac) becomes the fixtures the listener is tested against in CI.

## M6. Extraction on save

Design, kept as decided:

- **Extraction runs on save.** A document can have a saved extraction spec (`DbDataKit.Semantic.Extraction`: match, fingerprint, tables, checks against the sheet's own totals). Each new version is checked against the fingerprint; a sheet whose layout moved is reported (a notification through `DbDataKit.Monitoring`, with what moved), and its last good extraction stays in place. In the serving layer it runs in a child process (plan 20 O09).
- **Extracted tables land in a model by default.** An extraction writes into an entity of a master data model (with its rules and history), so what consumers read is an ordinary, validated table. This is the default, not the only destination: session-scope ad hoc tables stay for one-off questions.

## M7. Consumers and change events

| Consumer | How |
| --- | --- |
| DbDataSync | a source connection on the model's engine; history and row versions give incremental reads; the change event triggers a run |
| DbDataBuild | `project import` writes the entities as mapped models (keys, references as foreign keys); or an origin connection for copies; a refresh follows the change event |
| Serving layer | a data source on the model's connection, read-only like any other (entities as Flight SQL sources are plan 20 O08b) |
| Semantic queries | an entity marked as a dimension, mapping or calendar becomes a governed dimension, value map, bridge or weighted relation |

Also here: the change event itself (model, entity, change id) and the "on change" trigger. The service publishes its models through `DbDataKit.Metadata` records and the engine registry so every consumer sees the shape the service enforces.

## M8. Promotion

A temporary mapping table an agent or a user made for one semantic query (an ad hoc table, a value map, a bridge, a weighted relation) can be **promoted** into DbDataStore: the service creates an entity for it (keys and types from the table's descriptor, the map's from/to/weight columns), loads the rows, and from then on it is visible to others, edited in the grid, versioned, and read by later queries as a governed relation instead of a session one. The query that made it keeps working, now pointing at the managed entity. This is the main collaboration path between people and agents; the semantic scopes `shared` and `governed` become "promoted into DbDataStore". (Ad hoc data is not in the semantic layer's first release, so this waits on it.)

## Q4. The approval model, kept open

The mechanism is built (`approval: none|steward|owner`, `agents: request|direct`, a steward needing the author role, an owner the administrator role, an agent never deciding). What is **not decided** and is to be specified with the owner:

- who approves what, per model and per kind of change (row changes, structure changes, publishing, deleting a model);
- agents' writes beyond the one switch;
- emergency edits (a way to write now and have it reviewed after, and how that is recorded);
- what an approval records (who, when, the note, the version it applied to, and whether it is kept with the audit).

Permissions per entity are not in `model/1`; they belong with this question and with plan 22's grants.
