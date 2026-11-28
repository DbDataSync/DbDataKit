# Parked work

Work that is not scheduled, each with its whole design: what it does, how, why, and why it is parked. Nothing parked is kept in a subject file, so a
subject can close without it. The owner un-parks an item; its section then moves into a subject as an open design or as tasks.

## Refinements of the navigation

whether a tab is always in the address (as `?tab=` is for Agents, Knowledge and Delivery but not for a workspace except `runs`), and how a workspace's up to 11 tabs are grouped (edit, check, run, publish); after the owner has used the navigation.

## Data transfer

a shared transfer verifier, Sync on the write gate, Arrow-first and Parquet staging (waits on a benchmark), MySQL and Oracle writers, SCD2 and Snapshot, segmenting in shared packages, the Windows access list on the statement log (`Shared/src/DbDataKit.Transfer/README.md` says what is built and the rules that order this work).

## MDX, XMLA and the extra protocols

Excel PivotTables over semantic models, XMLA sign-in, Entra tokens for Power Query, the PostgreSQL wire protocol and Quack (ENDPOINTS-6 only reserves the listener). Moved from SEMANTICS' open designs.

The protocols are low priority (owner, 2026-11-09), come in the order given below, and the Excel work needs the owner's Windows VM with Excel before
any code that talks to Excel.

### MDX and XMLA for Excel PivotTables

**What.** An Excel PivotTable on an OLAP source connects with the Analysis Services provider over XMLA (SOAP over HTTP or HTTPS): `Discover` for schema
rowsets, `Execute` for MDX, sessions, cell sets. Excel generates a narrow, recognisable set of MDX shapes (`NON EMPTY
Hierarchize(AddCalculatedMembers(DrilldownLevel({...})))` on axes with `DIMENSION PROPERTIES`, filters as a sub-select or a `WHERE` slicer,
drill-down, member searches). No .NET XMLA server exists to reuse.

**How, as decided.** Translate only the MDX Excel sends into semantic requests; never build a general MDX engine. An MDX parser for the subset works on
the parsed tree, never on text, and produces the axes, the slicer and the sub-select filters, then one semantic request per cell set, then the result
reshaped into the cell set Excel expects. Anything outside the subset is an XMLA error with a clear message, logged with the request's shape (values
removed), never answered approximately. Other clients are out of scope until asked for.

- **Excel's own traffic is the specification and the CI fixture.** On a Windows VM with Excel, the forwarder (`dbdata-xmla-forwarder`) records Excel's
  requests against the QIS Tabular model while a script of PivotTable actions runs by hand (fields on rows, columns, values and filters; expand and
  collapse; sort; filter by label and value; search; refresh; totals on and off; each available Excel version). The recordings become golden fixtures
  (under the Shared tests' `xmla/` folder); in CI each recorded request goes to the XMLA endpoint and to SSAS, and the two cell sets must agree. A new
  Excel version is a new recording.
- **The order.** (1) `Discover` rowsets so Excel lists the fields (`DISCOVER_PROPERTIES`, `DBSCHEMA_CATALOGS`, `MDSCHEMA_CUBES`, `_DIMENSIONS`,
  `_HIERARCHIES`, `_LEVELS`, `_MEASURES`, `_MEMBERS`, `_PROPERTIES`, `_SETS`) and sessions, as an `xmla` serving module (a line of `modules:`); (2) rows,
  columns and values with subtotals and grand totals; (3) filters, drill-down and sorting; (4) member search, named sets and measures Excel calculates
  itself. Write-back, drill-through and user-defined calculated members are out of scope.
- **Model additions MDX needs.** An `All` member and member unique names per hierarchy, measure groups, display folders and format strings on measures:
  optional keys of the semantic package's format 2, refused with file and line when wrong, and refused by an instance that reads only format 1.
- **Sign-in.** XMLA is served over HTTPS only, with Windows (Negotiate) or the identity home's user name and a personal access token, resolved to the
  serving layer's identities so every PivotTable query carries a person. HTTP (msmdpump) is not used by Excel and native TCP is not built. A wrong
  token is an XMLA error, not a hang.
- **Access rules.** The translators produce semantic requests, so row and column rules apply through the planner; the module joins the access
  conformance suite when it exists.

**Open questions.** The VM and its recording session (the owner's). The risk is MDX scope creep: the subset is what the recordings show.

### The PostgreSQL wire protocol

**What.** A `pgwire` serving module: PostgreSQL wire v3 (simple and extended query, SCRAM-SHA-256, TLS, cancel). No .NET PostgreSQL server library
exists, so the protocol is the suite's own. Like every protocol it is a front end to the one query core: it turns wire messages into a semantic
request, maps its sign-in onto the serving layer's identities and returns rows; it never opens a connection, applies a limit or formats an error itself.

**How, as decided.**

- **Semantic SQL only, never pass-through:** a PostgreSQL client writing PostgreSQL SQL against a SQL Server source would be a dialect mismatch.
- **Flat names:** every member also has a flat column name (`customer_region`; the separator configurable; `flat_name` resolves a collision, and a
  collision without it is a load error naming both members): optional keys of the semantic package's format 2.
- **`pg_catalog` and `information_schema` are emulated tool by tool**, each against recorded fixtures: `psql` and a Python client first (they need the
  least of `pg_catalog`), then Power BI Desktop's PostgreSQL connector.
- **A compatibility layer** reads a tool's SQL as a strict semantic request (an aggregate over a measure column as `MEASURE()`, an aggregate that
  contradicts the measure refused with a message). It is separate from the semantic SQL specification, in its own document versioned on its own with
  per-tool fixtures, and every translated query is shown as the canonical semantic SQL it became.
- **Saved queries on the wire:** a `semantic` schema with a saved query as a view, and a function whose arguments are prepared-statement parameters;
  the same saved query returns the same rows over Flight SQL and this endpoint.
- The serving contract gains a multi-leg authentication exchange and cancel by session. ENDPOINTS-6 reserves the listener.

**Open questions.** Which tool after Power BI; the compatibility layer's document format (registered in `docs/formats.json` when it is built).

### Quack (DuckDB's client-server protocol)

Waits for DuckDB to publish a server specification and a stability promise. The cheaper route then may be a DuckDB child process serving Quack over
views of semantic results, behind the serving layer's identities.

### Entra tokens for Power Query

For an install with Entra configured, the feeds, saved queries and OData endpoints answer an unauthenticated call with a bearer challenge naming the
tenant and accept that tenant's access tokens for the configured resource, the person linked to their home account by UPN; nothing changes when Entra
is not configured. Proven with a stand-in tenant (the challenge names the tenant; a token for another audience or tenant is refused) and a real Power
Query refresh recorded on the Excel VM. Open question: whether it waits for the Excel work or comes first, since it serves Power Query rather than
PivotTables.

## Ad hoc semantic registrations

registrations and their review, and the combine in a child process, until an app or an agent needs them. Moved from SEMANTICS' open designs.

### Ad hoc registrations, and the combine in a child process

Not yet (decided 2026-11-09, the owner delegated): they wait until an app or an agent needs them. **Registrations:** a state module `semantic_adhoc`
in the Gateway holds a person's registered tables (with descriptors), bindings and value maps, at the scopes `session` and `personal` (per user), with a
row cap and expiry, served as the request kinds that need them; `shared` needs an owner's approval and `governed` is a commit to the Build project, so
neither is stored there. The documents used are those of `docs/semantic/combine.md` as they are. **A screen** lets a person upload and list their
registrations; a proposal to share goes to the model's owner, who approves or rejects with a reason, and an approved map is offered as a change to the
Build project (the Store's promotion of a value map is MASTERDATA's open design). **The combine in a child process** is taken only if the in-process
limits prove too soft (a recorded incident or a load test beating `memory_mb`): a combine beyond its memory limit stops its own process and the Gateway
answers `ResourceExhausted` while another query on the same instance finishes.

## Documents and WebDAV

the document store, extraction on save, WebDAV and its sign-in; with them the Store's other engines (SQLite, MariaDB, MySQL, Oracle), promotion of ad hoc tables, native temporal tables and the approval model. Moved from MASTERDATA's open designs. The old Store plan (15) kept these as decided design.

### Documents and WebDAV

**What.** A document store served by the Gateway's `webdav` module: files with versions, owners, permissions (roles per folder from the console),
metadata and tags. **Files are always stored in a database**, on the same engines as the models: content in chunks in a binary column (`varbinary(max)`,
`bytea`, `BLOB`), so a large workbook is read and written in pieces, with the version, hash and size beside it; backups, permissions and replication are
then the database's. **Every save is a version:** a `PUT` or an upload from the console makes a new version, nothing is overwritten.

**WebDAV for Excel.** Office opens, locks, edits and saves a document over WebDAV (`PROPFIND`, `GET`, `PUT`, `LOCK`, `UNLOCK`, `PROPPATCH`) on HTTPS.
**Open source only** (a commercial server is not an option): prototype with Dav.AspNetCore.Server (RFC 4918, ASP.NET Core, pluggable stores and lock
managers), NWebDav.Server second; where a library falls short, fix it in a fork or upstream rather than switching to a commercial engine. Office's WebDAV
behaviour (lock tokens, `OPTIONS` and `PROPFIND` probing, `MS-Author-Via`, sign-in prompts) is where such servers fail, and only Excel shows it, so
Excel's WebDAV traffic (opening, locking, editing, saving and closing a workbook, on Windows and Mac) is recorded on the Excel VM as the fixtures the
listener is tested against in CI. The WebDAV listener is its own and off by default.

**Sign-in.** Office's forms-based sign-in (MS-OFBA) leads to the identity home's sign-in page and the session it ends with is accepted on the WebDAV
requests; Windows; and the home's user name with a personal access token as the password. Office for Mac checked on the Excel VM.

**Open questions.** Whether the console's file uploads move to this store; the store's own format (registered when it is built).

### Extraction on save

A document can have a saved extraction spec (`DbDataKit.Semantic.Extraction`: match, fingerprint, tables, checks against the sheet's own totals). Each
new version is checked against the fingerprint; a sheet whose layout moved is reported (a notification with what moved) and its last good extraction
stays in place. Extraction runs in a child process of the Gateway. Extracted tables land in an entity of a Store model by default (with its rules and
history), so what consumers read is an ordinary, validated table; session-scope ad hoc tables stay for one-off questions. **Open question:** the
extraction spec's format (registered in `docs/formats.json` when this is sliced).

### Promotion of ad hoc tables and agent-built value maps

A temporary mapping table an agent or a person made for one semantic query (an ad hoc table, a value map, a bridge, a weighted relation) can be
**promoted** into the Store: an entity is created for it (keys and types from the table's descriptor, a map's from, to and weight columns), the rows are
loaded, and from then on it is visible to others, edited in the grid, versioned, and read by later queries as a governed relation. The query that made
it keeps working, now pointing at the entity. This is the main collaboration path between people and agents; the semantic scopes `shared` and
`governed` become "promoted into the Store".

When a semantic query joins sources whose values do not match (`NE` against `Northeast`), an agent builds the map while answering: it proposes the map
as a Store draft and the query uses that draft; the person approves each step, as with every agent action; the map can later be promoted. Tested with
the scripted fake model: a conversation hits the mismatch, the agent proposes a draft map, the person approves and the query reruns with matching
values; a denied map leaves the query unanswered and nothing stored. **Open question:** it waits for ad hoc registrations in the semantic layer, which
are parked.

### The Store's other engines

SQLite, then MariaDB, MySQL and Oracle (SQL Server and PostgreSQL are built). Each needs the shared pieces it lacks: the connection provider, the
`DbDataKit.Metadata` reader, the `DbDataKit.Schema` DDL for the dialect and a `DbDataKit.Transfer` bulk writer. The engine registry's "master data store"
role is `planned` for each.

### Native temporal tables

SQL Server's and MariaDB's system-versioned tables as a second history implementation behind one conformance suite (as-of reads, the history of a row,
corrections, concurrent edits). Only the application-managed history exists, so there is nothing yet for the suite to compare.

### The approval model, beyond what is built

The approval mechanism and its default are described in `DbDataStore/README.md`. Still to be specified with the owner: who approves what per kind of
change (row changes, structure changes, publishing, deleting a model); agents' writes beyond the one switch; emergency edits (write now, reviewed after,
and how that is recorded); what an approval records (who, when, the note, the version it applied to, whether it is kept with the audit); permissions per
entity, which are not in `model/1`.

## Backlog and custom attributes

the backlog's serving module, links, notifications, automatic items, the assistant; the console's Backlog screen stays as built. **Custom attributes** (the old EA rows): extension models keyed by an object's identity, to be re-planned on the Gateway's store module. Moved from the subject file BACKLOGS, which held only open designs.

### What it is for

Backlog, sprint and work management for teams that build on the suite, where a work item can point at the thing it is about; and custom attributes on
any object type, kept in the Store. Parked (owner, 2026-11-14): future work, not scheduled; what is built stays. These are its open designs, so the
design is not lost while it waits.

### What exists today

- `DbDataKit.Backlog` (`Shared/src/DbDataKit.Backlog/README.md`): work items, epics, sprints, boards, comments, links and attachments on the
  application's own structure, the state module `backlog`; the default workflow and its rules.
- The console serves the library at `/api/backlog` and shows it on its Backlog screen (backlog, board, sprints, the item page); no browser story.
- No serving module, no links' effects on other products' pages, no notifications, no automatic items, no assistant, no custom attributes.

### The backlog's own structure and its serving module

The core data structure is the application's own, never a Store model (owner, 2026-11-06): DbDataBacklog is set up when it is installed, while Store
models are authored after the rest of the configuration, so the backlog cannot depend on them; the Store can only augment it. Work items, epics, sprints,
boards, comments, links, states and transitions are the `backlog` state module, created on install and migrated with each release. Its rules (who may
move an item to which state, required fields, what a transition does) are the backlog library, served by a `backlog` module of the Gateway (its API, as
the signed-in person, audited, with the module's health), so the screens are a client of it like any app: an app shipped with the suite, served by the
apps module, and a portal for teams that want it alone. Attachments go to the payload store, notifications and digests through the delivery service,
automatic items (a failed run, a check that blocks, an approval waiting) are integration tasks on a Runner (a second failure of the same task adds a
comment, not an item), and an assistant (write and split items, estimate, plan and summarise a sprint) is an agent definition with the backlog API as
its tools, asking before it writes. It is not a server of its own.

**What makes it worth having in this suite:** links to suite objects (an item names a Build model, a replication, a semantic model, a Store entity, an
app, a run or a change request by its lineage identity, and those objects show their open items; a dropped object leaves the link flagged, not deleted);
links to Workspace branches (an item opens a workspace and follows its commits, merge and publish) and to GitHub pull requests; a back-end agent that
takes an item, works in its workspace and hands it back for review; a closed item's resolution proposed as a knowledge suggestion. Outside tools (Jira,
Azure Boards) are extremely low priority and may never happen; an import would be an integration template, never a two-way sync.

**Open questions:** whether the console's own serving of the library stays once the module exists; the order of the parts when it is un-parked.

### Custom attributes for any object type

An object's core structure belongs to the product that owns it; the Store can augment any object type with custom attributes (owner, 2026-11-06).

- **An extension model** is a Store model attached to an object type (`work-item`, `semantic-model`, `build-model`, `app`, `connection`, ...) with
  `extends` (an optional key of `model` format 2): its fields are the custom attributes, each record keyed by the object's stable identity, the one the
  catalog and lineage use.
- **The object's own screens show them**, edited with the Store's validation, history and audit under the Store's permissions, through
  `@dbdatakit/store-ui`; first the work item, then the semantic model, Build model, app and connection screens; a reader sees them read-only.
- **They never change what the product does:** a custom attribute cannot make a work item skip a state or a model build differently, and no rule of
  the owning product reads one.
- **An object that goes leaves its attributes orphaned**, listed for their owners, who can delete them, as knowledge items are.
- **Two kinds of attribute, kept apart:** properties written in a definition (Build's `tags` and `properties`, in git, versioned with it) and custom
  attributes kept in the Store (edited by business users at run time); search and generated knowledge show both, labelled.

**Open question:** whether extension models wait for the Gateway's store module to serve them by object identity (the old plan re-planned them on it).

## Excel add-in

the agent pane and custom functions; the spike is `docs/excel-addin-spike.md`.

## Notebooks as a Runner task kind

decided when it is built, after scripts and compiled projects; first check that a kernel is maintained and runs headless.

## RDL without a report server

rendering RDL files in the suite (Majorsilence Reporting, or FastReport if its spike fails); verifying the Flight SQL ODBC driver on a report server; published semantic views in semantic SQL. Known gaps: Majorsilence refuses matrices, gauges, sparklines, maps and `Lookup`, so a sample of real reports goes through its `rdl-doctor` and renders on Linux and Windows with pushed data before anything is promised; its own data providers stay off and code in a report (`<Code>`, `<Classes>`, `<CodeModules>`) is refused; the decompiled `ReportViewerCore` is not recommended (`docs/bi-tools-on-semantic-models.md` has the routes).

## Upgrading an MSI-installed service from the console

the console's upgrade applies to folder installs; an MSI install is swapped as a folder, which Windows Installer does not know about): MSI may come back; not near the list (owner, 2026-11-18).

## Console-started pairing

For networks where a server cannot reach the console (low priority; the join by token works). Moved from INSTALLER's tasks.

**What.** Console-started pairing for networks where a server cannot reach the console (low priority): `setup invite` and the Add a server screen offer it; the console still generates the command for the remote, code included; it then either takes the remote's address up front, or waits for the user to run the command there and enter the address the command prints; it then connects to the server and pins it, with the token's scope and single use

**Done when (as it was written).** Servers suite: a Runner whose calls to the console are blocked joins by this path and is listed ready, once with its address given up front and once with the address the command printed entered afterwards; a reused or expired code is refused; a Playwright story of entering the address

## Creating a service's login and database on request

A convenience, parked: the administrator creates the login and database today, and `state grants` prints what is needed. Moved from TRUSTZONES' tasks.

**What.** Create the login and the database in one go on request: by default the suite creates nothing (the administrator does, `state grants`), but `state init --apply` of a service's command line and a screen of the console (a button on the service's state settings) create the login and its database when the person asks, through a temporary administrator connection string they give once; the string is used for that run only and never stored, and the generated password is stored as the service's secret and used by the service from then on. The screen is not a stored format, so it needs no row of `docs/ui-editors.md`. INSTALLER-21 writes the `state:` settings and keeps passing the password by secret reference

**Done when (as it was written).** a test on SQL Server and on PostgreSQL: the login and the database are created; the temporary connection string is found nowhere on disk and in no log or audit line; the generated password is stored as the secret and the service starts with it; a second run changes nothing; a story runs the screen

## The console reading other services' runs from their databases

Parked: it cuts against reading each service through its own API, and was always at most an option. Moved from TRUSTZONES' open designs.

**What.** An option for installs where the console can reach a service's database: the console's login gets a read-only grant on the runs, audit and
notifications schemas of that service, never payloads or secrets, and the console's registry lists them so one query reads all. It is never assumed. The
default is each service's API (`docs/state-logins.md`, "Limits"), which needs no network path to the service's database.

**Why.** An API is not always the best path for a large list of runs, and a person may prefer one query. The owner has no rule between APIs and database
queries and cares about UX, performance and security, chosen case by case; direct access is at most an option, because some installs give the console no
network access to a service's database (SQL Server Express, a private container database).

**Open questions.** Where the option is switched on (the console's state settings, per service); how the grant is made (`state grants` for the console's
login, limited to those three modules); how the console falls back to the API when the database is unreachable; and a test on SQL Server and PostgreSQL that the grant is
read-only and limited to those modules, which is the "Done when" of a task made when the owner schedules this.
