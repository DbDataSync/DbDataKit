# Plan 27: knowledge, agents, functions, automations and work management

Status: **proposed, high level** (2026-11-03; DbDataFunctions added the same day at the owner's suggestion). Four new areas the owner
asked for, plus a name that unifies the suite's custom code. This page decides their shape and how they fit together
and with plans 16, 20, 22, 25 and 26. It lists the questions only the owner can answer (section 8). Not sliced yet: each area gets a
detailed plan of its own (section 9) once the questions are answered.

| Area | What it is for |
| --- | --- |
| **DbDataKnowledge** | what agents know: on the back end, to build new objects that fit what exists; on the front end, to answer questions and write queries |
| **DbDataAgents** | an agent definition: its purpose, the context and knowledge it reads, the tools it may call, the model it uses and how it is tested, for business users on the front end or developers on the back end |
| **DbDataFunctions** | the suite's custom code under one name: Sync's scripts today, and functions that are agents' tools and apps' custom logic (section 4.1) |
| **DbDataAutomations** | scheduled and triggered ETL workflows, whose custom steps are functions (section 4.2) |
| **DbDataBacklog** (working name; section 5) | backlog, sprint and work management, tied to the suite's own objects |

## 1. What already exists, and what this plan adds

The four areas are mostly new *definitions* on machinery the suite has or has planned. This plan adds as little new machinery as it can.

| Needed | Exists or planned | Gap this plan fills |
| --- | --- | --- |
| A tool for every feature | the command surface (`DbDataKit.Cli`: command specs, effect classes, JSON schemas, generated MCP tools; plan 13 W4a); "no feature without a command spec" (plan 16 section 1) | tools that are not commands: callable functions (section 4.1) |
| An agent runtime | plan 16 section 1: `DbDataKit.Agents` on `Microsoft.Extensions.AI`, approvals, acting as the user, transcripts (**not built**); testing with a scripted fake and recordings (plan 25 section 2) | the **agent definition** as a versioned, published object (section 3) |
| What agents know | catalog and glossary on the lineage graph (plan 22 section 3); semantic models (plan 14); Build metadata and `project agent-kit`; DbDataStore models (plan 15) | curated knowledge, verified queries, retrieval, and access rules on what an agent may read (section 2) |
| Custom code | `DbDataKit.Scripting` (Roslyn `.csx` with `#r "nuget:"`, built, used by Sync's scripts, libraries and drivers) | one definition of custom code for every product (DbDataFunctions), and workflows (section 4) |
| Schedules, runs, alerts | `DbDataKit.Scheduling`, `DbDataKit.Monitoring` (plan 13 W10), the delivery service (plan 22 section 5) | none: automations use them as Build and Sync do |
| Authoring and publishing | shared authoring (plan 22 section 1: workspaces, editor, commit, merge, publish); the serving layer runs from published definitions (plan 20) | agents and automations as new kinds of authored project (section 6) |
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
| Audience | **front end** (business users: answer questions, write and run queries, fill in forms) or **back end** (developers: build models, replications, Store models, apps, automations) |
| Instructions | the agent's own instructions, in files in its project |
| Knowledge | which knowledge collections and kinds it searches (section 2), and context it always gets (for example "the Sales semantic model") |
| Tools | an allow-list of commands and automation functions (section 4), named by spec; never wider than its user's roles |
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

Plan 16 decided "every action approved as it happens, to be refined later". Agent definitions are where that refinement is written down:

- **Proposed default:** steps whose effect class is *reads* run without a prompt. Every write, every change to a definition and every
  outward-facing step (send, publish, call an outside system) waits for the user. Destructive and outward steps always ask, whatever the
  definition says.
- A definition can only make approvals **stricter** than the install's policy, never looser.
- **Owner question 5.**

### 3.4 Testing and evaluation

1. **Behaviour** is tested as in plan 25 section 2: a scripted fake model, recorded provider responses, golden transcripts, approval
   gates.
2. **Quality** is measured with an evaluation set: questions with known good answers, drawn from verified queries (section 2.1). It is
   run against a real model by hand or on a schedule, never as a pull request gate. A published version records its last score.

## 4. DbDataFunctions and DbDataAutomations

**Goal.** One way to write custom logic, used everywhere the suite runs custom code: Sync's scripts, scheduled ETL processes, tools an
agent can call, and functions an app can call.

**Two names, two jobs** (owner's suggestion, 2026-11-03: a name that unifies the scripting the suite already has):

- **DbDataFunctions** is the **code**: every piece of custom logic, in whichever product it runs. The engine underneath is the built
  `DbDataKit.Scripting` (resolver, lock files, loader, compiler).
- **DbDataAutomations** is the **orchestration**: workflows with triggers, steps, retries and monitoring. A workflow's custom steps are
  functions.

### 4.1 What a function is

Sync already has the shape. A Sync script is a manifest (`scripts/<name>.yaml`: name, a `kind` naming the extension point it implements,
a language of C# or SQL, declared parameters) beside its code (`<name>.csx` or `<name>.sql`) and its lock file. A function generalizes
that manifest:

| Part | Sync script today | A function |
| --- | --- | --- |
| Code | `.csx` or `.sql` | the same, plus a call to commands; Python is owner question 3 |
| Packages | `#r "nuget:"` with a lock file (13a S12) | the same, through the same allow-list |
| Parameters | declared, supplied by a binding | an input schema, as a command spec has |
| Result | whatever the slot's interface returns | an output schema |
| Where it plugs in | `kind`: one of Sync's slots | `kind`: an **extension point** (a Sync slot; later Build hooks and others) or a **callable** function (a tool and an endpoint) |
| Effect | implied by the slot | an effect class (reads, writes), as commands have |

- **A callable function is a tool by construction.** It has a spec like a command, so the command surface turns it into an MCP tool, a
  tool an agent definition can allow, and an endpoint apps call through the serving layer, under the same access rules. One definition,
  several callers.
- **An extension-point function** is called by the product that owns the point, with that point's interface, as Sync's scripts are now.
- **Shared libraries** of helper code (`#load`) and package sets belong to DbDataFunctions too, so Sync, automations and callable
  functions resolve and pin packages one way.
- **Callable functions run in the serving layer**, in a child process (plan 20 principle 5), as the caller. Extension-point functions run
  in the product that calls them, as today.

**What it changes for Sync** (a clean break is allowed, never a silent one): Sync's scripts become functions of an extension-point kind.
The manifest gains the input and output schemas, and an old manifest is refused with a message naming the file. Sync's `script` noun
becomes the shared `function` noun. Drivers stay Sync's, compiled by the same engine. Whether Build's SQL hooks join is for the detailed
plan.

**Name: owner question 9.** Candidates: **DbDataFunctions** (proposed), DbDataScripts, DbDataCode, DbDataExtensions.

- *Functions* says "typed inputs and outputs", which is what makes one a tool or an endpoint, and it reads well for SQL bodies too.
- *Scripts* undersells the typed, callable side, and suggests C# only.
- The clash to manage: "function" also means a SQL function or a DuckDB macro (Build's key functions, plan 25). The docs always say
  "a DbDataFunctions function" or "a SQL function", and the command noun is `function`.

### 4.2 Workflows (DbDataAutomations)

A **workflow** is steps in order or in parallel: run a command (a Sync replication, a Build refresh plan), a function, SQL, a transfer
(`DbDataKit.Transfer`), a check (plan 23), an agent step (section 4.5). It has triggers, retries and timeouts. It runs on the back end,
each run in a child process.

- **Triggers:** a schedule (`DbDataKit.Scheduling`), the end of another run (a Sync replication, a Build plan), a DbDataStore change, a
  signed webhook, or by hand.
- **Runs are monitored like everything else** (`DbDataKit.Monitoring`): history, outcomes, overdue and failed runs, alerts through the
  delivery service, and run lineage (plan 18) from each step's declared inputs and outputs.

### 4.3 Not a third ETL engine

Sync replicates and Build models. A workflow **orchestrates** them and covers what neither does (call an API, move a file, a custom
transform, a step that needs a person). A step that Sync or Build can do is a command step that calls them, not a function. The detailed
plan names what Sync's extension points and Build's hooks keep, so there is one obvious place for each job.

### 4.4 Trust and isolation

`DbDataKit.Scripting`'s syntax guard is not a security boundary, so every function is **trusted code**:

- written by people with the Author role, in a workspace, reviewed and merged;
- a published version is approved by an administrator, recorded with an audit, as plan 26 does for trusted apps;
- run in a child process with limits (time, memory), with outside network access refused unless the definition's allow-list names the
  host (the same allow-list idea as apps' egress, plan 26 section 2b);
- packages only through the resolver's allow-list and lock file (13a S12).

The connections a function or workflow uses are named in its definition and bound by an administrator, per channel (plan 21). A function
called by a person runs **as that person**, so access rules apply to its reads. A scheduled workflow runs as a **service account** of the
identity home (plan 16, "Sign-in decisions" item 2).

### 4.5 Agents inside workflows

A workflow step can ask an agent to do something (triage a failed load, summarize a change, draft a mapping). No user is there to approve
each action, so an unattended agent step **may only read and propose**: what it would change becomes a draft or a work item (section 5)
for a person to approve. **Owner question 6.**

### 4.6 Language

C# `.csx` on `DbDataKit.Scripting` first (built, runtime only, no SDK), plus SQL and command bodies. Python is the obvious second
language for data work. **Owner question 3.**

## 5. DbDataBacklog: work management

**Name.** "Backlog" names one view of the tool. Candidates: **DbDataWork** (proposed: plain, and it covers backlog, sprints and boards),
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
| Automatic items: a failed run, a check that blocks, an approval waiting | workflows (section 4) |
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
| Functions | functions in workspaces | **calls** from apps and agents, in child processes; extension-point functions stay in the product that calls them | bindings, approvals of trusted versions |
| Automations | workflows in workspaces; **workflow runs** on an automations server | (nothing of its own) | runs, schedules |
| Work | (nothing of its own) | the work app on the `store` and `apps` modules | the app's administration like any app |

**Back-end servers.** Agents, functions and automations each need somewhere to author, build, test and publish, and workflows need a scheduler that
is always running. Two ways:

- **A.** One back-end server per product (agents, functions, automations), the shape of every other product.
- **B.** One back-end server for authored code: apps (plan 22's DbDataApps back end), agents, functions and automations are all projects authored in
  workspaces, built, tested and published to the serving layer, and the scheduler runs workflows. Each keeps its own name, nouns and
  screens.

**Proposed: B**, because the three share almost all of their back-end work and an install with fewer services is easier to run. **Owner
question 1.**

## 7. Order

| Wave | What | Needs |
| --- | --- | --- |
| 0 | What plan 16's agents need: the suite-wide command spec test, `DbDataKit.Agents` with the scripted fake, the provider proof against the four providers | 13a W4a work; plan 25 section 2 |
| 1 | `agent/1` and one front-end agent: questions over a semantic model in the console's chat, with knowledge generated from the catalog and semantic descriptions, and full-text search | wave 0; plan 22 section 3's catalog search |
| 2 | DbDataFunctions: the function manifest; Sync's scripts move onto it; a `.csx` or SQL function as a tool of an agent and an endpoint for an app, in a child process in the serving layer | wave 1; the serving contract (plan 20 section 10) |
| 3 | Curated knowledge in DbDataStore (glossary, metrics, verified queries), drafts from agents, evaluation sets; embedding search if chosen | DbDataStore drafts (plan 21); access rules (plan 22 section 2) |
| 4 | Workflows: triggers, steps, retries, monitoring, lineage; back-end agents in workspaces | shared authoring (plan 22 section 1) |
| 5 | The work tool: models, app, workflows, assistant | DbDataStore relationships and documents; the app framework's declarative views |

Waves 1 and 2 can run side by side once wave 0 is done; wave 5 can start its DbDataStore models as soon as DbDataStore can hold them.

## 8. Questions for the owner

| # | Question | Proposed |
| --- | --- | --- |
| 1 | Which of these are servers? | Knowledge and the work tool are not (a library, Store models, a module, an app). Agents, functions and automations share one back-end server with apps (section 6, B) |
| 2 | The work tool's name | DbDataWork |
| 3 | Function languages | C# `.csx`, SQL and command steps first; Python later, as a second script host |
| 4 | Embedding search | optional per install; full-text always |
| 5 | Agent approvals | reads run without asking; writes, definition changes and outward steps ask; a definition may only tighten |
| 6 | Unattended agents in workflows | read and propose only; changes become drafts or work items |
| 7 | The work tool's scope | its own items first; links to GitHub pull requests and workspaces; import from Jira or Azure Boards later; no two-way sync |
| 8 | First audience | front-end agents first (wave 1), since the semantic layer and catalog are closest to ready |
| 9 | The name for the suite's custom code | DbDataFunctions, with DbDataAutomations for workflows; the command noun `function` replaces Sync's `script` (section 4.1) |

## 9. Detailed plans to follow

Once section 8 is answered, each area gets a detailed plan with its definitions' formats, commands, storage, tests and slices (one slice,
one branch `suite/<id>-<short-name>`, a "Done when" test, as in 13a):

| Plan | Area | New slice letter (not yet used) |
| --- | --- | --- |
| 28 | DbDataKnowledge | to choose when sliced (13a and plans 19 to 26 already use A B C D E F G H J K L M N O P Q R S T U V W X Y Z and AP; D and Q are decision and step numbers, the rest slices) |
| 29 | DbDataAgents | |
| 30 | DbDataFunctions and DbDataAutomations | |
| 31 | The work tool | |

## 10. What this changes in other plans (once approved)

| Plan | Change |
| --- | --- |
| 16 section 1 | the agent runtime loads published `agent/1` definitions; approvals are refined per section 3.3 |
| 20 section 4 | new modules: `knowledge` (reads), and function calls under `mcp` and `apps` (or a `functions` module) |
| 13 W2, 13a S-series | Sync's scripts become DbDataFunctions functions of an extension-point kind; `script` becomes `function` (a clean break, refused with a message) |
| 22 section 1 | shared authoring gains three project kinds, agents, functions and automations, and the apps back end hosts them (if question 1 is B) |
| 22 section 3 | catalog search becomes part of knowledge search |
| 25 section 2 | adds evaluation sets beside the behaviour tests |
| 26 | the work tool is the first app the suite ships |
