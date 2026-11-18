# Planning

The one index of open work. Each subject has a file `planning/<SLUG>.md` with its goal, what exists, its tasks (`<SLUG>-<n>`, in priority order), what it
needs from other subjects, its proposed decisions (`<SLUG>-P<n>`) and the shared areas it touches. How the work is run, and who may edit what, is
[ROLES.md](ROLES.md). How the product works, and why, is in the product documentation (`docs/`, the READMEs), never here.

Subjects in priority order. The owner sets the order, the status and the assignment.

| # | Subject | Goal | Status | Assigned |
| --- | --- | --- | --- | --- |
| 1 | [INSTALLER](INSTALLER.md) | Services own their data, start one way, fail in words, join the console by a generated command; no pairing codes | active | unassigned |
| 2 | [ENDPOINTS](ENDPOINTS.md) | The Gateway listens on one public HTTPS port routed by path, plus Flight SQL and management | next | unassigned |
| 3 | [TRUSTZONES](TRUSTZONES.md) | The console links Gateway and Runner groups; Serving asks Work through an outbox; Work tells and reads Serving; logins per service | next | unassigned |
| 4 | [CERTIFICATES](CERTIFICATES.md) | Enterprise certificates on the console's and the Gateway's user-facing endpoints; management certificates renew themselves without breaking a link (has open designs) | queued | unassigned |
| 5 | [ALERTING](ALERTING.md) | Failed runs, checks, approvals, a silent console or a dead server reach a person, from the console and from each service | queued | unassigned |
| 6 | [DATAQUALITY](DATAQUALITY.md) | The checks of replications, Store models, data sources and catalogs are evaluated; a blocked dataset keeps its last good version served | queued | unassigned |
| 7 | [AUTHORING](AUTHORING.md) | Everything the deleted servers did is done from Workspace projects, with browser stories | queued | unassigned |
| 8 | [SYNCAUTHORING](SYNCAUTHORING.md) | A Workspace sync project's replications are authored in the Sync screens restored as its Designer, with Files beside it as the fallback | queued | unassigned |
| 9 | [NAVIGATION](NAVIGATION.md) | A labelled left navigation in six named groups, servers nested under their names, in the old Sync screens' nav and tab styles | queued | unassigned |
| 10 | [PAGEFORMAT](PAGEFORMAT.md) | `page/1` replaces `dashboard/1`, `report/1` and `view-spec/1` | queued | unassigned |
| 11 | [PUBLISHING](PUBLISHING.md) | Every kind on the one publish API: per-kind routes gone, apps by file, the approver's signature, `blocked_by_check` | queued | unassigned |
| 12 | [SUBSCRIPTIONS](SUBSCRIPTIONS.md) | Subscriptions run as Runner tasks, as the subscriber | queued | unassigned |
| 13 | [REPORTCATALOGS](REPORTCATALOGS.md) | Reports on outside report servers found in the suite: a Runner task reads each catalog, the Gateway shows it per person, each entry links out | queued | unassigned |
| 14 | [APPHOSTING](APPHOSTING.md) | Several sites with their own host names, certificates and sign-in on one Gateway | queued | unassigned |
| 15 | [BACKUPRESTORE](BACKUPRESTORE.md) | One backup set, one key backup, one restore drill in CI | queued | unassigned |
| 16 | [KNOWLEDGE](KNOWLEDGE.md) | Knowledge served by the Gateway, agents front and back, actions on path 1 | queued | unassigned |
| 17 | [CHANNELS](CHANNELS.md) | Build test targets and Sync trial runs from the Workspace; previews removed on merge | queued | unassigned |
| 18 | [SEMANTICS](SEMANTICS.md) | Semantic models checked, typed, described and configurable; SSAS bounded | queued | unassigned |
| 19 | [MASTERDATA](MASTERDATA.md) | A change feed on the store module, native entity ids, instances, entities over Flight SQL | queued | unassigned |
| 20 | [TASKENGINE](TASKENGINE.md) | Runner limits and accounts, Sync state on the target, lineage retention, availability tests | queued | unassigned |
| 21 | [CODECLEANUP](CODECLEANUP.md) | No dead code or stale docs of the deleted servers; the old state path gone | queued | unassigned |
| 22 | [FABRICSERVING](FABRICSERVING.md) | A Fabric warehouse as a Gateway data source, signed in with Entra; close to working, low priority until the product is consistent | open design | unassigned |

## Parked

Not scheduled; a line each. The owner un-parks one by adding a subject or scheduling a subject's open design. Where the old plans held design for a
parked line, the line names where that design is kept.

- **Data transfer** (the old TR rows): a shared transfer verifier, Sync on the write gate, Arrow-first and Parquet staging (waits on a benchmark), MySQL
  and Oracle writers, SCD2 and Snapshot, segmenting in shared packages, the Windows access list on the statement log (`Shared/src/DbDataKit.Transfer/README.md`
  says what is built and the rules that order this work).
- **MDX, XMLA and the extra protocols**: Excel PivotTables over semantic models, XMLA sign-in, Entra tokens for Power Query, the PostgreSQL wire protocol
  and Quack (ENDPOINTS-6 only reserves the listener). Their design is SEMANTICS' open designs.
- **Documents and WebDAV**: the document store, extraction on save, WebDAV and its sign-in; with them the Store's other engines (SQLite, MariaDB, MySQL,
  Oracle), promotion of ad hoc tables, native temporal tables and the approval model. Their design is MASTERDATA's open designs.
- **Backlog** (the old BK rows after BK01): the backlog's serving module, links, notifications, automatic items, the assistant; the console's Backlog
  screen stays as built. **Custom attributes** (the old EA rows): extension models keyed by an object's identity, to be re-planned on the Gateway's store
  module. Their design: [BACKLOGS](BACKLOGS.md) (open designs only).
- **Excel add-in** (XL02, XL03): the agent pane and custom functions; the spike is `docs/excel-addin-spike.md`.
- **Notebooks as a Runner task kind**: decided when it is built, after scripts and compiled projects; first check that a kernel is maintained and runs
  headless.
- **RDL without a report server**: rendering RDL files in the suite (Majorsilence Reporting, or FastReport if its spike fails); verifying the Flight SQL
  ODBC driver on a report server; published semantic views in semantic SQL. Known gaps: Majorsilence refuses matrices, gauges, sparklines, maps and
  `Lookup`, so a sample of real reports goes through its `rdl-doctor` and renders on Linux and Windows with pushed data before anything is promised; its own
  data providers stay off and code in a report (`<Code>`, `<Classes>`, `<CodeModules>`) is refused; the decompiled `ReportViewerCore` is not recommended
  (`docs/bi-tools-on-semantic-models.md` has the routes).
- **Ad hoc semantic registrations** (SL25 to SL27): registrations and their review, and the combine in a child process, until an app or an agent needs them.
  Their design is SEMANTICS' open designs.
- **Upgrading an MSI-installed service from the console** (24a UC09: the console's upgrade applies to folder installs; an MSI install is swapped as a folder,
  which Windows Installer does not know about): MSI may come back; not near the list (owner, 2026-11-18).
