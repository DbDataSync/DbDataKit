# Open work, by service

Audited against the code and the git log on 2026-11-11. This page lists what is not built, grouped by the service it lands in, with the plan that owns each row (the row there has the detail and the "done when" test). It does not repeat what a row says; when a row changes, change it in its plan and, if its line here is wrong, here. Built work is in the product docs, never here.

Flags: **OWNER** waits for a decision only the owner can take (the decision is named); **HOLD** is on hold on purpose; **LOW** is low priority by the owner's word; **PARKED** is kept as a paragraph and not scheduled; **PARTLY** says what is built so the line shows only the rest. Rows with no flag can be taken up now.

## Handoff (2026-11-16): the install is four services

[Plan 33](33-fewer-services.md) SC23 is built: the Build, Sync and Store servers are deleted and the install is four services (console, Workspace, Gateway, Runner); `docs/architecture.md` says how it is. Owner rules that steer the work are there: our code calls our code as a library, never as a CLI; shareable code goes into a shared library first; screens follow the work, not the servers; framework-dependent builds; fail forward (CI is the check).

- **First (owner, 2026-11-17): [plan 36](36-install-startup-and-pairing.md), install, startup and pairing**, before any further work on the secrets review and on plan 35 I4 to I9; it changes none of the secrets review's open decisions. Why: the snapshot install failed on the owner's Windows 11 workstation because files were made by one account and used by another (the installer made the Gateway's data before the service ran); plan 36 has each service create and own its data, one startup sequence, reversed pairing from the console and `dbdatakit setup single-server`. It replaces 28c E20d's join by a code and the rows E20e and E20f (removed).
- **Next:** the rows SC23 moved after it (SC20p's operations package, SW01's remainder, SC20n's browser story, SC22i (an owner question: where a preview's rows may be kept), SC22c's platform from the Runner), plan 30 PB02 and PB04 (the Gateway's per-kind publish routes, which still have callers that are not servers), and, after plan 36, plan 35 I4 to I9 (the console-made link between a Gateway group and a Runner group, the two paths with the outbox, state logins per service and group, one backup set, structural tasks only from Control). What SC23 left open is in its row.
- **How the work is run:** `docs/agent-workflow.md` (the coordinator, one worktree per agent, one commit per slice, push only when CI is idle, backups, pacing against a usage limit). Every decision is recorded in the plan row it belongs to; this page and the plans are the whole state, nothing lives elsewhere.

## Held and parked, first

- **LOW priority (owner, 2026-11-09):** [plan 07](07-data-transfer.md) TR01 to TR08 (the shared transfer verifier and Sync on the write gate are the two with a reason; the rest waits for a benchmark or a second product), [plan 12](12-shared-tls-rollout.md) (the follow-up decisions; the owner reviews it later), [plan 15](15-master-data-and-documents.md) (other engines, the document store and WebDAV, extraction on save, "on change" triggers, ad hoc promotion, native temporal tables, the approval model), and the deferred **MDX and XMLA** with the extra protocols ([plan 14a](14a-semantic-slices.md) SL40 to SL55, [plan 20](20-serving-layer.md) O09, O15, O16, O17), which nothing depends on.
- **PARKED:** (owner, 2026-11-14, S21; what is built stays) the backlog's remaining rows (27a BK02, BK05 to BK11; the console's Backlog screen stays as built), custom attributes (EA01 to EA04, to be re-planned on the Gateway's store module) and the Excel add-in (XL02, XL03; the spike document stays); notebooks as a runner task kind (decided when it is built, after scripts and compiled projects; [plan 28](28-runner.md) section "Deferred"), RDL reports without a report server (Majorsilence), verifying the Flight SQL ODBC driver on a report server, published semantic views in semantic SQL (README, "Parked").
- **PAUSED (owner, 2026-11-16):** quality alerts (the console's alert watcher asks servers as the signed-in person, so a background pass sends nothing; plan 35 I3 found it): a row of its own when resumed, after plan 35 I4 gives the console an identity toward a Runner. Resync and reconcile-deletes buttons on a sync project's Runs tab (plan 33 SC22l default 3; they need the drain to work out a mapping's source key). Container images for the Runner and the Workspace, so the quickstart runs all four services: wanted, not yet.

## Decisions the owner must take

| Plan | Decision | Blocks |
| --- | --- | --- |
| [14a](14a-semantic-slices.md) SL25 to SL27 | Ad hoc registrations and the combine in a child process wait until an app or an agent needs them (decided, "not yet"); a request from the owner starts them | nothing else |

Taken (not open any more): the word "unit" of Sync's state on a target is the **position log** (owner, 2026-11-14, S8; built in [28c](28c-engines-on-runner.md) E17); AP17's per-app origin goes into `view-spec/1` without a version bump ([26](26-app-publishing.md), 2026-11-11); the other defaults taken by delegation on 2026-11-09 are in each plan's decisions.

## Console (DbDataKit server and its web app)

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [23a](23a-first-run-and-quality-slices.md) FQ20, FQ22 | Scaling out as a step (state move, add an instance, re-pair); the Gateway and the Runner still installable alone (FQ18 done: `dbdatakit-full` is the Windows bundle, owner 2026-11-14) | |
| [24a](24a-upgrade-and-cache-slices.md) UC11, UC16 | Groups upgrade rolling after draining (waits for the first stable release, owner 2026-11-14) (UC10, one server at a time, is built); the cache settings screen and docs | |
| [22a](22a-gap-slices.md) GP16, GP17 | Alerts: the sources for approvals waiting, expiring previews and late datasets, a task of the runner (the rules, the cool-down, the screen and the failed-run source are built, 2026-11-12); subscriptions: the access-loss stop of a person who loses a rule's access | PARTLY: delivery service, subscriptions library, Delivery page, alerts built |
| [22a](22a-gap-slices.md) GP19, GP22 | The `git.remote` setting and its `health` line in the Workspace's projects first, then the Gateway and the console (owner 2026-11-14; the pusher `GitRemotePusher` is built); the recovery page, the `health` lines (no backup in N days) and the CI drill for the four services | PARTLY |
| [20](20-serving-layer.md) O07, O06b, O20 | The identity home forwarded behind the firewall; hardening (Entra group overage, keytab, signing keys); a group as one node in the console | |
| [29b](29b-state-migration-slices.md) C07 | The Windows installers read and write the shared `state` section (Sync's half dropped; C04, C05 and C06 are built) | |
| [29a](29a-state-slices.md) Q21 | The `runs` module reads Build's runs from the tracking copy | PARTLY: the divergence screen is built |
| [28c](28c-engines-on-runner.md) E18 | Switch-primary as an action, not a refusal | PARTLY |
| [28a](28a-runner-slices.md) R25 | Operator screens for in-doubt steps, dead letters and the table of units | PARTLY: checkpoints built |
| [27a](27a-knowledge-slices.md) AG15, AG18, AG21, AG29, KN28, IX23 | The chat over a real model, providers and usage, fallback lists, the knowledge screens' remainder, the outside-connection form per kind (begun, its row does not say so) | PARTLY |
| [27a](27a-knowledge-slices.md) AX14, KN25 to KN27, KN29, KN33 | Approvals and grants of actions, the knowledge components and app, chunking defaults, the backlog: parked, the console's screen stays (the console hosts the screens, partly built) | not built |
| [Identity README](../../Shared/src/DbDataKit.Identity/README.md) "Not yet run on a real host"; [ui-coverage](../ui-coverage.md) "Open gaps" | V1: Windows sign-in on a domain-joined host and Entra sign-in on a tenant (all of it runs with fakes in CI); UI06: the secrets overview of Sync, Build, Store and Workspace (decided 2026-11-12, not built); the editor-screen run-time evidence (stories not run, Windows runs, the Store's engine paths) | V1 needs a domain-joined host and an Entra tenant |

## Gateway (serving layer, semantic layer, views, apps host)

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [30](30-publishing.md) PB01 to PB06 | The shared HTTP API's callers (the apps command; Build's data sources and models and the Workspace's models and data source documents are on it) and the removal of the per-kind routes (when Sync is operational on the runner), the approver's signature, apps by file from a disk cache, the entity models' and apps' prune screens (PB06, promotion between groups, is built: library, runner command and the console's action) | PB01, PB02 (runner, Gateway), PB03 (but the approver's signature) and PB05 mostly built |
| [21](21-channels-and-previews.md) CH02 to CH06 | A Playwright story of the Releases screen (the console hosts releases for runner packages and Gateway definitions), Build's channel targets, Sync trial runs (the contract guard's callers are built); CH07's removal of a preview on merge or abandon of a branch (the Workspace's call) | CH01, CH07 built but for the merge call |
| [14a](14a-semantic-slices.md) SL02 (rest), SL06 to SL11, SL19, SL28, SL29 | The Gateway's old engine switches retired (SL03, SL04 and most of SL02 built 2026-11-12); the mapped-columns check and pruning at publish; describe with types; metadata documents; the `semantic:` form; promote a saved query; a row cap in the DAX text; the SSAS executor | |
| [14a](14a-semantic-slices.md) SL25 to SL27 | Ad hoc registrations and review, the combine in a child process | OWNER ("not yet") |
| [20](20-serving-layer.md) O05, O08b, O11, O13 | HA in CI; entities over Flight SQL; app code in child processes; the outage tests (the instance row); (O10c and O14b closed 2026-11-14: they touched only the deleted servers; publishing a Store model with instances is 26 Z12b) | |
| [26](26-app-publishing.md) AP08, AP17, AP19, AP30, Z12b | The home's side of address bindings and certificate renewal; a Windows run of embedded report servers; a portal home block of a published `dashboard/1`; Store instance remainders | PARTLY |
| [24a](24a-upgrade-and-cache-slices.md) UC13 (the producers of `data.changed`) | Before plan 33 SC23: Build's and Sync's runtimes report their writes on the Runner (done 2026-11-14) and a Store write through the Gateway clears its own cache (the servers' producers go); the Link flow's browser story, Runner only | |
| [32](32-dashboards-and-reports.md) DBR13, DBR14, DBR16 to DBR20, DBR23, DBR29, RC01 to RC05 | The rest of apps and portals, the export component (built 2026-11-12: a release asset per platform, `dbdatagateway export install`, the console's Install button, the installer options and the `-export` image; what is left is a first real release run that proves the zips and the image, and a real `export install` against it), scheduled delivery in the Gateway (built: it enqueues by the channel kind stored on the subscription and the console's sender sends; unverified against a real console and Gateway on one SQL Server), the embed widget's report-server run, report catalogs (built as libraries, the module's routes and the Catalog tab: what is left is a host that wires the connections, the runner task and the relay of `/api/v1/catalog...`, the app manifest's `catalogs`, the knowledge module's call, recording the SSRS answers, the Windows sign-in) | PARTLY each |
| [32](32-dashboards-and-reports.md) DBR31 (the host half and a browser story), DBR32 (the page route, the CI job), DBR33, DBR34, DBR36 (the apps host's half and a browser story), DBR42 (print only: checked in a browser), DBR53 (the runner's `task` and the apps host's app scope), DBR53b (`for: selection` with a table's selection, inline edits, every version conflict listed) | Custom views in the shell (frames, dialogs, panels, scheduled custom views), the nested axis in print, filtered and repeated measures, attributes in the matrix, field groups, the runner's task from an action, batches by selection and inline edits | actions, the popup, batches built; the rest not built |
| [34](34-pages.md) PG02 to PG10 | One document format `page/1` with a `kind` replaces `dashboard/1`, `report/1` and `view-spec/1`; entity widgets, the GFM text widget and its editor, the Store read query, the portal home and custom pages as pages, the catalog's kind facet, the spec of data entry (DBR13's unbuilt parts fold in) | decided 2026-11-14; starts after plan 33's server-removal work |
| [22a](22a-gap-slices.md) GP14 (GP01, GP02, GP10 to GP13 and GP15 built; GP15 but for the form editor of the section) | Access rules end to end: the rules model of QG2 (a rules table built by a published Build model, a join in the plan; column rules at describe time) | GP01, GP02 built 2026-11-12 (the section, its publish; the refusal of a rules table no Build model produces waits for KN13); GP10, GP11 built 2026-11-12 (the resolver, the row filter of semantic models; the Store entity half too: `StoreCaller.Groups`, the filter in the Store's SQL); GP13 built 2026-11-12 (column rules on semantic models and the entity read's shaper, one cache-key fingerprint with the row rules; the per-group mask key and the publish warning for a mask that became a hide: QR follow-ups); GP12 built 2026-11-13 (a statement that names a table a model puts rules on is refused to pass-through SQL naming the rule and the model, and the raw listing leaves the table out: `RuledTables`, `PassThroughGuardServices`, the lexical `TableNameScan`); GP14 built 2026-11-13 (the module matrix, the reference tests; it found and fixed the column rules of a Store model never applied by a Store read and the views module's own cache serving rows of the old rules) (GP15 built but for the form editor of the `access:` section in the Workspace and Store model panels); LOW and deferred (owner, 2026-11-14): whether the publish warning also names a `null` rule on an Analysis Services column (turned into a hide like a mask) needs more thought, and Analysis Services has its own row-level security; not scheduled |
| [23a](23a-first-run-and-quality-slices.md) FQ11 to FQ15 | Run, warn and block checks of the Store's models and the serving definitions (FQ03 and FQ05, the stored sections, are done) | FQ11 to FQ15 open |
| [18](18-lineage.md) G13 to G15 | Manual entries, OpenLineage export, tracing a value | G13 to G15 later |
| [27a](27a-knowledge-slices.md) KN06 (binding it, Windows), KN11 to KN17, KN19 to KN23, AG23, AG25, AG28 | The built-in embedding model is built (generator, catalog, files): it cannot be bound from the console yet; the Gateway hosting of the `knowledge` module (the library is built: a version per index, stage, switch, rollback, KN11), its subscription to published semantic models (KN12; KN22's re-check is the same subscription) and to Build\'s metadata (KN13, plus Build\'s own publish step), values as knowledge, the hosts of the read, index, `query` and suggestion commands (KN16, KN17, KN19, KN21, KN23: built as libraries, KN19 to KN23 also in the console; the serving layer offers none), the Workspace server\'s collection of suggestions (KN20), the promotion call that asks `EvaluationGate` (AG23: built as a library and in the console, the publish API does not call it), front-end agents and a published agent as an MCP tool | KN10 to KN14, KN16, KN17 PARTLY (libraries, commands and the console\'s Check tab built) |

## Runner

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [28a](28a-runner-slices.md) R07, R13, R26, R27, R27c, R27d | Limits per connection, the process tree and the cgroup path in CI; the serving layer's webhook end, the Store change source (from the Gateway's store module), a file connection kind; a per-package account; the manifest generated in a build step, a release cut and installed; a task and a replication under a real Windows service | R13 PARTLY |
| [36](36-install-startup-and-pairing.md) IS13 | A Runner or Gateway instance joins its group through the console's join token and receives the group key from the console (replaces 28c E20d's join by a code, E20e and E20f; a group key change is not in plan 36, decision 15) | |
| [28c](28c-engines-on-runner.md) E06f, E17, E21, E22 | Splitting the Sync integration CI job (recorded only); Sync's state on the target (the `tracking:` setting, its status and the Tracking card are built; the same-transaction writers are built for SQL Server and PostgreSQL; the follower to the state database, the persisted divergence with its resolve, the retention and the console's Sync half are built, in the Sync server only; the runner-side follower for a runner-owned Sync and the MySQL/MariaDB/Oracle writers are not); interactive writes as manual tasks; retiring the old paths (`ProcessSupervisor`, the loopback state; waits for E17's state modes and E16's `mode: state`; `BuildScheduler` went with the Build server, plan 33 SC23c) | E17 PARTLY; E21 required (owner 2026-11-14), Build half built |
| [28c](28c-engines-on-runner.md) E14, E16, CW02 | The rest of Sync on modules; Build tracking's remainder; the primary-key-update remainder | PARTLY |
| [29a](29a-state-slices.md) Q22, Q25 | Sync's processes on the runner-owned state; deleting the old state path | Q20 PARTLY |
| [27a](27a-knowledge-slices.md) IX03 to IX06, IX16 to IX24 | OAuth 2 authorization code, SFTP, SMB and object storage, templates, the integration kind in the Workspace | IX20 to IX24 and the W rows follow the Workspace |

## Workspace

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [33](33-fewer-services.md) SC23 (open items), SC24 | SC23 and SC24 are built; what SC23 left open (the `checks:` of a replication or a Store model evaluated by nobody, plan 35 I3 having built a Build model's on the Runner, the contract guard asking nobody outside a test, a set's first activation before `provision`, no image of the Workspace or the Runner, the engine's design history that still describes the server) is in the SC23 row | |
| [33](33-fewer-services.md) SC25 | Framework-dependent everywhere (architecture D11, owner 2026-11-14): Build and the Gateway stop bundling .NET; the Gateway's 32-bit ODBC worker stays self-contained | |
| [33](33-fewer-services.md) SW01 (the apps command's move with plan 30 PB04, retiring SC20e's verb and SC21f's confirmation), SW02 (the slot bindings screen: built 2026-11-16, its story), SC20e, SC20f, SC20j, SC20k, SC20n, SC20p, SC21f, SC22d, SC22j, SC22k | Build publishing to a Gateway, a browser story of the screens, the catalog cache's producer, the Views tab opened in a browser, the database-facing screens, adoption, Sync screens' stories, test a connection, the source side of the provisioning plan (the live server's) and the platform setting; built 2026-11-12 and not opened in a browser: column lineage (SC20h), model files from cached tables (SC20k), the provisioning plan from the cache (SC22j), scripts (SC22k) | SC20e, SC20o, SC21f, SC22l OWNER; SC22h built 2026-11-16 (the Runner tests a published set's bound login, from the Runs tab); the rest PARTLY or not built |
| [22a](22a-gap-slices.md) GP36 | Agents through MCP, every step approved | PARTLY |
| [27a](27a-knowledge-slices.md) AG16, AX13, KN20, KN29, KN30, IX20, IX24, BK06 | The W rows: the agent, action, knowledge, search and integration editors in the Workspace, links to workspaces and pull requests | follow GP35 |

## Build

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [25](25-surrogate-keys-and-agent-tests.md) K07, K11 | Consumers of inferred relationships, Store ids as `BINARY(16)` and `uuid` | none blocked; K11's drafts exist |
| [18](18-lineage.md) G06b | A job node per load (decided against for now); the server path over a real tracking copy untested | |
| [Build OPEN-ITEMS](../../DbDataBuild/docs/progress/OPEN-ITEMS.md) section S; [12](12-shared-tls-rollout.md) items 10 and 11; [Gateway README](../../DbDataGateway/README.md) Fabric (Not supported yet); [windows-test-install](../windows-test-install.md) | K1 (done 2026-11-12, except the ODBC and DuckDB opens, which stay on purpose: Build OPEN-ITEMS S): the shared readers' schema-wide keys read and Build's `SourceCatalogReader` on it; K2: Sync's `service`, `cert` and `secret` verbs are deleted with the Sync server (plan 33 SC23, owner 2026-11-14), not moved; D11: Build's CLI on the shared router; F1: Fabric through the Gateway (needs a tenant) | D11 LOW, revisit after 33 SC23 (owner 2026-11-14: blocks nothing; keeping the command line is a question of a tool for people only); K2 closes with SC23 |
| [14a](14a-semantic-slices.md) SL10, SL22 | Metadata documents with semantic models; the `semantic` noun | |
| [27a](27a-knowledge-slices.md) AG01 | The suite-wide command-spec test with a golden of offered MCP tools: built (`CommandSurface`) and kept for the console; the other products add their own copy | PARTLY |

## Sync

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [28c](28c-engines-on-runner.md) E17, E22 | State on the target (SQL Server and PostgreSQL writers built; the follower, the divergence record and the other engines are not), retiring the old paths | E17 PARTLY |
| [29a](29a-state-slices.md) Q22, [29b](29b-state-migration-slices.md) C05, C07 | The runner-owned state for processes; the state API; the shared `state` section | |
| [07](07-data-transfer.md) TR04 | Sync adopts the write gate | LOW |
| [18](18-lineage.md) G07b | The target table's own upstream when Sync is not its only writer; runs on column edges | |

## Store

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [15](15-master-data-and-documents.md) | Everything listed there | LOW |
| [21](21-channels-and-previews.md) CH02 to CH06 | The Store's part of releases and inherited reads | |
| [26](26-app-publishing.md) Z12b, Z13b | Shared entities in `column` mode, instance-scoped tokens and roles, retype and mode moves, the consumer refusal | |
| [18](18-lineage.md) G12 | The apps host's own graph (the Store's emitter moves with G18) | |
| [20](20-serving-layer.md) O08b | Entities as Flight SQL sources | |

## Shared and cross-cutting

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [36](36-install-startup-and-pairing.md) IS01 to IS20 | Install, startup and pairing (owner, 2026-11-17; comes first): the shared startup library and one startup-failure file per service, logs in one place, exact service names and accounts, services that create and own their data, admin commands through the local API, `--skip-folder-permissions-check` as an explicit switch, `doctor` renamed `service check`, ports from the suite range 7800 to 7899, the join library and reversed pairing, the Add a server screen, `dbdatakit setup single-server` and `setup invite`, groups through the token, server-to-server trust through the console, the Scoop scripts reduced, Docker Compose, the clean break, the old code deleted, console-started pairing (low priority) | no open question (answered 2026-11-17) |
| [35](35-isolation-by-trust.md) I1 to I9 | Isolation by trust: the housekeeping library in every server and the embedded runner's removal (I1, I2, inside SC23); after SC23, evaluation as a Runner task, the console-made Gateway-Runner link, path 1 with an outbox and the 10-second wait, path 2 for the contract guard and `data.changed`, state logins per service and group, one backup set, structural tasks only from Control | I1, I2, I3, I3a built (2026-11-16) |
| [22a](22a-gap-slices.md) GP29 | OpenTelemetry: what is left is the trace id in the serving contract and the hop through a module (the hosts' spans and counters, GP28, and the trace across the console's relay are built; `docs/observability.md`) | |
| [22a](22a-gap-slices.md) GP23 | Descriptions and owners in lineage nodes: the emitters, once the definitions carry the text (the contract `LineageDescriptions` is built) | PARTLY |
| [24a](24a-upgrade-and-cache-slices.md) UC07 (program legs) | The compatibility job runs the previous release's programs against the current build (the contract check of generations and formats is built) | waits for the first stable release (owner 2026-11-14) |
| (no row yet: owner, 2026-11-15) | **Every secret value and login has an optional expiration date**, if it does not already: stored with the secret (the shared secret stores, the runner group store, the console's store, connection logins), shown and edited where the secret is, warned of before it expires, and a run that uses an expired login fails naming it. Implement later; the first step is a row in the plan that owns the shared secret stores | not started |
| [27a](27a-knowledge-slices.md) AX01 to AX12 | Actions (one script format, callable and served, principals, grants, audit) | not built |
| [7](07-data-transfer.md), [12](12-shared-tls-rollout.md) | See "Held and parked" | LOW |
