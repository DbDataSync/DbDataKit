# Plan 27: knowledge, agents, integrations and work management

Status: **proposed, high level** (2026-11-03; integrations and actions reworked on 2026-11-04 after the owner's decisions, with the
runner in [plan 28](28-runner.md)). Four new areas the owner asked for. This page decides their shape and how they fit together
and with plans 16, 20, 22, 25 and 26. It lists the questions only the owner can answer (section 8). Not sliced yet: each area gets a
detailed plan of its own (section 9) once the questions are answered.

| Area | What it is for |
| --- | --- |
| **DbDataKnowledge** | what agents know: on the back end, to build new objects that fit what exists; on the front end, to answer questions and write queries |
| **DbDataAgents** | an agent definition: its purpose, the context and knowledge it reads, the tools it may call, the model it uses and how it is tested, for business users on the front end or developers on the back end |
| **DbDataIntegration** (section 4) | a product of its own: integrations with systems outside the suite's databases (web APIs, file shares, object storage), into the suite and out of it, as ordinary code published to the runner ([plan 28](28-runner.md)) |
| **Actions** (section 4.4) | scripts in apps and agents as well as Sync, on one shared script format: agents' custom tools and apps' custom server-side logic, including elevated actions that hold the permission to the data while people hold the permission to run them |
| **DbDataBacklog** (working name; section 5) | backlog, sprint and work management, tied to the suite's own objects |

## 1. What already exists, and what this plan adds

The four areas are mostly new *definitions* on machinery the suite has or has planned. This plan adds as little new machinery as it can.

| Needed | Exists or planned | Gap this plan fills |
| --- | --- | --- |
| A tool for every feature | the command surface (`DbDataKit.Cli`: command specs, effect classes, JSON schemas, generated MCP tools; plan 13 W4a); "no feature without a command spec" (plan 16 section 1) | tools that are not commands: actions (section 4) |
| An agent runtime | plan 16 section 1: `DbDataKit.Agents` on `Microsoft.Extensions.AI`, approvals, acting as the user, transcripts (**not built**); testing with a scripted fake and recordings (plan 25 section 2) | the **agent definition** as a versioned, published object (section 3) |
| What agents know | catalog and glossary on the lineage graph (plan 22 section 3); semantic models (plan 14); Build metadata and `project agent-kit`; DbDataStore models (plan 15) | curated knowledge, verified queries, retrieval, and access rules on what an agent may read (section 2) |
| Custom code | `DbDataKit.Scripting` (Roslyn `.csx` with `#r "nuget:"`, built, used by Sync's scripts, libraries and drivers) | integrations with outside systems as their own product, a shared script format for actions, and the runner (section 4, plan 28) |
| Schedules, runs, alerts | `DbDataKit.Scheduling`, `DbDataKit.Monitoring` (plan 13 W10), the delivery service (plan 22 section 5) | the runner hosts them for integrations (plan 28) |
| Authoring and publishing | shared authoring (plan 22 section 1: workspaces, editor, commit, merge, publish); the serving layer runs from published definitions (plan 20) | agents and integrations as new kinds of authored project (section 6) |
| Managed business data with history, approval and forms | DbDataStore (plan 15), apps and portals (plans 16, 26) | the work-management model and its app (section 5) |

## 2. DbDataKnowledge

**Goal.** One place an agent asks "what do I need to know to do this well", answered with what the suite already knows plus what people
wrote down, filtered to what the person the agent acts for may see.

### 2.1 What it holds

| Kind | Examples | Comes from | Kept as |
| --- | --- | --- | --- |
| **Generated** | tables, columns, types, relationships, keys, lineage, semantic members and their descriptions, DbDataStore entities, app manifests, value samples and profiles | the definitions and the catalog (plan 22 section 3); never typed twice | derived, refreshed when a definition is published |
| **Curated, business** | glossary terms, metric definitions, business rules ("an active customer is..."), known data issues | business owners | DbDataStore entities, with history and approval (the glossary of plan 22 section 3 is one of them) |
| **Curated, technical** | naming conventions, modelling patterns, "how we build a slowly changing dimension here", review checklists | developers | files in a git project, reviewed like code |
| **Verified queries** | a question, the semantic query or SQL that answers it, who verified it and when | people, or an agent's answer a person approved | DbDataStore entities; also the test set for front-end agents (section 3.4) |
| **Documents** | specifications, policies, meeting notes | DbDataStore documents (plan 15 section 6), with extraction | the document plus its extracted text |
| **Learned** | an agent proposes a new term, a correction or a verified query while working | agents | a DbDataStore **draft** (plan 21), kept only once a person approves it, as with agent-built mappings (plan 25 section 3) |

### 2.2 How it is read

1. **Search and show, as commands.** `knowledge search`, `knowledge show`, `knowledge related` on the command surface, so the console, MCP
   clients and embedded agents use the same tools. They extend plan 22's `catalog search` and `catalog show` rather than sitting beside them.
2. **Retrieval in two parts.** Full-text search always, on the state database's engine. Semantic (embedding) search optional, through
   `Microsoft.Extensions.AI`'s embedding abstraction and a vector index (PostgreSQL with pgvector, SQL Server's vector type, DuckDB's
   vector extension). An install without a model provider still has full-text search. **Owner question 4.**
3. **Access rules apply to knowledge.** An item about a table, a model or an entity is visible only to someone who may read that object.
   This uses the policy engine of plan 22 section 2, so an agent cannot learn through knowledge what its user could not query.
4. **Served by the serving layer** as a `knowledge` module (reads only), published from the back end like every other definition (plan 20
   principle 1). Curation (writing terms, approving drafts) happens where DbDataStore's writes already happen.

### 2.3 Product or library

Knowledge has no runtime of its own beyond an index and a module. **Proposed:** DbDataKnowledge is a set of DbDataStore models shipped
with the suite, a shared library (`DbDataKit.Knowledge`: the index, retrieval, the access filter), a serving module, and console screens.
It is **not** a new server. **Owner question 1.**

## 3. DbDataAgents

**Goal.** An agent is a **definition**, versioned and published like an app, not a prompt pasted into a chat. The same definition runs
in the console's chat, in an app's assistant panel, and for an outside client over MCP.

### 3.1 The definition (`agent/1`)

| Part | What it says |
| --- | --- |
| Identity | name, description, owner, version |
| Audience | **front end** (business users: answer questions, write and run queries, fill in forms) or **back end** (developers: build models, replications, Store models, apps, integrations) |
| Instructions | the agent's own instructions, in files in its project |
| Knowledge | which knowledge collections and kinds it searches (section 2), and context it always gets (for example "the Sales semantic model") |
| Tools | an allow-list of commands and actions (section 4.4), named by spec; never wider than its user's roles |
| Model | provider and model, or "the install's default", chosen from the providers an administrator allows (plan 16 section 1) |
| Approvals | which effect classes need the user's approval at each step (section 3.3) |
| Limits | turns, tool calls, tokens and cost per conversation |
| Output | which view kinds it may return (plan 16 section 2a: tables, matrix grids, Vega-Lite) |
| Tests | scripted conversations and an evaluation set (section 3.4) |

### 3.2 Where it runs

- **The runtime is `DbDataKit.Agents`**, plan 16 section 1 as written. This plan adds loading a published `agent/1` into that runtime.
- **Front-end agents run in the serving layer** (the `mcp` module, plan 20), so they work while the back ends are down, and they reach
  data as the signed-in user.
- **Back-end agents work in an authoring workspace** (plan 22 section 1): a branch, edits, a build and preview, then a commit and merge the
  user approves. They never change a published definition directly.
- **Keys and model calls stay on the server**, never in the browser (plan 16).

### 3.3 Approvals

Plan 16 decided "every action approved as it happens, to be refined later".

**Decided (owner, 2026-11-03): prompting is configured per action.** Each tool an agent may run (a command, or an action from section 4)
has its own setting for whether the person is asked before it runs. There is no single rule by effect class.

To settle in the detailed plan (owner question 5):

- the settings (for example: always ask; ask once per conversation; do not ask);
- where the setting lives: on the action or command itself as its default, on the agent definition per tool, or both, with the agent's
  setting overriding the action's;
- defaults: proposed from the effect class (reads do not ask; writes, definition changes, outward-facing steps and elevated actions ask);
- whether an administrator can set an install-wide floor that no definition can lower (for example, destructive steps always ask).

### 3.4 Testing and evaluation

1. **Behaviour** is tested as in plan 25 section 2: a scripted fake model, recorded provider responses, golden transcripts, approval
   gates.
2. **Quality** is measured with an evaluation set: questions with known good answers, drawn from verified queries (section 2.1). It is
   run against a real model by hand or on a schedule, never as a pull request gate. A published version records its last score.

## 4. Integrations, actions and the runner

**Decided (owner, 2026-11-04)**, after comparing extending Sync's scripting with a separate integration service:

1. **Integrations are a product of their own, DbDataIntegration** (section 4.11). It **publishes to the runner** ([plan 28](28-runner.md)), a
   service for scheduled and batch work that runs each task in a separate process by default, the way products publish to the serving
   layer.
2. **Integrations are ordinary code** with shared run-time services (logging, retry, state, lineage, scheduling), not a generic engine
   configured for every case (section 4.1).
3. **Actions are scripts in apps and agents, as well as in Sync**, on one shared script format (section 4.4). They run in the serving
   layer; they are not part of DbDataIntegration.
4. **Sync and Build may move onto the runner later**, in a migration planned separately (plan 28 section 8). It is not part of these new
   products.

This answers owner question 12 (one area or two): two products and a shared runtime, with the script format shared between actions and
Sync.

**Scope, as the owner set it (2026-11-03).** "ETL" here means **integration with systems outside the suite's databases**: web APIs,
file shares, object storage and the like, moving data **into** the suite and **out** of it. Sync already covers database-to-database
work, so integrations are the counterpart for everything that is not a database.

**Security posture (owner, 2026-11-04), for this plan and the suite.** We provide simple, clear options, guardrails and defaults, but
administrators decide what the system does. We do not block a choice because it could be a security concern; we make it **explicit and
clear** to the person making it, and **default to secure** where we can (plan 28 section 5). Every rule in this section that limits what
code may do is therefore a **secure default** an administrator can change with a recorded, confirmed choice, unless the detailed plan
shows it cannot be (an audit record, for example, is always written).

### 4.1 Integrations: code first

**Why code (owner, 2026-11-04).** A generic system cannot handle enough variety. Sources and targets come in many file formats (JSON,
CSV and TSV, XML, Excel, fixed width, compressed or encrypted files) and are sent or fetched over HTTP, SFTP, FTPS, SCP and CIFS/SMB, each
with its own sign-in, paging, naming, partial files and acknowledgements. Extending Sync to cover them would be possible, and some
integrations would gain from Sync's change tracking, but it is better to write ordinary code and give it common support. So:

- **An integration is a package of tasks** published to the runner: a manifest (tasks, schedules, triggers, the connections each needs)
  and code in any of the runner's kinds (plan 28 section 4: a `.csx` script, a compiled .NET project with NuGet dependencies, any
  executable speaking the runner protocol, or a .NET notebook orchestrating a project's APIs).
- **DbDataIntegration** is where integrations are authored, built, tested, versioned and published: the back end for them, on
  shared authoring (plan 22 section 1).
- **Templates, not an engine.** Common cases ("pull pages from a REST API into a staging table", "pick up CSV files from a share") are
  project templates that generate ordinary code an author then owns.

### 4.2 Directions and patterns

| Pattern | What it does | Where data lands or comes from |
| --- | --- | --- |
| **Inbound pull** | read an API or a share on a schedule, incrementally | DbDataStore entities through the store module's API (validated writes, history, audit), staging tables in a suite connection (through `DbDataKit.Transfer`) for Build to model, or DbDataStore documents for files |
| **Inbound push** | an outside system calls a webhook | the same destinations; where webhooks are received is plan 28 question 4 |
| **Outbound push** ("reverse ETL") | send suite data to an outside system on a schedule or on change | from a semantic query, a DbDataStore read, or a table or view a Build model owns |
| **Outbound file** | write an extract to a share, SFTP or object storage | the same sources |
| **Requested** | a task an action enqueues (section 4.4): create a ticket, send a document | whatever the task does |
| **Chained** | an integration task that starts when another run ends | runs on the same runner; Sync and Build runs join when they move (plan 28 section 8) |

Two-way synchronization with an outside system of record (conflicts, which side wins) is a later step, decided per system.

### 4.3 The integration kit

What every integration gets, so none writes it again. The .NET kit wraps the runner protocol (plan 28 section 4.1); other languages use
the protocol directly.

| Service | What the code gets |
| --- | --- |
| Connections | outside connections (section 4.6) and suite connections, resolved with their secrets and bound per channel |
| State | checkpoints (where a task got to in a source, plan 29 section 4.1), a ledger of processed files keyed by name and hash, idempotency keys for pushes, so a rerun neither loses nor repeats work |
| Retry | policies with back-off for errors a connection classifies as temporary; rate limits per connection; a store for records an outside system refused, and replay from the last good point |
| Logging and metrics | structured logs and run metrics in the runner's run record, secrets redacted |
| Lineage | "read from" and "wrote to" calls that record outside systems as lineage nodes (plan 18) |
| Landing data | rows to a staging table (`DbDataKit.Transfer`); rows to DbDataStore through its API; files to DbDataStore documents |
| Change detection | Sync's ideas as a library: row hashing and comparison with the last run, for sources with no change feed |
| Clients, as libraries | HTTP with authentication, SFTP and SCP, FTPS, SMB; readers and writers for CSV and TSV, JSON and JSON Lines, XML, Excel, Parquet. Vetted packages, wrapped thinly, never a framework the code has to fit |
| Testing | recorded HTTP responses, a fake file share, a dry run that reads but writes nothing (section 4.10) |

Schedules, triggers, monitoring, notifications and retention come from the runner itself (plan 28 section 3).

### 4.4 Actions: scripts in apps, agents and Sync

**An action is one typed operation**: an input schema, an output schema, an effect class, and a body. It may have side effects. Actions
are agents' custom tools and apps' custom server-side logic (owner, 2026-11-04: scripts within apps and agents, in addition to Sync).

- **One script format.** Sync's script manifest (`scripts/<name>.yaml`: a name, a `kind`, a language of C# or SQL, declared parameters,
  beside its code and lock file) is generalized and moved to shared code. It gains input and output schemas, an effect class and the
  caller's context. Sync's slots stay kinds of it, run by Sync's engine as today; apps and agents add a *callable* kind.
- **A body** is a `.csx` script, SQL against a suite connection, a call to a command, or, for elevated actions, a declarative form
  (section 4.5).
- **A callable action is a tool by construction.** It has a spec like a command, so the command surface turns it into an MCP tool, a
  tool an agent definition can allow, and an endpoint apps call through the serving layer, under the same access rules.
- **Callable actions run in the serving layer.** Declarative ones run in process. Code runs by default in a pool of worker processes kept
  warm, with time and memory limits and no outside network access; an administrator can change these defaults (the security posture
  above).
- **Long or outward work goes to the runner.** An action that needs an outside system or a long job enqueues an integration task (plan
  28 section 7) and returns; the result comes back as a status, a DbDataStore change or a notification.
- **Life cycle.** Actions for apps and agents are authored in workspaces, versioned, approved and published (plans 21, 22, 26). Whether
  Sync's scripts move to the same life cycle or stay in Sync's configuration repository is for the detailed plan.

### 4.5 Elevated actions: permissions held by the action, not the user

**Confirmed by the owner (2026-11-03)** as written here; who approves the grants is still owner question 11.

**The need (owner, 2026-11-03).** Some workflows should let a person make one specific change to DbDataStore data that they have no
permission to make directly. **The permission to read and write the data is assigned to the action. The person is given permission to
run the action.** The action checks each request and then applies it or rejects it. Examples:

- a requester may move their own expense claim from *Draft* to *Submitted*, but may not edit the claim after that or write to the claims
  entity at all;
- an approver may set the status and a comment on a claim, and no other field, and only on claims of their own team;
- a manager may add a new starter to an HR entity through an onboarding app without general write access to that entity;
- anyone may see how many tickets are open per team, without being able to read the tickets.

**Not tied to an app.** The action is the unit of permission, so the same elevated action can be called from several apps, from an agent
acting for a person, or from the command surface, and the person's right to run it is the same everywhere. An app's manifest names the
actions it calls; it does not grant anything.

**How it fits plan 16's rules.** Plan 16 says an app is untrusted UI, and that an app's token is "never wider than the person". Both
stay true: the app's code in the browser holds only the person's own permissions, narrowed by the manifest. Elevation exists only inside
**server-side actions**, which are trusted code (section 4.7). The rule therefore still lives on the server, as plan 16 requires; it is
written in the action instead of in a role. This is the same pattern as a stored procedure that runs with its own rights and is granted
`EXECUTE` (`EXECUTE AS`, `SECURITY DEFINER`).

**The two grants:**

1. **To the action: what it may do to the data.** Each elevated action has an identity of its own in the identity home (an action
   principal), per channel. It is granted, as narrowly as DbDataStore's permissions allow, the entities, the operations (read, create,
   update, change request), and where possible the fields and row conditions. An administrator and the **data's owner** (the DbDataStore
   model's owner) approve these grants, with an audit (owner question 11).
2. **To people: who may run the action.** People and groups are given *run* on the action, in the identity home like any role
   assignment, so the same directory groups and DbDataStore grants (plan 22 section 2) can be used. Whoever may assign this is owner
   question 11 too.

An action that is not marked elevated has no grants of its own and runs as the person, as before.

**The action decides: validate, then apply or reject.**

1. **The caller is part of the input.** The action receives the person's identity and attributes (plan 22 section 2), so it can enforce
   "only your own claims" or "only your team's rows".
2. **It reads the current state** with its own grants and checks the request against it: the row exists, it is in the right state, the
   caller is allowed this transition, the new values are acceptable.
3. **It applies or rejects.** A rejection returns a reason the person (or the agent) can act on, and changes nothing. An application
   writes through DbDataStore's normal write path in one transaction, so the **model's own validation still runs** after the action's
   checks: the action can add rules, never skip the model's.
4. **The declarative form** makes the common checks one line each (`where: owner = caller.id`, `from_state: Draft`, `to_state:
   Submitted`, `fields: [status, comment]`), so most elevated actions need no code. Checks that need more are a `.csx` body.

**Rules that keep it safe:**

- **A person runs the action; they never get its grants.** They receive the action's result, shaped by its output schema, never a token or
  rows the action was not written to return.
- **No escalation:** an elevated action cannot change grants, roles, definitions or model structure, and cannot call another elevated
  action unless its definition names it.
- **Audit names both:** every write records the action and its version, its principal, and the person who asked (and the app or agent it
  came through). DbDataStore's history shows "Alice ran *submit-claim* v3, from the Expense app". "Who changed what" stays answerable.
- **Approval when published:** an elevated action is trusted code and needs the administrator's approval of that exact version
  (section 4.7, plan 26). A change to its grants or its body is a new version, approved again.
- **Agents:** an agent acting for a person can call an elevated action only if that person may run it. Whether the person is asked first
  is set per action (section 3.3); elevated actions ask by default.
- **Channels:** grants are per channel. In a preview, an elevated write goes to the preview's draft layer (plan 21), never to production.
- **Limits:** per-caller rate limits, and every rejection is logged with the reason.

**When not to use it.** If a workflow is "a person proposes, someone with the right role approves", DbDataStore's change requests (plan 15)
already do that without elevation. Elevated actions are for steps where the action's own checks are the control: a narrow change that
people make themselves, without anyone approving each one.

**Beyond DbDataStore.** Elevated reads of semantic models (an aggregate without row access) follow the same pattern, but are later work.
The first version covers DbDataStore entities.

### 4.6 Connections to outside systems

**Proposed:** an outside system is a **connection** like a database, a new kind in the existing `connection/1` format rather than a
second format. It then shares what connections already have: secret references for keys, passwords and OAuth refresh tokens (plan 13
W13); per-channel binding, so a preview never reaches production by accident (plan 21); and the console's connections view. Sign-in
methods to cover: API key, basic, OAuth 2 client credentials, OAuth 2 authorization code with stored refresh tokens, client
certificates, SSH keys, Windows (SMB with Kerberos). **Owner question 10.**

### 4.7 Trust, isolation and channels

`DbDataKit.Scripting`'s syntax guard is not a security boundary, so integration code and action code are **trusted code**. The defaults:

- written by people with the Author role, in a workspace, reviewed and merged;
- a published version is approved by an administrator, with an audit, as plan 26 does for trusted apps;
- run in a separate process with time and memory limits;
- packages only from the allow-listed sources, pinned in a lock file (13a S12).

Outside network access is the administrator's to configure, in the environment and on the runners (plan 28 section 5): the runner reports
what a runner can reach, and the console shows it. Actions in the serving layer have no outside access by default.

An action called by a person runs **as that person**, so access rules apply to its reads, unless it is an elevated action (section 4.5).
A scheduled integration task runs as a **service account** of the identity home (plan 16, "Sign-in decisions" item 2).

**Channels (plan 21).** Writes are never inherited from production, and that matters more here than anywhere: an integration in a preview
channel must not send production data to a real supplier or create real tickets by accident. By default, an outbound step in a
non-production channel uses the connection bound for that channel (a sandbox endpoint), or is refused when there is none. An administrator
can bind a production endpoint to a non-production channel, as an explicit, recorded choice.

### 4.8 Not a third data engine

Sync replicates between databases and Build models inside them. An integration covers what is outside them. A database-to-database copy
is a Sync replication, and a transformation inside the warehouse is a Build model. An integration does not redo them; once they run on
the runner (plan 28 section 8), an integration task can be chained with them.

**What it changes for Sync** (a clean break is allowed, never a silent one): Sync's scripts move to the shared script format (section
4.4). The manifest gains input and output schemas, and an old manifest is refused with a message naming the file. Drivers stay Sync's,
compiled by the same engine.

### 4.9 Agents in integrations, and agents writing them

- **An integration task can ask an agent** (triage a failed load, summarize a change, match records). No user is there to approve each
  action, so by default an unattended agent step **only reads and proposes**: what it would change becomes a draft or a work item
  (section 5). **Owner question 6.**
- **Agents can write integrations and actions**, from the templates, in a workspace like any author, with each step prompted as its
  action is configured (section 3.3); publishing still needs the administrator's approval of trusted code.

### 4.10 Testing

Recorded HTTP responses and a fake file share in unit tests, as plan 25 does for model providers; a dry run that reads the outside
system but writes nothing; a run against the channel's sandbox connection before a version is promoted.

### 4.11 The name: DbDataIntegration

**Decided (owner, 2026-11-04): DbDataIntegration.** DbDataAutomations was the working name while integrations and actions were one area.
Others considered: DbDataIntegrate, DbDataIntegrations, DbDataBridge, DbDataExchange, DbDataConnect. The words inside are fixed:
**connection**, **integration** (a package), **task**, **action**, **script**.

Its progress (checkpoints, processed files, idempotency keys, refused records) is kept in a state database, the runner's progress store
([plan 29](29-unified-state.md) section 4).

## 5. DbDataBacklog: work management

**Name.** **DbDataBacklog** is the working name (owner, 2026-11-03); the final name is undecided. "Backlog" names one view of the tool. Other candidates: DbDataWork (plain, and it covers backlog, sprints and boards),
DbDataBoard, DbDataTrack, DbDataTasks. DbDataPlan is ruled out, since Build already uses "plan" for its plans. **Owner question 2.**

**Goal.** Backlog, sprint and work management for teams that build on the suite, where a work item can point at the thing it is about.

### 5.1 Built on the suite, not beside it

**Proposed:** the work tool is a **solution** made from the other parts, and adds no new server:

| Part | Built with |
| --- | --- |
| Work items, epics, sprints, boards, comments, links, states and their transitions | DbDataStore models shipped with the suite: history, approval, audit and access rules come with them |
| Attachments | DbDataStore documents |
| The screens: backlog, board, sprint, item | an app (plan 26) on the shared framework, with declarative grid, form and board views; a portal for teams that want it alone |
| Notifications and digests | the delivery service (plan 22 section 5) |
| Automatic items: a failed run, a check that blocks, an approval waiting | integration tasks on the runner (section 4) |
| An assistant: write and split items, estimate, plan a sprint, summarize a sprint | an agent definition (section 3) |

This also tests the platform: anything the work tool needs and the suite lacks is a gap in DbDataStore or the app framework, fixed there.

### 5.2 What makes it worth having in this suite

1. **Links to suite objects:** an item can name a Build model, a Sync replication, a semantic model, a Store entity, an app, a run or a
   change request, and those objects show their open items.
2. **Links to workspaces:** an item can open an authoring workspace (a branch), and the item follows its commits, merge and publish
   (plan 22 section 1). A back-end agent can take an item, work in its workspace, and hand it back for review.
3. **Knowledge from finished work:** a closed item's resolution can be proposed as a knowledge draft (section 2.1).

### 5.3 Scope

Whether it replaces Jira or Azure Boards for its users, or links to them (import, two-way sync), changes its size a lot. **Owner
question 7.**

## 6. How the four fit the suite's layers

| | Back end (definitions, authoring, scheduled work) | Serving layer (runtime, end users) | Console (administration) |
| --- | --- | --- | --- |
| Knowledge | curated technical knowledge in git; index builds | `knowledge` module: search, show, access-filtered | screens for collections, drafts and approvals |
| Agents | agent projects in workspaces; evaluation runs | front-end agents in the `mcp` module; chat in apps | the console's chat; providers, models, limits, transcripts |
| DbDataIntegration | projects in workspaces, builds, tests, publishing; tasks **run on the runner** (plan 28) | (nothing of its own; webhooks are plan 28 question 4) | connections and their bindings, approvals of trusted versions; runs and schedules through the runner |
| Actions | actions in app and agent projects; Sync's scripts in Sync | **action calls** from apps and agents: declarative in process, code in warm worker processes | approvals of trusted versions, grants of elevated actions |
| Runner (plan 28) | | | runner groups, runs, schedules, pause and run-now |
| Work | (nothing of its own) | the work app on the `store` and `apps` modules | the app's administration like any app |

**Back-end servers.** Agents and integrations each need somewhere to author, build, test and publish; scheduling is the runner's
(plan 28). Two ways:

- **A.** One back-end server per product (agents, integrations), the shape of every other product.
- **B.** One back-end server for authored code: apps (plan 22's DbDataApps back end), agents and integrations are all projects authored in
  workspaces, built, tested and published (apps and agents to the serving layer, integrations to the runner). Each keeps its own name, nouns and
  screens.

**Owner, 2026-11-03: not decided yet; the plans are not specific enough.** Decided since (2026-11-04): **integrations are a product of
their own that publishes to the runner**, and the runner is a service of its own (plan 28). What remains open is whether agents share a
back end with apps, and whether DbDataIntegration's back end is shared with them (option B) or stands alone. **Owner question 1.**

## 7. Order

| Wave | What | Needs |
| --- | --- | --- |
| 0 | What plan 16's agents need: the suite-wide command spec test, `DbDataKit.Agents` with the scripted fake, the provider proof against the four providers | 13a W4a work; plan 25 section 2 |
| 1 | `agent/1` and one front-end agent: questions over a semantic model in the console's chat, with knowledge generated from the catalog and semantic descriptions, and full-text search | wave 0; plan 22 section 3's catalog search |
| 2 | Actions: the shared script format; Sync's scripts move onto it; a `.csx` or SQL action as a tool of an agent and an endpoint for an app, in warm worker processes in the serving layer | wave 1; the serving contract (plan 20 section 10) |
| 3 | Curated knowledge in DbDataStore (glossary, metrics, verified queries), drafts from agents, evaluation sets; embedding search if chosen | DbDataStore drafts (plan 21); access rules (plan 22 section 2) |
| 3b | Elevated actions: action principals, grants to actions with field and row conditions, *run* grants to people, validate then apply or reject, the declarative form, audit of both identities | wave 2; DbDataStore permissions (plan 15); the identity home's service principals (13a U20) |
| 4 | The runner (plan 28) with its progress store on the unified state structure (plan 29), then DbDataIntegration: outside connections, the integration kit, templates, inbound and outbound patterns, webhooks; actions that enqueue integration tasks; back-end agents in workspaces | shared authoring (plan 22 section 1) |
| 5 | The work tool: models, app, integration tasks, assistant | DbDataStore relationships and documents; the app framework's declarative views |

Waves 1 and 2 can run side by side once wave 0 is done; wave 5 can start its DbDataStore models as soon as DbDataStore can hold them.

## 8. Questions for the owner

| # | Question | Proposed |
| --- | --- | --- |
| 1 | Which of these are servers? | **Partly decided** (owner, 2026-11-04): integrations are a product of their own, publishing to the runner, a service of its own (plan 28). Open: whether agents and DbDataIntegration share a back end with apps; proposed earlier: knowledge and the work tool are not servers (section 6) |
| 2 | The work tool's name | **Working name DbDataBacklog** (owner, 2026-11-03); final name undecided |
| 3 | Languages and packaging | **Decided for integrations** (owner, 2026-11-04): `.csx` scripts, compiled .NET projects, any executable through the runner protocol, and hybrids such as a .NET notebook over a project (plan 28 section 4). Actions: `.csx`, SQL, command calls and declarative elevated forms |
| 4 | Embedding search | optional per install; full-text always |
| 5 | Agent approvals | **Decided** (owner, 2026-11-03): prompting is configured per action. Open: the settings, where they live, defaults, an install-wide floor (section 3.3) |
| 6 | Unattended agents in integration tasks | read and propose only; changes become drafts or work items |
| 7 | The work tool's scope | its own items first; links to GitHub pull requests and workspaces; import from Jira or Azure Boards later; no two-way sync |
| 8 | First audience | front-end agents first (wave 1), since the semantic layer and catalog are closest to ready |
| 9 | The integrations product's name | **Decided** (owner, 2026-11-04): DbDataIntegration (section 4.11) |
| 10 | Outside systems as connections | a new kind in `connection/1`, sharing secrets, channel bindings and the console's view (section 4.6) |
| 11 | Elevated actions: who approves an action's grants on data, and who may give people *run* on it | grants on data: an administrator and the DbDataStore model's owner, both, with an audit; *run*: the action's owner or an administrator (section 4.5) |
| 12 | Integrations with or apart from actions and Sync's scripts | **Decided** (owner, 2026-11-04): apart. Integrations are a product publishing to the runner; actions share one script format with Sync (section 4) |

## 9. Detailed plans to follow

Once section 8 is answered, each area gets a detailed plan with its definitions' formats, commands, storage, tests and slices (one slice,
one branch `suite/<id>-<short-name>`, a "Done when" test, as in 13a):

| Plan | Area | New slice letter (not yet used) |
| --- | --- | --- |
| [28](28-runner.md) | The runner (written at high level, 2026-11-04) | to choose when sliced (13a and plans 19 to 26 already use A B C D E F G H J K L M N O P Q R S T U V W X Y Z and AP; D and Q are decision and step numbers, the rest slices) |
| [29](29-unified-state.md) | One structure for the suite's state databases (written at high level, 2026-11-04) | |
| 30 | DbDataKnowledge | |
| 31 | DbDataAgents | |
| 32 | DbDataIntegration | |
| 33 | Actions: the shared script format, callable and elevated actions | |
| 34 | The work tool | |
| later | Sync and Build on the runner (plan 28 section 8), after its state design | |

## 10. What this changes in other plans (once approved)

| Plan | Change |
| --- | --- |
| 16 section 1 | the agent runtime loads published `agent/1` definitions; approvals are refined per section 3.3 |
| 16 section 2 (the app is untrusted UI) and "Sign-in decisions" item 1 (a token never wider than the person) | unchanged for an app's front end; a server-side elevated action holds its own grants on the data, people hold *run* on the action, and it validates each request before applying or rejecting it (section 4.5). The rule stays on the server, written in the action |
| 15 (DbDataStore permissions) | grants to action principals, with field and row conditions; history and audit record the action, its version and the person who asked |
| 20 section 4 | new modules: `knowledge` (reads), and action calls under `mcp` and `apps`; the runner (plan 28) as the scheduled and batch counterpart |
| 13 W2, 13a S-series | Sync's script manifest becomes the shared script format, used by apps and agents too; an old manifest is refused with a message |
| 22 section 1 | shared authoring gains project kinds for agents and integrations, and actions inside app and agent projects |
| 22 section 3 | catalog search becomes part of knowledge search |
| 21 | by default, outbound integration steps in a non-production channel use that channel's connection or are refused; an administrator may choose otherwise, recorded (section 4.7) |
| 18 | outside systems become lineage nodes |
| 06 and `connection/1` | a kind for outside systems (if question 10 is agreed) |
| 25 section 2 | adds evaluation sets beside the behaviour tests |
| 26 | the work tool is the first app the suite ships |
