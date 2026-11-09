# Plan 27a: knowledge, agents, integrations and work in slices

Status: **proposed** (2026-11-09). [Plan 27](27-knowledge-agents-integrations-work.md) decides *what* DbDataKnowledge, DbDataAgents, DbDataIntegration, actions and DbDataBacklog are, and records every answer of the owner (sections 2 to 6 and the 61 rows of section 8, with the direction of 2026-11-03 and 2026-11-04). This plan does the audit that plan 27 never had (what exists on `origin/main`, item by item, with the file or test that proves it) and cuts what is left into slices in the shape of [22a](22a-gap-slices.md), [24a](24a-upgrade-and-cache-slices.md) and [28a](28a-runner-slices.md): one slice, one branch `suite/<id>-<short-name>`, one commit, "done" is the test in the last column, and the status goes in the slice's row in the same change. It reopens no decision. Where a decision is missing it says so in the questions at the end, each with a recommended default.

**The one-line result.** Of plan 27's four areas, **nothing of the product itself is built**: no agent runtime, no knowledge library, no actions, no backlog. What is built is what they stand on: the command surface and MCP for outside agents, Build's agent kit, the whole **run-time side of integrations** (the runner, 28a R01 to R20), the state engines' vector check and a test environment that has vectors, the publishing library, the JSON query request, semantic SQL and saved queries. The audit below marks each item **built**, **partly** or **not started**, and [`docs/agent-tools.md`](../agent-tools.md), [`docs/knowledge.md`](../knowledge.md) and the integrations section of [`DbDataRunner/README.md`](../../DbDataRunner/README.md) write the built half as product documents.

## Rules for every slice

1. One slice is one branch, one commit whose message starts with the slice id, and its row's status text in the same commit (CLAUDE.md, "Making changes").
2. **Every slice that makes something a person authors, publishes or configures names its editor.** Each format, definition kind or setting in this plan has a UI row in [`docs/ui-editors.md`](../ui-editors.md) (added with this plan, state `planned`) and an editor slice below, or a documented exemption in the state cell (`none (reason)`). The editor slices are the rows marked **UI** in the slice tables. A slice that adds a stored format also registers it in `docs/formats.json` (open until the first release that contains a key of it, plan 24a UC02) and adds the row that `scripts/check_ui_editors.py` then demands.
3. **Docs in the same commit.** A slice that builds a feature updates the product document that [`docs/agent-tools.md`](../agent-tools.md), [`docs/knowledge.md`](../knowledge.md) or the product's README holds for it (replacing "not started" with what exists), and the console's help gains the page when the design page of [`docs/agents/`](../agents/) becomes true.
4. **Every new command has a spec, an effect class and traits and a JSON Schema**, and the command-spec test of AG01 lists it. **Every new stored thing passes the freeze rule** (24a UC02).
5. **The three engines.** Anything that touches a state module runs its tests on SQLite, SQL Server and PostgreSQL (`testenv`), and anything that needs vectors runs on SQL Server 2025 and PostgreSQL with `pgvector` (SQLite once KN08 exists). The N-1 engine job (SQL Server 2022, PostgreSQL 16) cannot hold vectors; question QK4.
6. **No model provider in CI.** Agent behaviour is tested with the scripted fake and recorded responses (plan 25 section 2); quality evaluation needs a real model and never gates a pull request (plan 27 section 3.4).

## What exists (verified on `origin/main` at `b493ebb3`, 2026-11-09)

**How the audit was done.** Each plan item below was searched by `git grep` and `git show` against `origin/main` (code, tests, READMEs, the plans' status lines and `git log`). **built** means the item as plan 27 decides it exists and a test or document proves it; **partly** means something real exists and the cell says what is missing; **not started** means a search for the item's names found nothing, and the cell says which neighbouring thing exists, if any. A search that found nothing is stated, so the reader can repeat it (`git grep -il "IEmbeddingGenerator"` is empty, for example).

### Foundations from other plans that plan 27 builds on

| Item | Plan | Status | Evidence |
| --- | --- | --- | --- |
| Command surface: specs, effect classes, JSON Schemas, generated MCP tools | 13 W4a, C08 | **built** | `DbDataKit/src/DbDataKit.Cli/CommandSpec.cs` (`EffectClass`), `Noun.cs` (a verb without a schema is refused when the router is made), `Mcp.cs` (`McpServer`, `UiNoun`); tests `DbDataKit.Cli.Tests/McpServerTests.cs`, `CommandSpecTests.cs`; `ui mcp` in `DbDataSync/src/DbDataSync.Cli/SyncCli.cs` and `DbDataGateway/src/DbDataGateway.Server/Hosting/Cli.cs`; Build's own server `DbDataBuild/src/DbDataBuild.Cli/Mcp/McpServer.cs` |
| "No feature without a command spec" as a suite-wide test (plan 16 section 1) | 16 | **partly** | the per-router schema rule above, and Build's own test (`DbDataBuild.Tests.Unit/CliTests.cs`); no test enumerates every product's router; the Store and the Console have verbs through their own hosts, not a common test |
| Agent tools for semantic queries | 19 J15 | **built** | `DbDataKit.Semantic.Cli/SemanticNoun.cs` (`describe`, `query`, `explain`, `members`), mounted in the Gateway and offered by `ui mcp`; `docs/semantic/architecture.md` |
| Build's agent kit as a skill | Build | **built** | `DbDataBuild/src/DbDataBuild.Cli/AgentKit/SKILL.md`, `AgentKitCommand.cs`; `AgentKitTests.cs` (every command, option and code the skill names exists); `DbDataBuild/docs/agents.md` |
| Plan file hash and a separate write login as guardrails outside the model | Build | **built** | `DbDataBuild/docs/agents.md` ("the plan file is the review gate"); `DbDataBuild.Tests.Unit` plan tests |
| Runner: packages, protocol, kit, executor, limits, retries, scheduler, group, labels, triggers, posture, publishing library, staging and activation, console's embedded runner | 28a R01 to R20 | **built** (R07 library half, R16 partial: no signatures) | `docs/plans/28a-runner-slices.md` rows with `Status: done`; `DbDataKit/src/DbDataKit.Runner*`, `DbDataRunner/`, `DbDataConsole/src/DbDataConsole.Server/ConsoleRunner.cs`; `DbDataRunner/README.md` |
| Progress store: checkpoints, steps and units, ledger, idempotency, refused records | 28b, 29a Q20 | **built** | `DbDataKit/src/DbDataKit.State/Progress/` (`ProgressStore`, `StepStore`, `UnitStore`); kit in `DbDataKit.Runner.Kit` (`StepEngine.cs`, `UnitEngine.cs`) |
| Sync's engines and Build's executor as runner packages, Sync and Build state on modules | 28a R21 to R24, 28c | **not started** | the rows in 28a and 28c carry no status; `DbDataSync.TaskRunner` still runs replications |
| Publishing library: versions, artifacts by hash, stage and activate, channels, bindings, instance status | 30, 28a R15 | **built** (library); signatures not built | `DbDataKit/src/DbDataKit.State/Definitions/` (`DefinitionKinds`, `DefinitionPublisher`, `PublishArtifacts`, `InstanceStatusStore`); kinds registered today: `semantic-model`, `entity-model`, `data-source`, `app`, `app-instance`, `app-nav`, `dashboard`, `report`, `integration` (`DbDataKit.Views/Publishing/ViewDefinitions.cs`, `DbDataGateway.Server/Serving/Published*.cs`, `DbDataKit.Runner/Hosting/RunnerPackages.cs`); the serving follower is `DbDataGateway.Server/State/DefinitionFollowerService.cs`; `README` of the runner says signatures are not built |
| Kinds of plan 27 in plan 30: `agent`, `action`, `skill`, `agent-policy`, `knowledge-project`, `build-metadata` | 30 | **not started** | plan 30 section on serving groups lists them as "later"; none is registered (`git grep -n "knowledge-project"` outside plans is empty) |
| Unified state: modules, payloads, retention, audit, notifications, lineage | 29a | **built** | `DbDataKit.State/Modules`, `Payloads/PayloadStore.cs`, `AuditLog.cs`, `NotificationFeedStore.cs`; modules `agents`, `knowledge`, `backlog` of plan 29 are **not created** (no `StateModule` named so outside the test fixtures) |
| Engine features: a module that needs vectors refuses to be created without them | 29 | **built** | `DbDataKit/src/DbDataKit.State/EngineFeatures.cs`, `Modules/StateModule.cs` (`requiresFeatures`); tests `DbDataKit.State.Tests/EngineFeatureTests.cs`, `State.Cli.Tests/StateVerbsTests.cs` (a module named `knowledge` in the fixtures); `DbDataKit.State.Engines/README.md` |
| Test engines with vectors: SQL Server 2025, PostgreSQL with `pgvector` | 27 section 2.4 | **built** | `testenv/docker-compose.yml` (`mssql` and `mssql-target` on `2025-latest`, `postgres` on `pgvector/pgvector:pg17`, N-1 profiles `mssql2022`, `postgres16`); commits `0b1962fc`, `8d942639` |
| JSON query request, relative periods, semantic SQL as input, saved queries in catalogs, a person's saved queries | 14, 19 J25 to J27 | **built** | `docs/semantic/query.md`; `DbDataKit.Semantic/QisEngine.Time.cs` (`last_complete`); `DbDataKit.Semantic/Sql/SemanticSqlTranslator.cs`; `docs/semantic/catalog.md` ("Saved queries"); `DbDataGateway.Server/State/SavedQueryStore.cs` (versions, sharing, lookup as the caller). The API a person saves through is the apps' and is not built |
| Lineage graph with stable identities, console page with search by name | 18 | **built** | `DbDataKit/src/DbDataKit.Lineage*`, `docs/lineage.md` |
| Identity home: service accounts, directory groups mapped to multi-valued attributes | 22a GP04, 13a U20 | **built** | `DbDataKit.Identity/Directory.cs` (`MapExternalGroupToAttribute`), `IdentityAdminApi.cs`; Gateway screen `IdentityAdminPage.tsx` (`identity-service-account-add`, `identity-attribute-add`) |
| Row and column access rules, grants entity | 22a GP01 to GP03, GP10 to GP15 | **not started** | deferred by the owner (22a QG2, 2026-11-07); where grants live is under review (the hold of 2026-11-08) |
| DbDataStore: validated writes, history with the actor, audit, change requests approved at the edge | 15 | **built** | `DbDataKit/src/DbDataKit.Store/ModelWriter.cs`, `DbDataGateway.Server/Serving/GroupStoreApi.cs` (202 and a change request); Store README |
| DbDataStore permissions with field and row conditions for an action principal; extension models; documents with extraction (WebDAV); drafts | 15, 21 | **not started** | `git grep -il "extension model"` is empty; the serving layer's `webdav` module is "not built" in `docs/serving-layer.md`; no Store draft layer found |
| Apps: instances, trust, app approval of exact bytes, token broker, data route with scopes from the manifest | 26 | **built** | `docs/apps.md`; `GroupAppData`, `AppTokenBroker`, kind `app-approval` |
| App assistant panel, app calling an action, apps' server-side logic | 26, 27 | **not started** | `git grep -i "assistant"` in `DbDataKit.Apps.*` is empty; the manifest has no `actions` key |
| Shared authoring library; Workspace server; app workspace | 22a GP30 to GP36 | **not started** | no `DbDataKit.Authoring` and no `DbDataWorkspace` folder; Build has its own git workspaces (`DbDataBuild/src/DbDataBuild.Cli/Server/Workspaces.cs`, `DbDataKit.Config.Git/GitWorktrees.cs`) that GP30 would extract |
| Delivery service (mail, Teams, Slack, webhooks, subscriptions) | 22a GP07 to GP09 | **not started** | no `DbDataKit.Delivery`; `NotificationFeedStore` is the in-product feed only |
| Report catalogs (31), dashboards and reports (32) | 31, 32 | 31 **not started**; 32 **in progress** | plan status lines; reports and dashboards are published kinds (`ViewDefinitions.cs`) |
| Shared web packages | 16 section 2a, 19 lane Y | **built**: `shell`, `visuals`, `store-ui`, `views-editor`, `view-app` | `DbDataKit/web/*`; no `knowledge-ui`, no agent or chat package |

### Plan 27 section 2: DbDataKnowledge

| Item (section) | Status | Evidence |
| --- | --- | --- |
| Library `DbDataKit.Knowledge`, the `knowledge/1` item format with role, scope, owner; `_knowledge.yml` defaults; subjects named as the catalog names them (2.3, 3.5c) | **not started** | `git grep -il "knowledge: 1"` finds only plans and `docs/agents/*.md`; no project named Knowledge |
| Knowledge kinds: generated, curated business, curated technical, verified queries, documents, learned (2.1) | **not started** | no reader or store for any; documents need plan 15's `webdav` (not built); learned needs Store drafts (not built) |
| Sources: git files, generated, the Workspace server's editor, documents, outside systems (2.4) | **not started** | |
| Vector search on SQL Server 2025, PostgreSQL `pgvector`, SQLite `sqlite-vec` (2.4) | **partly** | engine feature check and a vector-capable test environment are built (foundations above); no vector table, no query, no `sqlite-vec` (`git grep -il "sqlite-vec"` is empty); no index module |
| Embedding models as a connection with purpose `embeddings`: built in (ONNX Runtime), Ollama, OpenAI-compatible (3.7a) | **not started** | `git grep -il "IEmbeddingGenerator"` and `OnnxRuntime` are empty; `connection/1` has no non-database kind |
| Business knowledge in forms, kept in git: the knowledge app, suggestions at the edge, collection and approval by the Workspace server (2.5) | **not started** | the pattern it copies is built: Store change requests approved at the edge (`GroupStoreApi.cs`) and the serving group's follower; nothing of the suggestion record, the knowledge app, or the Workspace server's collection |
| Publishing `knowledge-project`; the `knowledge` module checks, embeds changed items at staging and switches at activation (2.6) | **not started** | publishing library built; kind and module not |
| Generated knowledge from published definitions, stable ids, one item per subject; `build-metadata` kind; values by opt-in under the column's rules (2.7) | **not started** | sources exist: published kinds above; Build's metadata documents (`project show metadata`, stored on `connection publish`) exist locally and are not published to a serving group; the opt-in `knowledge: values` is not in the model schema |
| Descriptions on semantic members and Store fields (2.7, "filling the gaps") | **partly** | semantic measures and attributes already have `description`, `label` and `synonyms` (`DbDataKit.Semantic/schemas/catalog.schema.json`; plan 27 says they have none, which is out of date); a Store model's entities and attributes have a `Title` only (`DbDataKit.DataModel/DataModel.cs`); no "undescribed" report |
| Chunking long references (2.8) | **not started** | |
| Verified queries (2.9) | **not started** | pieces built: the pinned form (JSON request), semantic SQL translation, saved queries, relative periods, the Store-style change request path; no `verified-query` item, no `query run-verified`, no re-verification at staging |
| `knowledge` commands (2.10) | **not started** | neighbours: `semantic describe`, `lineage graph|impact|provenance`; no `knowledge` noun, no `catalog` noun (22a GP25) |
| Knowledge app and the console's index, context and workspace-check screens (2.10) | **not started** | |
| `@dbdatakit/knowledge-ui` (2.10a) | **not started** | `DbDataKit/web/` has no such package |
| What is searchable and how: definitions as generated knowledge; workspace search; data and records through their own reads (2.11) | **partly** | the console's Lineage page searches nodes by name and id (`docs/lineage.md`); no generated-knowledge search, no workspace search, no multi-source search box |
| Access rules apply to knowledge (2.2 item 3) | **not started** | needs plan 22 section 2; directory attributes (GP04) are built, rules are deferred |
| Test environment on SQL Server 2025 with `pgvector` (2.4) | **built** | `testenv/docker-compose.yml`; the other products' suites have not been run on it (plan 27 says so) |

### Plan 27 section 3: DbDataAgents

| Item (section) | Status | Evidence |
| --- | --- | --- |
| `DbDataKit.Agents` on `Microsoft.Extensions.AI`, scripted fake chat client, provider proof against OpenAI, Foundry, Anthropic, Ollama (plan 16 section 1, 25 section 2, wave 0) | **not started** | `git grep -il "Microsoft.Extensions.AI"` outside plans is empty; `Directory.Packages.props` has no AI package |
| `agent/1` definition, agent as a git project, build checks, publish as the `agent` kind (3.1, 3.10) | **not started** | |
| Where agents run: back end in the console, front end in the serving layer's `mcp` module, unattended in a task, evaluation on the embedded runner (3.2) | **not started** | no agent code in `DbDataConsole` or the Gateway (`GatewayModules.Known` is `flightsql, identity, apps, store, views`); the embedded runner exists (R20) |
| Back-end agent first (3.2, wave 1) | **not started** | its parts exist: Build's skill, Build's MCP server, Build's workspaces, the console's relay to servers |
| Prompting per action: three settings, three layers, defaults from traits (3.3) | **partly** | effect classes and "writes are not offered unless `--allow-writes`" (`Mcp.cs`) are the only gate; no traits, no per-tool setting, no policy layer |
| Evaluation: scripted conversations, evaluation sets, judge model, scores per version, optional minimum (3.4) | **not started** | |
| Context: four homes, roles, scope, fixed assembly, recorded per turn, budget, "never cut silently" (3.5 to 3.5c) | **not started** | the design is explained in `docs/agents/context.md` and `context-window.md`, which say "not built yet" |
| Reach = the person's access narrowed by the agent's scopes and the install policy, enforced by the serving layer (3.6) | **partly** | the serving layer enforces a token's audience and scopes for apps (`docs/apps.md`, the data route); agent scopes and the install policy do not exist |
| Model providers as connections, per channel, with a slot; local servers as stand-ins for the public API in tests (3.7, 3.7a) | **not started** | |
| Transcripts and audit in an `agents` module; 30-day history (3.8) | **not started** | the audit and retention machinery is built (`AuditLog`, `RetentionJob`) |
| Limits and cost per conversation, person, install (3.9) | **not started** | |
| Memories, subagents (3.11, 3.12) | **not started** | |
| Skills: offered by name and description, loaded by a tool, the `skill` kind (3.13) | **partly** | Build's `SKILL.md` has the header and is installed and checked by `agent-kit`; no loader, no `skill` kind, no budget |
| Conversations across versions, model fallback (3.14, 3.15) | **not started** | |
| Surfaces: console chat, assistant panel, MCP, messaging, Excel add-in (3.16, 3.16a) | **partly** | MCP for outside clients (read commands as tools) is built; the rest is not; the Excel add-in is still a proposal (decision 61) |
| Untrusted content marked as data; answers validated against view schemas (3.17) | **partly** | MCP never offers secrets or row-returning `query` for the Gateway; view schemas and validators exist (`DbDataKit.Views`, `conformance/views`); nothing marks tool results as data (there is no runtime), nothing validates an agent's answer |

### Plan 27 section 4: integrations, actions and the runner

| Item (section) | Status | Evidence |
| --- | --- | --- |
| Integrations publish to the runner; packages of tasks with schedules, triggers, slots (4, 4.1) | **built** | foundations above; kind `integration` |
| DbDataIntegration as the place to author, build, test and publish integrations (4.1, 4.11) | **partly** | a .NET project is built and published by hand, from Build's Publish tab (`RunnerPublish.tsx`) or `dbdatarunner package publish`; no `integration` project kind in a workspace, no templates (`git grep -il "DbDataIntegration"` outside plans is empty) |
| Directions and patterns: inbound pull, inbound push, outbound push, outbound file, requested, chained (4.2) | **partly** | a task can be written for each with the kit; webhook receiver and after-run chaining built (`TriggerEngine.cs`, `WebhookReceiver.cs`); the serving layer's end of webhooks, outbound helpers, requested (an action enqueues) and chains with Sync and Build runs are not |
| Kit services (4.3): connections | **partly** | slots resolve to suite connections (`RunnerSlotResolver`, `ConnectionGrant`); outside kinds do not exist |
| Kit: state, retry, logging and metrics, testing | **built** | progress store; `RetryPolicy`, `RetryPlanner`; `OutputLogger` redaction; `DbDataKit.Runner.Kit.Testing` (`RunSimulator`) |
| Kit: lineage calls for outside systems | **partly** | deployment lineage is built (`RunnerDeploymentLineage`); "read from" and "wrote to" nodes for outside systems are not |
| Kit: landing data, change detection | **not started** | `DbDataKit.Transfer` exists for staging tables; Sync has row hashing (`RowHash`) in its own code; no kit surface for either |
| Kit: clients as libraries (HTTP, SFTP, SCP, FTPS, SMB; CSV, TSV, JSON, XML, Excel, Parquet, fixed width, compressed or encrypted files) | **not started** | related code elsewhere: SSH.NET inside Sync's drivers, `Parquet.Net` and `ClosedXML` as packages in Views export and semantic extraction; none is wrapped for jobs |
| Actions: one shared script format, callable actions as tools and endpoints, warm worker pool in the serving layer (4.4) | **not started** | `DbDataKit.Scripting` (compile, lock files) and Sync's manifest (`sync-script-config/1`, open) are the starting points; the Gateway already has worker processes for ODBC (`DbDataGateway.Worker`) |
| Elevated actions: principals, grants with field and row conditions, run grants, validate then apply or reject, audit of both identities (4.5) | **not started** | building blocks: identity service accounts, Store history with the actor, app approval of exact bytes |
| Connections to outside systems as a kind of `connection/1` with the sign-in methods (4.6) | **not started** | `DbDataKit/src/DbDataKit.Runner/Triggers/FileSources.cs` says no file or SFTP connection kind exists; `connection/1` is open at format 2 |
| Trust, isolation and channels (4.7) | **partly** | allow-listed packages and lock files (`DbDataKit.Scripting`), process isolation, limits, posture choices and per-channel bindings (runner), approval of trusted app versions are built; "approval of trusted action and integration versions" for actions and the rule for outbound steps in non-production channels are not |
| Agents in integrations; proposals instead of prompts (4.9) | **not started** | |
| Testing: recorded HTTP, fake share, dry run, sandbox run before promotion (4.10) | **partly** | `RunSimulator` is built; the rest is not |

### Plan 27 sections 5 and 6: DbDataBacklog, custom attributes, the Workspace server

| Item (section) | Status | Evidence |
| --- | --- | --- |
| DbDataBacklog: `backlog` state module, serving module, app, links, automatic items, assistant (5.1 to 5.3) | **not started** | `git grep -il "backlog"` finds only Sync's change-backlog code and plans |
| Custom attributes for any object type through DbDataStore extension models (5.4) | **not started** | |
| One Workspace server (DbDataWorkspace) for app, agent, integration, skill and knowledge projects (6) | **not started** | plan 22a GP30 to GP36 |
| Plan 27 section 10: the edits other plans were to receive | **not applied**, except two | plan 30 lists the kinds; plan 22a GP05 is dropped and GP03, GP10, GP23, GP24, GP26 are under review; plans 15, 16, 18, 20, 21, 25 and 26 have no mention of plan 27 (`git grep -c "plan 27"` is 0 in each) |

### Plan 27 section 8: the questions

All 61 rows are **decided** (owner, 2026-11-03 to 2026-11-06) except row 61, the Excel add-in, which stays **proposed**. They are not repeated here. The questions that the decisions leave open are at the end of this plan.
