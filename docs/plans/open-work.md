# Open work, by service

Audited against the code and the git log on 2026-11-11. This page lists what is not built, grouped by the service it lands in, with the plan that owns each row (the row there has the detail and the "done when" test). It does not repeat what a row says; when a row changes, change it in its plan and, if its line here is wrong, here. Built work is in the product docs, never here.

Flags: **OWNER** waits for a decision only the owner can take (the decision is named); **HOLD** is on hold on purpose; **LOW** is low priority by the owner's word; **PARKED** is kept as a paragraph and not scheduled; **PARTLY** says what is built so the line shows only the rest. Rows with no flag can be taken up now.

## Held and parked, first

- **ON HOLD until the owner has tested the Workspace's parity** (owner, 2026-11-11; the matrix is `docs/authoring-parity.md`): SC20g (the Build server's authoring routes handed over), SC21d (the Store's own authoring screens handed over), SC22e (the Sync server's authoring routes handed over), all in [plan 33](33-fewer-services.md). Parity first, then the owner's snapshot test, then the owner's go.
- **LOW priority (owner, 2026-11-09):** [plan 07](07-data-transfer.md) TR01 to TR08 (the shared transfer verifier and Sync on the write gate are the two with a reason; the rest waits for a benchmark or a second product), [plan 12](12-shared-tls-rollout.md) (the follow-up decisions; the owner reviews it later), [plan 15](15-master-data-and-documents.md) (other engines, the document store and WebDAV, extraction on save, "on change" triggers, ad hoc promotion, native temporal tables, the approval model), and the deferred **MDX and XMLA** with the extra protocols ([plan 14a](14a-semantic-slices.md) SL40 to SL55, [plan 20](20-serving-layer.md) O09, O15, O16, O17), which nothing depends on.
- **PARKED:** notebooks as a runner task kind (decided when it is built, after scripts and compiled projects; [plan 28](28-runner.md) section "Deferred"), RDL reports without a report server (Majorsilence), verifying the Flight SQL ODBC driver on a report server, published semantic views in semantic SQL (README, "Parked").

## Decisions the owner must take

| Plan | Decision | Blocks |
| --- | --- | --- |
| [33](33-fewer-services.md) SC20o | Build's database-facing screens (project tests, status, refresh run, compare, initialise): move to the Runner's task screens and the command line, or Build's server keeps those routes | what SC20g removes |
| [33](33-fewer-services.md) SC22l | Sync's run actions and monitoring: stay with the Runner and the Sync server, or come into the Workspace | what SC22e removes |
| [33](33-fewer-services.md) SC20e | A Build publish to a Gateway without a login in the Workspace: an offline `project publish --to gateway --out <folder>` that a Build server sends, or another way | Build publishing from the Workspace |
| [33](33-fewer-services.md) SC21f | Data source adoption at a Store publish (a publish-time adopt, or plan 30's slot bindings) | Store publishing parity |
| [22a](22a-gap-slices.md) QG2 | The rule language of access rules (our own expression or a DuckDB SQL subset) | GP01, GP02, GP11 to GP15, and with them FQ03 and FQ05 of [23a](23a-first-run-and-quality-slices.md) and the rules half of the serving cache (UC15) |
| [UI06 of the editor-screen gaps](../ui-coverage.md) | Which store Sync's and Build's secrets overview compares their references with (Store and Workspace: whether their stores are listed at all) | the secrets overview for Sync, Build, Store and Workspace |
| [27](27-knowledge-agents-integrations-work.md) row 61, [27a](27a-knowledge-slices.md) XL01 to XL03 | The Excel add-in: still a proposal | the add-in |
| [14a](14a-semantic-slices.md) SL25 to SL27 | Ad hoc registrations and the combine in a child process wait until an app or an agent needs them (decided, "not yet"); a request from the owner starts them | nothing else |
| [28c](28c-engines-on-runner.md) E21 | Interactive writes as manual tasks (optional; the write-login constraint was relaxed on 2026-11-07, to re-evaluate later) | a Build server with no write login at all |
| [27a](27a-knowledge-slices.md) IX17 and IX23, [docs/channel-bindings.md](../channel-bindings.md) | **OWNER:** where the record of a production binding in a non-production channel lives. You asked for one place; built is one mechanism (the same code) in two tables, each server keeping the choices of the bindings it holds (the console's `bindings.posture_choice`, the Runner's `runner.posture_choice`), because the Runner must enforce the rule without reading the console's database. The document says why and what one table would cost | whether the posture record stays per server |

Taken (not open any more): AP17's per-app origin goes into `view-spec/1` without a version bump ([26](26-app-publishing.md), 2026-11-11); the other defaults taken by delegation on 2026-11-09 are in each plan's decisions.

## Console (DbDataKit server and its web app)

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [23a](23a-first-run-and-quality-slices.md) FQ18, FQ20, FQ22 | The single-machine bundle (one installer, the bundle token), scaling out as a step (state move, add an instance, re-pair), each product still installable alone. Plan 33's `dbdatakit-full` Scoop package is the nearest thing | |
| [24a](24a-upgrade-and-cache-slices.md) UC11, UC16 | Groups upgrade rolling after draining (UC10, one server at a time, is built); the cache settings screen and docs | |
| [22a](22a-gap-slices.md) GP16, GP17 | Alerts: the sources for approvals waiting, expiring previews and late datasets, a task of the runner (the rules, the cool-down, the screen and the failed-run source are built, 2026-11-12); subscriptions: the access-loss stop of a person who loses a rule's access | PARTLY: delivery service, subscriptions library, Delivery page, alerts built |
| [22a](22a-gap-slices.md) GP19 to GP22 | A git remote per configuration repository, `state backup` for SQLite, the secrets key backup and restore, the recovery drill page | |
| [20](20-serving-layer.md) O07, O06b, O20 | The identity home forwarded behind the firewall; hardening (Entra group overage, keytab, signing keys); a group as one node in the console | |
| [29b](29b-state-migration-slices.md) C04 to C07 | Products name their encrypted columns for the decrypt-a-sample check; the state API on the runner and the Sync server; the connection test on the server's modules; Sync and the Windows installers read and write the shared `state` section | PARTLY: the identity home, the audit and the Gateway's maintenance are built |
| [29a](29a-state-slices.md) Q21 | The `runs` module reads Build's runs from the tracking copy | PARTLY: the divergence screen is built |
| [28c](28c-engines-on-runner.md) E18 | Switch-primary as an action, not a refusal | PARTLY |
| [28a](28a-runner-slices.md) R25 | Operator screens for in-doubt steps, dead letters and the table of units | PARTLY: checkpoints built |
| [27a](27a-knowledge-slices.md) AG15, AG18, AG21, AG29, KN28, IX23 | The chat over a real model, providers and usage, fallback lists, the knowledge screens' remainder, the outside-connection form per kind (begun, its row does not say so) | PARTLY |
| [27a](27a-knowledge-slices.md) AG24, AX14, KN25 to KN27, KN29, KN33, BK03, BK04 | Evaluation, approvals and grants of actions, the knowledge components and app, chunking defaults, the backlog and board, sprints | not built |
| [Identity README](../../Shared/src/DbDataKit.Identity/README.md) "Not yet run on a real host"; [ui-coverage](../ui-coverage.md) "Open gaps" | V1: Windows sign-in on a domain-joined host and Entra sign-in on a tenant (all of it runs with fakes in CI); UI06: the secrets overview of Sync, Build, Store and Workspace; the editor-screen run-time evidence (stories not run, Windows runs, the Store's engine paths) | V1 needs a domain-joined host and an Entra tenant; UI06 OWNER |

## Gateway (serving layer, semantic layer, views, apps host)

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [30](30-publishing.md) PB01 to PB06 | The rest of the serving kinds in the registry (semantic model, data source, entity model, app, instance, navigation), the shared HTTP API and the removal of the per-kind routes (when Sync is operational on the runner), signatures and a trust store, apps by file from a disk cache, prune for every kind, promotion between groups | PB01 PARTLY |
| [21](21-channels-and-previews.md) CH02 to CH06 | Releases with one approval, inherited reads, Build's channel targets, Sync trial runs, the contract-step refusal; CH07's settings, notice and caller | CH01, CH07 library built |
| [14a](14a-semantic-slices.md) SL02 to SL04, SL06 to SL11, SL19, SL20, SL22, SL28, SL29 | The three products read the engine registry; the mapped-columns check and pruning at publish; describe with types; decimals; metadata documents; the `semantic:` form; promote a saved query; the `queries:` editor; Build's `semantic` noun; a row cap in the DAX text; the SSAS executor | |
| [14a](14a-semantic-slices.md) SL25 to SL27, SL30, SL31 | Ad hoc registrations and review, the combine in a child process, publish from Build's command line, the REST listener | OWNER ("not yet"); SL30 LOW |
| [20](20-serving-layer.md) O05, O08b, O10c, O11 to O14b, O18, O19 | HA in CI; entities over Flight SQL; Store publishing from its API; app code in child processes and the `mcp` module; the outage tests; the rename's vocabulary in Build and Store; module health on `/health`; a request pinned to one version | O10c and O18 PARTLY |
| [26](26-app-publishing.md) AP08, AP17, AP19, AP27, AP30, Z12b | A Playwright story and DNS-challenge verification; the per-app origin and a Windows run; the swap of the apps host onto the home's app tokens; the apps host's stories run; portal home blocks; Store instance remainders | PARTLY |
| [24a](24a-upgrade-and-cache-slices.md) UC12 to UC15 | The result cache: service and key, expiry on `data.changed`, time-to-live and shared store, the safety conformance suite | UC15 needs the rules (OWNER, QG2) |
| [32](32-dashboards-and-reports.md) DBR13, DBR14, DBR16 to DBR21, DBR23, DBR29, RC01 to RC05 | The rest of apps and portals, the PDF exporter's settings, scheduled delivery's remainder, the embed widget's report-server run, validate commands, report catalogs | PARTLY each |
| [32](32-dashboards-and-reports.md) DBR31 to DBR34, DBR36, DBR42, DBR50 to DBR55 | Custom views in the shell (frames, dialogs, panels, scheduled custom views), the nested axis in print, filtered and repeated measures, attributes in the matrix, field groups, actions with a confirmation popup and batches of changes | not built (9h is design only) |
| [22a](22a-gap-slices.md) GP01, GP02, GP11 to GP15 | Access rules end to end | OWNER (QG2) |
| [23a](23a-first-run-and-quality-slices.md) FQ03, FQ05, FQ11 to FQ15 | Checks stored with the Store's models and the serving definitions, run, warn and block | FQ03, FQ05 wait for QG2 |
| [18](18-lineage.md) G13 to G15 | Manual entries, OpenLineage export, tracing a value | G13 to G15 later |
| [27a](27a-knowledge-slices.md) KN06 (binding it, Windows), KN11 to KN17, KN19 to KN23, AG25, AG28 | The built-in embedding model is built (generator, catalog, files): it cannot be bound from the console yet; the Gateway hosting of the `knowledge` module (the library is built: a version per index, stage, switch, rollback, KN11), its subscription to published semantic models (KN12) and to Build\'s metadata (KN13, plus Build\'s own publish step), values as knowledge, the hosts of the read and index commands (KN16, KN17), suggestions, verified queries at staging, front-end agents and a published agent as an MCP tool | KN10 to KN14, KN16, KN17 PARTLY (libraries, commands and the console\'s Check tab built) |

## Runner

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [28a](28a-runner-slices.md) R07, R13, R16, R20, R26, R27, R27c, R27d | Limits per connection, the process tree and the cgroup path in CI; the serving layer's webhook end, the Store change source, a file connection kind; signatures; the embedded runner's account; a per-package account; the manifest generated in a build step, a release cut and installed; a task and a replication under a real Windows service | R13 PARTLY |
| [28c](28c-engines-on-runner.md) E06f, E17, E21, E22 | Splitting the Sync integration CI job (recorded only); Sync's state on the target; interactive writes as manual tasks; retiring the old paths (`ProcessSupervisor`, `BuildScheduler`, the loopback state) | E21 OWNER (optional) |
| [28c](28c-engines-on-runner.md) E14, E16, CW02 | The rest of Sync on modules; Build tracking's remainder; the primary-key-update remainder | PARTLY |
| [29a](29a-state-slices.md) Q20, Q22, Q25 | The migration function (a `package/2`); Sync's processes on the runner-owned state; deleting the old state path | Q20 PARTLY |
| [27a](27a-knowledge-slices.md) IX03 to IX06, IX12, IX16 to IX24 | OAuth 2 authorization code, SFTP, SMB and object storage, lineage nodes for outside systems, templates, the integration kind in the Workspace | IX20 to IX24 and the W rows follow the Workspace |

## Workspace

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [33](33-fewer-services.md) SC20g, SC21d, SC22e | The hand-over of the Build, Store and Sync authoring routes | ON HOLD |
| [33](33-fewer-services.md) SC20e, SC20f, SC20j, SC20k, SC20n, SC20o, SC21f, SC22d, SC22h, SC22j, SC22k, SC22l | Build publishing to a Gateway, a browser story of the screens, the catalog cache's producer, model files from cached tables, the Views tab opened in a browser, the database-facing screens, adoption, Sync screens' stories, test a connection, provisioning plan, scripts and platform forms, Sync's run actions | SC20e, SC20o, SC21f, SC22l OWNER; the rest PARTLY or not built |
| [33](33-fewer-services.md) SC14 | The Windows install job of the suite release has never run | needs a Windows machine |
| [22a](22a-gap-slices.md) GP36 | Agents through MCP, every step approved | PARTLY |
| [27a](27a-knowledge-slices.md) AG16, AX13, KN20, KN29, KN30, IX20, IX24, BK06 | The W rows: the agent, action, knowledge, search and integration editors in the Workspace, links to workspaces and pull requests | follow GP35 |

## Build

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [25](25-surrogate-keys-and-agent-tests.md) K07, K10, K11, K12, K06b | Consumers of inferred relationships, `key_map`, Store ids as `BINARY(16)` and `uuid`, keys in pure SQL, the uniqueness check as a `check/1` | none blocked; K11's drafts exist |
| [18](18-lineage.md) G06b | A job node per load (decided against for now); the server path over a real tracking copy untested | |
| [Build OPEN-ITEMS](../../DbDataBuild/docs/progress/OPEN-ITEMS.md) section S; [12](12-shared-tls-rollout.md) items 10 and 11; [Gateway README](../../DbDataGateway/README.md) Fabric (Not supported yet); [windows-test-install](../windows-test-install.md) | K1: Build's `SourceCatalogReader` (needs a schema-wide keys read in the shared readers first) and the ODBC and DuckDB opens; K2: Sync's `service`, `cert` and `secret` verbs on the shared commands (needs a Windows host); D11: Build's CLI on the shared router; F1: Fabric through the Gateway (needs a tenant) | D11 OWNER (deferred); K2 LOW (plan 12) |
| [14a](14a-semantic-slices.md) SL03, SL10, SL22 | Build reads the registry; metadata documents with semantic models; the `semantic` noun | |
| [27a](27a-knowledge-slices.md) AG01 | The suite-wide command-spec test with a golden of offered MCP tools: built (`CommandSurface`) and kept for the console; the other products add their own copy | PARTLY |

## Sync

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [28c](28c-engines-on-runner.md) E17, E22 | State on the target, retiring the old paths | |
| [29a](29a-state-slices.md) Q22, [29b](29b-state-migration-slices.md) C05, C07 | The runner-owned state for processes; the state API; the shared `state` section | |
| [07](07-data-transfer.md) TR04 | Sync adopts the write gate | LOW |
| [18](18-lineage.md) G07b | The target table's own upstream when Sync is not its only writer; runs on column edges | |
| [14a](14a-semantic-slices.md) SL04 | Sync reads the engine registry | |

## Store

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [15](15-master-data-and-documents.md) | Everything listed there | LOW |
| [21](21-channels-and-previews.md) CH02 to CH06 | The Store's part of releases and inherited reads | |
| [26](26-app-publishing.md) Z12b, Z13b | Shared entities in `column` mode, instance-scoped tokens and roles, retype and mode moves, the consumer refusal | |
| [18](18-lineage.md) G12 | The Store's documents, extractions and promotions (the Store has none yet); the apps host's own graph | |
| [27a](27a-knowledge-slices.md) EA03, EA04, BK01, BK02 | Orphaned attributes, both kinds in search, the backlog module | not built |
| [20](20-serving-layer.md) O08b | Entities as Flight SQL sources | |

## Shared and cross-cutting

| Plan and row | What remains | Flag |
| --- | --- | --- |
| [22a](22a-gap-slices.md) GP27 to GP29 | OpenTelemetry: the library, the hosts, traces across servers | |
| [22a](22a-gap-slices.md) GP23 | Descriptions and owners in lineage nodes | |
| [24a](24a-upgrade-and-cache-slices.md) UC06, UC07 | The expand-then-contract check of state migrations; the N and N-1 compatibility CI job | |
| [27a](27a-knowledge-slices.md) AX01 to AX12, BK05 to BK11 | Actions (one script format, callable and served, principals, grants, audit) and the backlog's links, notifications, automatic items, assistant and agent hand-off | not built |
| [7](07-data-transfer.md), [12](12-shared-tls-rollout.md) | See "Held and parked" | LOW |
