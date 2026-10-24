# Plan 15: DbDataStore, a master data and document service

Status: **proposed for review** (2026-10-24), from the owner's long-term direction. **Not part of plan 13's work list.** Name
**DbDataStore** (`dbdatastore`), chosen by the owner. Builds on plan 13 (the console, server shape, trust, shared libraries) and
plan 14 (the semantic layer, extraction).

## 1. What it is for

The owner's goals:

1. **Data models that users and agents define**, so that users and agents manage the data in them: reference lists, mappings,
   hierarchies, targets and budgets, territory assignments, the tables that today live in spreadsheets on someone's drive.
2. **Stored on common SQL engines**: SQL Server, PostgreSQL, SQLite, MariaDB, MySQL, Oracle, probably in that order.
3. **User-managed documents**, such as Excel workbooks, ideally with **WebDAV** so Excel opens, edits and saves them in place.
4. **Data extracted from those documents** and readable like any other master data model or ad hoc source.
5. **Consumed by Sync, Build and semantic queries.**

It fills the one gap left in the suite's flow: data that *people* own and change, rather than data replicated, built or queried.

```
  people, agents ──► DbDataStore ──► master data tables (SQL Server, PostgreSQL, SQLite, ...) ──► Sync, Build, Proxy, semantic queries
  Excel (WebDAV) ──► documents ──► extraction ──┘
```

## 2. Principles

- **Master data is ordinary tables on an ordinary engine.** Every consumer reads it with what it already has (a connection, a mapped
  model, a data source). There is no special read protocol and no proprietary store between the data and its users.
- **Writes go through the service; reads do not have to.** Validation, history, approval and audit live in the service's write path.
  Readers use the tables directly or through Proxy, read-only.
- **User data is never broken.** Plan 13's clean break applies to the suite's *own* configuration, not to the data people put in
  models: a change to a model's structure keeps its rows, or is refused, or is an explicit, approved migration.
- **Agents propose, people approve** where the model says so: an agent's change is a change request until someone with the right role
  accepts it.
- **Same shape as the other products**: a server with a management API, paired with consoles (plan 13 W12), its own CLI on the shared
  nouns, a console module, the shared state store, secrets, monitoring and certificates.

## 3. Data models

**A model** is a set of **entities** (tables) with **attributes** (columns), keys, references between entities, and rules. It is
written in YAML (`model/1`, strict, through `DbDataKit.Config.Yaml`), kept in the service's git-tracked config directory, and can be
created and edited from the console, the CLI or an agent tool.

```yaml
schemaVersion: 1
name: sales_territories
title: Sales territories
storage: {connection: mdm_sql}          # a connection/1 definition
approval: {changes: steward}            # who must approve row changes: none | steward | owner
entities:
  territory:
    key: [territory_code]
    attributes:
      territory_code: {type: VARCHAR(10), required: true, pattern: "^[A-Z]{2,4}-[0-9]{2}$"}
      name:           {type: VARCHAR(100), required: true, unique: true}
      region:         {type: VARCHAR(40), references: {model: crm_reference, entity: region}}
      active:         {type: BOOLEAN, default: true}
    history: effective_dated            # none | audit | effective_dated
  territory_share:
    key: [territory_code, region]
    attributes:
      territory_code: {type: VARCHAR(10), references: {entity: territory}}
      region:         {type: VARCHAR(40)}
      share:          {type: DECIMAL(5,4), min: 0, max: 1}
    rules:
      - {kind: sum_equals, attribute: share, group_by: [region], value: 1, tolerance: 0.0001}
```

- **Types** are `DbDataKit.Dialect` canonical types, the same names Build uses, written as each engine's own type on deploy.
- **Rules**: required, unique, default, ranges, patterns, allowed values (or a reference to another entity), references, and a small
  closed set of cross-row rules (a sum per group, no overlapping effective dates, a hierarchy without cycles). Each is checked on write,
  and reported with the field and the row, in the suite's error shape.
- **History**: `none`, `audit` (who changed what and when), or `effective_dated` (versioned rows with valid-from and valid-to; a change
  closes the old version). The owner wants versioned, effective-dated records and accepts either implementation: **native temporal tables**
  where the engine has them (SQL Server and MariaDB system-versioned tables) or an **application-managed** version of the same concept. One
  conformance suite defines the behaviour (as-of reads, history of a row, corrections, concurrent edits) and both implementations must pass
  it; the application-managed one is the baseline because it works on every engine, and an engine's native tables are used only where they
  pass the same tests.
- **Semantics for the semantic layer**: an entity can say it is a dimension (with a hierarchy over its attributes), a mapping (a value
  map or bridge: from, to, optional weight), or a calendar. That is what lets the semantic layer use it without extra binding (section 7).

## 4. Storage on SQL engines

**In the owner's order**: SQL Server, PostgreSQL, SQLite, MariaDB, MySQL, Oracle.

| Need | From the suite | Gap |
| --- | --- | --- |
| Connections, logins, TLS, failure model | `DbDataKit.Connections` (SQL Server, PostgreSQL today) | SQLite, MySQL/MariaDB and Oracle providers (plan 13 A09 adds MySQL and Oracle; SQLite is new, Sync's state database already uses `Microsoft.Data.Sqlite`) |
| DDL per engine | `DbDataKit.Dialect` (types, quoting); Build's target rendering; Sync's provisioning for its targets | `ISqlDialect` has no DDL fragments yet (`CreateTable`, add, alter and drop column, constraints, indexes; STATUS.md lists this as stubbed) |
| Knowing what is there | `DbDataKit.Metadata` readers (tables, columns, keys, foreign keys, indexes) | MySQL, Oracle and SQLite readers |
| Changing a structure without losing rows | Build's planner: compares the declared shape with the live one, classifies each change by risk, writes a plan, applies only what was approved | it is built around Build's SELECT-defined models |
| Bulk writes (imports, extraction) | `DbDataKit.Transfer` writers (SQL Server bulk copy, PostgreSQL COPY), the write gate with its statement log | MySQL, Oracle, SQLite writers |
| Engine registry | `engine/1` descriptors (plan 14 R5): "master data store" is one capability per engine | |

**Structure changes. Decided (owner): Build's table-structure planner moves into the shared code base** as `DbDataKit.Schema`
(declared shape against the live catalog, a change list with risk classes, a plan, apply through the write gate), used by Build and by
this service. Adding an attribute or relaxing a rule is safe and applied directly; renaming or retyping an attribute is a planned change
with a copy of the data; dropping one is destructive and needs approval. The first step is reading how Build's planner separates the
table structure from its SELECT-defined models, and moving that part with Build's own tests, so Build's behaviour is unchanged.

**One database per model or one for many** is a deployment choice: a model names its connection, and several models can share one
connection with a schema each.

## 5. Writing data

- **The API.** Row create, update, delete and bulk upsert through the service's management API (plan 13 W12 tokens, roles from the
  console): every write is validated, versioned (optimistic concurrency: a write names the row version it read and is refused if it
  changed, as plan 13 T08 does for config), recorded in history and audited with the user and the console.
- **Change requests.** When a model requires approval, a write becomes a change request (a set of row changes with a description),
  visible in the console, and applied in one transaction when approved. Agents' writes are always change requests unless the model's
  owner allows otherwise.
- **The console module.** Model designer (entities, attributes, rules, with the YAML beside it), a grid editor with validation as you
  type, imports (CSV, Excel, through extraction), change requests and their review, history of a row, and the model's consumers (which
  Sync replications, Build models and semantic models read it).
- **Agents.** Model, row and change-request tools on the shared command surface (plan 13 W4a): an agent can propose a new model, propose
  rows, or fix a validation error, and a person approves.
- **Change events.** Every applied change publishes an event (the model, the entity, the change id); Sync and Build can trigger on it
  (plan 13 W10 gains an "on change" trigger next to cron), so a refresh follows a change instead of a schedule.

## 6. Documents

- **A document store**: files with versions, owners, permissions (roles per folder from the console), metadata and tags. **Files are always
  stored in a database** (owner decision), on the same engines as the models: content in chunks in a binary column (`varbinary(max)`,
  `bytea`, `BLOB`), so a large workbook is read and written in pieces, with the version, hash and size beside it. Backups, permissions and
  replication are then the database's, like the rest of the data. Sync's planned user-provided files store (`DbDataSync/architecture/implementation/todo/phase-173V-user-provided-files-store.md`) and the console's
  Files page become this, rather than a second store.
- **WebDAV for Excel.** Office opens, locks, edits and saves a document over WebDAV (`PROPFIND`, `GET`, `PUT`, `LOCK`, `UNLOCK`,
  `PROPPATCH`) on HTTPS with Windows or Basic authentication. Library options found:

| Library | Licence | Office editing | Notes |
| --- | --- | --- | --- |
| Dav.AspNetCore.Server | open source | not documented | RFC 4918, ASP.NET Core, pluggable stores and lock managers (SQL Server, PostgreSQL, SQLite packages); last updated 2023 |
| NWebDav.Server | open source | not documented | ASP.NET Core variant; 0.2.0 beta since 2024, little activity |

  **Open source only** (owner decision: a commercial server is not an option). Prototype with **Dav.AspNetCore.Server** against real
  Excel (Windows and Mac) on the same VM as plan 14's Excel catalogue; NWebDav is the second candidate. Office's WebDAV behaviour (lock
  tokens, `OPTIONS` and `PROPFIND` probing, `MS-Author-Via`, authentication prompts) is where these servers fail, and only Excel shows
  it. Where the library falls short, fix it in our fork or upstream rather than switching to a commercial engine; record what Excel sends,
  as for XMLA, so the fixes have tests. The WebDAV listener is its own, off by default.
- **Every save is a version.** A `PUT` (or an upload from the console) creates a new version; nothing is overwritten.
- **Excel's WebDAV traffic is recorded for tests**, as plan 14 does for XMLA: on the Excel VM, a request-and-response recording of opening,
  locking, editing, saving and closing a workbook (Windows and Mac) becomes the fixtures the WebDAV listener is tested against in CI.

## 7. Extraction and consumption

- **Extraction runs on save.** A document can have a saved extraction spec (plan 14's QIS extraction: match, fingerprint, tables, checks
  against the sheet's own totals). Each new version is checked against the fingerprint; a sheet whose layout moved is reported (a
  notification through `DbDataKit.Monitoring`, with what moved), and its last good extraction stays in place.
- **Extracted tables land in a model by default.** An extraction writes into an entity of a master data model (with its rules and
  history), so what consumers read is an ordinary, validated table. This is the default, not the only destination: session-scope ad hoc
  tables (plan 14) stay for one-off questions, and other destinations can be added when a case needs one.
- **From a query to managed data** (collaboration). A temporary mapping table an agent or a user made for one semantic query (an ad hoc
  table, a value map, a bridge, a weighted relation; plan 14) can be **promoted** into DbDataStore: the service creates an entity for it
  (keys and types from the table's descriptor, the map's from/to/weight columns), loads the rows, and from then on it is visible to others,
  edited in the grid, versioned, and read by later queries as a governed relation instead of a session one. The query that made it keeps
  working, now pointing at the managed entity. This is the main collaboration path between people and agents, and QIS's `shared` and
  `governed` scopes become "promoted into DbDataStore".
- **Consumers read tables**:

| Consumer | How |
| --- | --- |
| DbDataSync | a source connection on the model's engine; history and row versions give incremental reads; the change event can trigger a run |
| DbDataBuild | `project import` writes the entities as mapped models (keys, references as foreign keys); or an origin connection for copies; a refresh can follow the change event |
| DbDataProxy | a data source on the model's connection, read-only like any other |
| Semantic queries (plan 14) | an entity marked as a dimension, mapping or calendar becomes, through the generated physical layer, a governed dimension, value map, bridge or weighted relation; QIS's `governed` scope for an ad hoc map (plan 14 R4) means *promoted into a master data model* |

- **Metadata.** The service publishes its models through `DbDataKit.Metadata` records (and the engine registry), so Build, Proxy and the
  semantic layer see the same shape the service enforces.

## 8. Reuse and gaps, in one table

| Piece | Reused from | New |
| --- | --- | --- |
| Server, service install, certificates, pairing, roles | plan 13 (Hosting, W12, console) | |
| Model files, validation messages | `Config.Yaml`, the suite's error shape | the `model/1` format and its rules |
| Connections, metadata, types | `Connections`, `Metadata`, `Dialect` | SQLite, MySQL/MariaDB, Oracle coverage |
| Structure changes | Build's planner, extracted (`DbDataKit.Schema`) | the extraction itself |
| Writes, bulk loads, statement log | `Transfer`, the write gate | MySQL, Oracle, SQLite writers |
| History | Sync's SCD2 experience (a design reference, not code) | history tables, effective dating |
| Change events, notifications, schedules | `Monitoring`, `Scheduling` (plan 13 W10) | the "on change" trigger |
| Documents | Sync's planned files store (one store, not two) | versions, permissions, WebDAV |
| Extraction | QIS extraction (plan 14) | running on save, landing in entities |
| Agent tools | the shared command surface (plan 13 W4a) | model, row and change-request tools |
| Console | the shared web packages (plan 13 W5) | model designer, grid editor, change review |

## 9. Decisions and open questions

| # | Question | Status |
| --- | --- | --- |
| 1 | Name | **decided**: DbDataStore (`dbdatastore`) |
| 2 | Structure changes | **decided**: Build's planner moves into `DbDataKit.Schema` |
| 3 | History | **decided**: versioned, effective-dated records; native temporal tables or application-managed, behind one conformance suite (section 3) |
| 4 | Approval model | **open**: to be specified together with the owner (who approves what, per model and per kind of change; agents' writes; emergency edits; what an approval records) |
| 5 | WebDAV | **decided**: open source only; Dav.AspNetCore.Server first, NWebDav second; Excel's traffic recorded as fixtures |
| 6 | Document storage | **decided**: always in a database |
| 7 | Extraction results | **decided**: into a model entity by default, not the only destination |
| 8 | Engine order | **decided**: SQL Server, PostgreSQL, SQLite, MariaDB, MySQL, Oracle, unless something comes up |
| 9 | Where DbDataStore is served from | **decided direction**: its own server, hosting Proxy's protocols as shared code (section 11) |

## 10. Suggested order, once approved

After plan 13's M3 (the console manages all products) and with plan 14's semantic packages:

| Step | What |
| --- | --- |
| M1 | The server skeleton on plan 13's shape; `model/1` and its validation; SQL Server and PostgreSQL storage for new models (create only) |
| M2 | Row writes with rules, history and audit; the console grid editor; agent tools as change requests |
| M3 | `DbDataKit.Schema` from Build's planner; structure changes with plans and approval |
| M4 | SQLite; then MariaDB, MySQL and Oracle, in that order (with the shared providers, readers and writers they need) |
| M5 | The document store in the database, with versions; WebDAV with Dav.AspNetCore.Server, tested against Excel's recorded traffic |
| M6 | Extraction on save into entities; fingerprint drift notifications |
| M7 | Consumers: change events and the "on change" trigger for Sync and Build; Build import of entities; semantic dimensions, maps and calendars from entities |
| M8 | Promotion of a query's temporary mapping tables into managed entities (section 7) |
| M9 | `DbDataKit.Serving.FlightSql` out of Proxy (no behaviour change); DbDataStore serves reads of its entities over it; validated writes over it after that |

## 11. Hosting: DbDataStore serves itself, with Proxy's protocols as shared code

The owner may later want DbDataStore's queries and updates served over the same protocols as Proxy's. The options considered:

| Option | For | Against |
| --- | --- | --- |
| A. Its own server; reads through Proxy | Proxy stays read-only | every DbDataStore install needs a Proxy to be read over Flight SQL, and two products to configure for one model |
| B. Proxy hosts DbDataStore | one endpoint for reading and writing managed data | overloads Proxy's primary function (serving read-only queries) with a write path every Proxy install and review would carry |
| **C. Proxy's protocols as shared code; DbDataStore hosts them itself** (owner's suggestion) | each product keeps one job; no product depends on the other to serve its data; one implementation of each protocol | the protocol libraries must stay free of either host's logic |

**Decided direction: C.** The protocol front ends move into shared libraries (`DbDataKit.Serving.FlightSql`, `.PostgresWire`, later
`.Xmla`; plan 14 R8), written against a host interface: authenticate a caller, describe what can be queried, run a query under the
caller's limits, and accept writes only if the host implements them. Then:

- **`dbdatastore server`** hosts Flight SQL (and Postgres wire when it exists) for **reads of its entities**, as-of reads of their history
  included (`FOR SYSTEM_TIME AS OF` in the query text or an option, mapped to the history design of section 3), and WebDAV for documents.
  Its management API (plan 13 W12) carries model, row and change-request operations.
- **Writes over a protocol are DbDataStore's choice, behind its own write path.** Flight SQL's update and ingest commands (and Postgres
  `INSERT`/`UPDATE` later) can be accepted for entities, translated into the same validated, versioned, audited writes (or change
  requests) as the API's. A protocol never writes a table directly.
- **Proxy stays read-only** and is not needed to read master data. It can still serve DbDataStore's tables as an ordinary read-only data
  source when someone wants one endpoint for everything, and semantic queries (plan 14) still go through Proxy, reading DbDataStore entities
  as dimensions, maps and calendars.
- **The DbDataStore logic** (models, validation, history, change requests, documents, extraction) stays in its own libraries
  (`DbDataKit.Store.*`) with a thin server, like the other products.

What this changes elsewhere: moving the Flight SQL server out of `DbDataProxy.Server` into `DbDataKit.Serving.FlightSql` is a refactor of
Proxy with no behaviour change (its tests and metadata goldens unchanged), and is the first step of plan 14 R8 either way.
