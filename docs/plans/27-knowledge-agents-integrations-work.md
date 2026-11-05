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

**Where knowledge is kept is not decided** (owner, 2026-11-05): the "Kept as" column below is a first proposal, to be settled when this
section's design is gone through. Nothing else in this plan depends on it.

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
| Knowledge | which knowledge it may search and which it always gets, by the binding rules of section 3.5 |
| Scopes | the semantic models, DbDataStore entities and operations it may use (section 3.6); narrows the person's own access, never widens it |
| Tools | an allow-list of commands and actions (section 4.4), named by spec, each with its prompting setting (section 3.3); never wider than its user's roles |
| Model | a model **slot**, bound per channel to a provider connection and model (section 3.7) |
| Limits | turns, tool calls, tokens and cost per conversation |
| Output | which view kinds it may return (plan 16 section 2a: tables, matrix grids, Vega-Lite) |
| Tests | scripted conversations and an evaluation set (section 3.4) |

### 3.2 Where it runs

**Proposed (2026-11-05):** one runtime library, `DbDataKit.Agents` (plan 16 section 1), loading a published `agent/1` (a plan 30 kind),
in three hosts:

| Agent | Runs in | Acts as |
| --- | --- | --- |
| **Back end** (developers) | the console, where developers already work | the signed-in person, in an authoring workspace (a branch, plan 22 section 1); never on a published definition |
| **Front end** (business users, app assistants, outside MCP clients) | the serving layer's `mcp` module, so it works while the back ends are down | the signed-in person |
| **Unattended** (a step of an integration task) | the task's process on the runner | the task's service account, with section 4.9's rule |

Keys and model calls stay on the server, never in the browser (plan 16).

**First (owner, 2026-11-05): the back-end agent**, because it unlocks building apps, which the front-end agents then live in. It starts
from what exists: Build's command surface and its agent kit (`dbdatabuild project agent-kit`, `DbDataBuild.Cli/AgentKit/SKILL.md`),
which outside agents already use over MCP.

### 3.3 Approvals: prompting per action

**Decided (owner, 2026-11-03): prompting is configured per action**, for every tool an agent may run (a command, or an action from
section 4). Proposed 2026-11-05:

**27-5a, the settings:** *ask* (every call waits for the person), *ask once per conversation* (after the first approval it runs without
asking for the rest of the conversation), *do not ask* (runs at once; the only setting that runs in an unattended task, section 4.9).

**27-5b, who sets it, in layers:** the command's or action's own default (its author, in its spec); the install policy (an administrator
can set any action's setting and a minimum no agent can go below); the agent definition, per tool, within that minimum. The strictest of
the three applies.

**27-5c, defaults from the traits of what it does.** Today's effect classes are Build's (offline only, repository files only, target
read-only, target writes, target data writes, tracking tables only) and cannot say "sends something outside" or "uses elevated grants".
They become **traits**, suite-wide, that a command or action declares in any combination, each with a default:

| Trait | Default prompting | Why it is its own trait |
| --- | --- | --- |
| **reads definitions** (models, catalogs, project files, metadata) | do not ask | describes the system, not its contents |
| **reads data** (rows from a semantic query, a DbDataStore read, a sample) | do not ask | **owner, 2026-11-05: kept apart from reading definitions.** Data read by an agent is sent to its model provider, so an administrator can treat it differently: ask, or refuse it for agents bound to a cloud provider (section 3.7), and the audit can tell the two apart |
| writes definitions or files | ask once per conversation | |
| writes data | ask | |
| changes structure | ask | |
| deletes | ask | |
| outward (outside systems, email, notifications) | ask | |
| elevated (uses an action's own grants, section 4.5) | ask | |

The MCP generator (`DbDataKit.Cli/Mcp.cs`), which today knows only "reads" or "does not", uses the traits too.

**What the person sees when asked:** the command's own preview (plan 16): what it reads or changes, its traits, and for a plan or a change
request, the plan. Every approval and refusal is recorded (section 3.8).

### 3.4 Testing and evaluation

1. **Behaviour** is tested as in plan 25 section 2: a scripted fake model, recorded provider responses, golden transcripts, approval
   gates.
2. **Quality** is measured with an evaluation set: questions with known good answers, drawn from verified queries (section 2.1). It is
   run against a real model by hand or on a schedule, never as a pull request gate. A published version records its last score.

### 3.5 What an agent has in context: instructions, guardrails and knowledge

Proposed (2026-11-05), from the owner's question: what decides an agent's instructions, guardrails and permissions, and are they files
in version control or bound by rules?

**Both, by who owns them, and with one rule above the rest: anything that must hold is enforced by the runtime, never only written in
a prompt.** A model can ignore a sentence. So:

- **Hard guardrails** are enforced outside the model: permissions and access rules (section 3.6), prompting and approvals (3.3), the
  view schemas an answer must validate against, limits (3.9), what reaches outside, the plan files Build refuses to apply when edited.
- **Instructions** (soft) are text the model reads: how to work, what words mean, style, what to ask the person. Build's agent kit shows
  the mix today: its "rules that are never negotiable" are instructions, and the ones that matter most are also enforced (a plan file
  carries its own hash; the agent never holds the write login).

**Where context comes from, in four homes:**

| Home | Owned by | Kept as | Examples |
| --- | --- | --- | --- |
| **The platform** | the suite | in the product's code, versioned with it | tool results are data, not instructions; never print a secret; how to read a command's JSON document |
| **The install policy** | administrators | a published definition, kind `agent-policy` (plan 30), edited in the console | "always say an answer is generated"; data rules for agents on cloud providers; prompting minimums |
| **The agent** | its developers | files in the agent's project in git, reviewed and published | its role, how it works, its tool list and scopes; for the back-end agent, Build's agent kit |
| **Knowledge bound to subjects** | the subject's owners | curated knowledge (section 2.1); where it is kept is decided with the knowledge design, not here | "revenue in the Sales model means net revenue"; "every dimension in this project has an unknown member"; naming conventions for a schema |

**Binding by rule, not by copying.** Every knowledge item, wherever it is kept, has:

1. **a role:** *reference* (found by search when relevant), *instruction* (always given when its scope applies) or *guardrail* (an
   instruction marked as policy, shown as such; when a check exists for it, the check is what enforces it);
2. **a scope:** the subjects it is about, and who it is for: a semantic model, an entity, a Build project or a folder of it, a connection,
   an app, a tag, an audience (front or back end), an agent, a channel.

**Assembly, the same for every turn, in a fixed order:** the platform's rules, the install policy, the agent's instructions, then the
instructions and guardrails whose scope matches what is in play (the models in the agent's scopes, the app it is in, the project its
workspace is on, the objects the conversation has touched), then references found by search within the remaining budget, then the
conversation. A higher layer wins over a lower one where they conflict.

- **Instructions are never cut silently.** If the bound instructions do not fit the model's budget, the agent's publish or promotion is
  refused naming them, rather than dropping some at run time.
- **Every turn records what it was given:** which items, at which versions (section 3.8), so "why did the agent say that" can be answered
  and a conversation replayed.
- **Instructions and guardrails need approval** by the subject's owner, since they change what agents do; references follow the
  knowledge's own rules.
- **A change to a bound instruction changes agents without republishing them.** That is the point for business owners, but it bypasses
  the agent's evaluation. What to do about that **needs further discussion** (owner, 2026-11-05); running the evaluation sets of the
  agents it reaches automatically was proposed and is not agreed (owner question 27-19).

### 3.5a A worked example

The back-end agent, in a workspace on the Build project `sales-warehouse`. The person asks: "add a monthly revenue fact by region".

**The knowledge that exists** (wherever it is kept):

| Item | Role | Scope |
| --- | --- | --- |
| K1 "Facts are named `fct_`, dimensions `dim_`; one model per file" | instruction | the project `sales-warehouse`, back-end agents |
| K2 "Every dimension has an unknown member (key 0)" | guardrail | the folder `models/marts` of that project; **also enforced** by a metadata rule in the project's `tests/metadata`, which `project tests run` fails on |
| K3 "Revenue means net revenue: gross minus returns minus discounts" | instruction | the semantic model `Sales`, its measure `revenue` |
| K4 "In 2025 the EMEA region was split into EU and MEA; rows before 2025 carry the old code" | reference | the model `dim_region` |
| K5 "How we backfill an incremental model" (a long how-to) | reference | the project |
| K6 "Say that an answer is generated by an agent" | instruction | front-end agents |

**What the agent gets, and why:**

1. **When the conversation starts:** the platform's rules, the install policy, the agent's own instructions (Build's agent kit), and **K1
   and K2**, because the workspace is on `sales-warehouse` and these are scoped to it. K2 is shown as a guardrail, and the project test is
   what actually stops a dimension without an unknown member from passing.
2. **K6 is never loaded:** it is for front-end agents.
3. **When "revenue" comes into play:** the agent runs `project show semantic` or reads the `Sales` model to find the measure. That tool call
   names the subject `Sales` and its `revenue` measure, so **K3** is loaded from that turn on. Which subjects are "in play" is worked out
   from the tool calls and their results, never from a guess about what the person meant.
4. **K4 and K5 are not loaded up front.** They are references. When the agent works on `dim_region`, a search over references within the
   agent's scopes finds K4 and gives it a short excerpt with its source (this is the "RAG lite" part: retrieval, not loading everything).
   K5 stays out unless the work becomes a backfill.

**Loaded directly versus retrieved:**

| Loaded directly (instructions, guardrails) | Retrieved (references) |
| --- | --- |
| short rules that must always apply where their scope applies: conventions, definitions of terms, policy | long or occasional material: how-tos, history of a dimension, known data issues, past decisions, documents |
| chosen by **scope**: exact, the same every time | chosen by **search**: full text (and embeddings if the install has them, question 4), ranked, cut to the room left |
| counted against the budget when the agent is published (below) | fills whatever room is left on each turn |

The agent can also search itself, with `knowledge search` and `knowledge show` as tools, when it needs more than the excerpts it was given.

### 3.5b The model's space

"Space" is the model's **context window**: how much text it can take in one request, counted in **tokens** (pieces of words; roughly
four characters of English per token). It depends on the model: a few thousand tokens for small local models, around 128,000 to 200,000
for common hosted ones, more for some. Everything shares it: the platform's rules, the policy, the agent's instructions, the **tool
definitions** (each tool's name, description and argument schema), the instructions bound by scope, retrieved references, the conversation
so far, the tool results, and room left for the answer.

For scale: Build's agent kit (`SKILL.md`) is about 6,200 words, 40 KB, roughly 10,000 tokens. Build's tool definitions add more. A small
local model could not take that at all; a large hosted one takes it easily but leaves less room for the conversation, and long contexts
cost more and can make the model less attentive to what is in the middle.

So each agent has a **budget for what is always loaded**: a share of its bound model's window (for example 20%). "Never cut silently"
means:

- **at publish or promotion**, the agent's own instructions, its tool definitions, and every instruction and guardrail bound to anything
  in its scopes are counted with the bound model's tokenizer; over the budget, the publish is refused, naming what is largest;
- **when an owner approves a new instruction or guardrail**, the agents it reaches are checked the same way, and the approval says which
  agent it would push over its budget;
- **at run time**, references and old conversation are what give way, never instructions.

### 3.6 What an agent may touch

Permissions are never instructions. An agent's reach is the **intersection** of:

1. **the person's own access**: roles, and the row and column rules of plan 22 section 2 (an unattended agent: its service account's);
2. **the agent's scopes**: the semantic models, DbDataStore entities and operations (read, write, change request) its definition names,
   as an app's manifest does (plan 16, "Sign-in decisions" item 1: the token's scopes come from the definition);
3. **the install policy**, which can narrow further (no data reads for agents on a cloud provider, for example).

The serving layer enforces this on every call, through the token's audience and scopes, as it does for apps. The only way past the
person's own access is an **elevated action** (section 4.5), which holds its own grants and checks each request itself.

### 3.7 Model providers

**Proposed (2026-11-05):** a model provider is a kind of connection in `connection/1`, like an outside system (question 10): its endpoint,
its key as a secret reference, the models it allows and their prices, bound per channel. An agent names a model slot; each channel binds
it, so a test channel can use a local or cheaper model. An install that must keep data in its tenant binds only Microsoft Foundry
connections. Whether a provider is cloud or local is part of the connection, so the install policy can tell them apart (3.6, 3.3).

### 3.8 Transcripts and the audit

Kept in the `agents` module of the unified state (plan 29), large tool results as payloads.

- **The audit, always kept:** every tool call, its arguments and outcome, every approval and refusal, and the context items given (3.5),
  each naming the person and the agent and its version (owner, 2026-11-05: these are audit records).
- **The conversation text** is history, with the default retention of 30 days (plan 29 section 3.3), adjustable.

### 3.9 Limits and cost

Proposed (2026-11-05): per conversation, per person and per install, on turns, tool calls, tokens and cost. Cost is the provider's reported
usage times the price on the connection (3.7). A limit reached stops the conversation with a message; administrators are told at a
threshold.

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
- **Life cycle.** Actions for apps and agents are authored in workspaces, versioned, approved and published (plans 21, 22, 26). Sync's
  scripts **stay in Sync's configuration repository for now** (owner, 2026-11-04), on the shared script format, and move to the publish
  life cycle when Sync moves to the runner (plan 28 section 8).

### 4.5 Elevated actions: permissions held by the action, not the user

**Confirmed by the owner (2026-11-03)** as written here; who approves the grants was decided on 2026-11-04 (question 11).

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
   model's owner) approve these grants, with an audit (decided, owner, 2026-11-04).
2. **To people: who may run the action.** People and groups are given *run* on the action, in the identity home like any role
   assignment, so the same directory groups and DbDataStore grants (plan 22 section 2) can be used. The action's owner or an administrator
   assigns it (decided, owner, 2026-11-04).

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
- **No escalation, by default:** an elevated action cannot change grants, roles, definitions or model structure, and cannot call another
  elevated action unless its definition names it. An administrator can lift this for one action, as a recorded choice (owner,
  2026-11-04, following the security posture).
- **Audit names both, always** (owner, 2026-11-04: fixed, not a default an administrator can change): every write records the action and its version, its principal, and the person who asked (and the app or agent it
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

**Decided (owner, 2026-11-04):** an outside system is a **connection** like a database, a new kind in the existing `connection/1` format rather than a
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

- **An integration task can ask an agent** (triage a failed load and suggest a fix, summarize a change, match supplier names to
  customer records). The task runs on a schedule with nobody watching, so nobody can answer a prompt. **Decided (owner, 2026-11-04):** the
  per-action prompting setting decides (section 3.3). An action set to *do not ask* runs unattended; an action that would ask a person
  becomes a **proposal** instead: a draft change, a work item (section 5) or a notification for someone to approve later. An administrator
  decides what is safe unattended by setting actions to *do not ask*.
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
| 1 | `agent/1` and the **back-end agent** (owner, 2026-11-05): in the console, working in an authoring workspace on a Build project, with Build's agent kit as its instructions, prompting per action and the traits, context assembly from git files | wave 0; shared authoring (plan 22 section 1) |
| 2 | Actions: the shared script format; Sync's scripts move onto it; a `.csx` or SQL action as a tool of an agent and an endpoint for an app, in warm worker processes in the serving layer | wave 1; the serving contract (plan 20 section 10) |
| 3 | Front-end agents in the serving layer; curated knowledge in DbDataStore (glossary, metrics, verified queries), drafts from agents, evaluation sets; embedding search if chosen | DbDataStore drafts (plan 21); access rules (plan 22 section 2) |
| 3b | Elevated actions: action principals, grants to actions with field and row conditions, *run* grants to people, validate then apply or reject, the declarative form, audit of both identities | wave 2; DbDataStore permissions (plan 15); the identity home's service principals (13a U20) |
| 4 | The runner (plan 28) with its progress store on the unified state structure (plan 29), then DbDataIntegration: outside connections, the integration kit, templates, inbound and outbound patterns, webhooks; actions that enqueue integration tasks; back-end agents in workspaces | shared authoring (plan 22 section 1) |
| 5 | The work tool: models, app, integration tasks, assistant | DbDataStore relationships and documents; the app framework's declarative views |

Waves 1 and 2 can run side by side once wave 0 is done; wave 5 can start its DbDataStore models as soon as DbDataStore can hold them.

## 8. Questions for the owner

| # | Question | Proposed |
| --- | --- | --- |
| 1 | Which of these are servers? | **Partly decided** (owner, 2026-11-04): integrations are a product of their own, publishing to the runner, a service of its own (plan 28). Open until the detailed plans show what each back end needs (owner, 2026-11-04): whether agents and DbDataIntegration share a back end with apps; proposed earlier: knowledge and the work tool are not servers (section 6) |
| 2 | The work tool's name | **Working name DbDataBacklog** (owner, 2026-11-03); final name undecided |
| 3 | Languages and packaging | **Decided for integrations** (owner, 2026-11-04): `.csx` scripts, compiled .NET projects, any executable through the runner protocol, and hybrids such as a .NET notebook over a project (plan 28 section 4). Actions: `.csx`, SQL, command calls and declarative elevated forms |
| 4 | Embedding search | optional per install; full-text always |
| 5 | Agent approvals | **Decided** (owner, 2026-11-03): prompting is configured per action. Proposed 2026-11-05 (section 3.3): 5a the three settings, 5b layers (action default, install policy with a minimum, agent definition; the strictest applies), 5c defaults from traits. **Decided** (owner, 2026-11-05): reading definitions and reading data are separate traits |
| 6 | Unattended agents in integration tasks | **Decided** (owner, 2026-11-04): actions set to *do not ask* run; actions that would ask become proposals (drafts, work items, notifications) (section 4.9) |
| 7 | The work tool's scope | its own items first; links to GitHub pull requests and workspaces; import from Jira or Azure Boards later; no two-way sync |
| 8 | First audience | **Decided** (owner, 2026-11-05): the **back-end** agent first, because it unlocks building apps (section 3.2) |
| 9 | The integrations product's name | **Decided** (owner, 2026-11-04): DbDataIntegration (section 4.11) |
| 10 | Outside systems as connections | **Decided** (owner, 2026-11-04): a new kind in `connection/1`, sharing secrets, channel bindings and the console's view (section 4.6) |
| 11 | Elevated actions: who approves an action's grants on data, and who may give people *run* on it | **Decided** (owner, 2026-11-04): grants on data: an administrator and the DbDataStore model's owner, both, with an audit; *run*: the action's owner or an administrator (section 4.5) |
| 12 | Integrations with or apart from actions and Sync's scripts | **Decided** (owner, 2026-11-04): apart. Integrations are a product publishing to the runner; actions share one script format with Sync (section 4) |
| 13 | `agent` as a published kind (plan 30), with its model as a slot bound per channel | proposed (sections 3.1, 3.7) |
| 14 | Where agents run: back end in the console, front end in the serving layer, unattended in the runner | proposed (section 3.2) |
| 15 | Model providers as a kind of connection | proposed (section 3.7) |
| 16 | Transcripts | **Decided** (owner, 2026-11-05): tool calls, approvals and context given are audit, always kept; conversation text is history, 30 days by default (section 3.8) |
| 17 | Limits and cost | proposed (section 3.9) |
| 18 | Context: four homes, roles (reference, instruction, guardrail) and scopes on knowledge, assembly in a fixed order, hard guardrails enforced by the runtime, a budget for what is always loaded | proposed (sections 3.5 to 3.5b); where curated knowledge is kept is not decided (section 2.1) |
| 19 | What to do when a change to a bound instruction changes agents without their evaluation | **Open, needs discussion** (owner, 2026-11-05); automatic evaluation runs were proposed and are not agreed (section 3.5) |
| 20 | An agent's reach: the person's access, narrowed by the agent's scopes and the install policy, enforced by the serving layer | proposed (section 3.6) |

## 9. Detailed plans to follow

Once section 8 is answered, each area gets a detailed plan with its definitions' formats, commands, storage, tests and slices (one slice,
one branch `suite/<id>-<short-name>`, a "Done when" test, as in 13a):

| Plan | Area | New slice letter (not yet used) |
| --- | --- | --- |
| [28](28-runner.md) | The runner (written at high level, 2026-11-04) | to choose when sliced (13a and plans 19 to 26 already use A B C D E F G H J K L M N O P Q R S T U V W X Y Z and AP; D and Q are decision and step numbers, the rest slices) |
| [29](29-unified-state.md) | One structure for the suite's state databases (written at high level, 2026-11-04) | |
| [30](30-publishing.md) | One way to publish, to the serving layer and the runner (direction decided, 2026-11-04) | |
| 31 | DbDataKnowledge | |
| 32 | DbDataAgents | |
| 33 | DbDataIntegration | |
| 34 | Actions: the shared script format, callable and elevated actions | |
| 35 | The work tool | |
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
