# Planning

The one index of open work. Each subject has a file `planning/<SLUG>.md` with its goal, what exists, its tasks (`<SLUG>-<n>`, in priority order), what it
needs from other subjects, its proposed decisions (`<SLUG>-P<n>`) and the shared areas it touches. How the work is run, and who may edit what, is
[ROLES.md](ROLES.md). How the product works, and why, is in the product documentation (`docs/`, the READMEs), never here.

Subjects in priority order. The owner sets the order, the status and the assignment.

| # | Subject | Goal | Status | Assigned | Tasks |
| --- | --- | --- | --- | --- | --- |
| 1 | [INSTALLER](INSTALLER.md) | Services own their data, start one way, fail in words, join the console by a generated command; no pairing codes | active | unassigned | 29 |
| 2 | [ENDPOINTS](ENDPOINTS.md) | The Gateway listens on one public HTTPS port routed by path, plus Flight SQL and management | next | unassigned | 6 |
| 3 | [TRUSTZONES](TRUSTZONES.md) | The console links Gateway and Runner groups; Serving asks Work through an outbox; Work tells and reads Serving; logins per service | next | unassigned | 12 |
| 4 | [ALERTING](ALERTING.md) | Failed runs, checks, approvals, a silent console or a dead server reach a person, from the console and from each service | queued | unassigned | 6 |
| 5 | [DATAQUALITY](DATAQUALITY.md) | The checks of replications, Store models, data sources and catalogs are evaluated; a blocked dataset keeps its last good version served | queued | unassigned | 6 |
| 6 | [AUTHORING](AUTHORING.md) | Everything the deleted servers did is done from Workspace projects, with browser stories | queued | unassigned | 13 |
| 7 | [PAGEFORMAT](PAGEFORMAT.md) | `page/1` replaces `dashboard/1`, `report/1` and `view-spec/1` | queued | unassigned | 13 |
| 8 | [PUBLISHING](PUBLISHING.md) | Every kind on the one publish API: per-kind routes gone, apps by file, the approver's signature, `blocked_by_check` | queued | unassigned | 5 |
| 9 | [SUBSCRIPTIONS](SUBSCRIPTIONS.md) | Subscriptions and report-catalog reads run as Runner tasks, as the subscriber | queued | unassigned | 7 |
| 10 | [APPHOSTING](APPHOSTING.md) | Several sites with their own host names, certificates and sign-in on one Gateway | queued | unassigned | 8 |
| 11 | [BACKUPRESTORE](BACKUPRESTORE.md) | One backup set, one key backup, one restore drill in CI | queued | unassigned | 5 |
| 12 | [KNOWLEDGE](KNOWLEDGE.md) | Knowledge served by the Gateway, agents front and back, actions on path 1 | queued | unassigned | 23 |
| 13 | [CHANNELS](CHANNELS.md) | Build test targets and Sync trial runs from the Workspace; previews removed on merge | queued | unassigned | 6 |
| 14 | [SEMANTICS](SEMANTICS.md) | Semantic models checked, typed, described and configurable; SSAS bounded | queued | unassigned | 9 |
| 15 | [MASTERDATA](MASTERDATA.md) | A change feed on the store module, native entity ids, instances, entities over Flight SQL | queued | unassigned | 5 |
| 16 | [TASKENGINE](TASKENGINE.md) | Runner limits and accounts, Sync state on the target, lineage retention, availability tests | queued | unassigned | 11 |
| 17 | [CODECLEANUP](CODECLEANUP.md) | No dead code or stale docs of the deleted servers; the old state path gone | queued | unassigned | 5 |

## Parked

Not scheduled; a line each. The owner un-parks one by adding a subject.

- **Data transfer** (the old TR rows): a shared transfer verifier, Sync on the write gate, Arrow-first and Parquet staging (waits on a benchmark), MySQL
  and Oracle writers, SCD2 and Snapshot, segmenting in shared packages (`Shared/src/DbDataKit.Transfer/README.md` says what is built).
- **MDX, XMLA and the extra protocols**: Excel PivotTables over semantic models, XMLA sign-in, Entra tokens for Power Query, the PostgreSQL wire protocol
  and Quack (ENDPOINTS-6 only reserves the listener).
- **Documents and WebDAV**: the document store, extraction on save, WebDAV and its sign-in; with them the Store's other engines (SQLite, MariaDB, MySQL,
  Oracle), promotion of ad hoc tables, native temporal tables and the approval model.
- **Backlog** (the old BK rows after BK01): the backlog's serving module, links, notifications, automatic items, the assistant; the console's Backlog
  screen stays as built.
- **Custom attributes** (the old EA rows): extension models keyed by an object's identity, to be re-planned on the Gateway's store module.
- **Excel add-in** (XL02, XL03): the agent pane and custom functions; the spike is `docs/excel-addin-spike.md`.
- **Notebooks as a Runner task kind**: decided when it is built, after scripts and compiled projects; first check that a kernel is maintained and runs
  headless.
- **RDL without a report server**: rendering RDL files in the suite (Majorsilence Reporting, or FastReport if its spike fails); verifying the Flight SQL
  ODBC driver on a report server; published semantic views in semantic SQL.
- **Ad hoc semantic registrations** (SL25 to SL27): registrations and their review, and the combine in a child process, until an app or an agent needs them.
- **DbDataKit Studio, trust lists and the `dbdatakit` admin tool** (a 2026-11-02 proposal found on an old branch; in git under the old plans folder's `needs-review/`, before
  `planning/` was created): the rename to Studio is overtaken by the product name DbDataKit; certificate rollover with trust
  lists and AD CS from a workstation are not planned.
