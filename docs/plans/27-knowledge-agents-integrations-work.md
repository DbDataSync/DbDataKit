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
| **DbDataBacklog** (section 5) | backlog, sprint and work management, tied to the suite's own objects |

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
| **Curated, business** | glossary terms, metric definitions, business rules ("an active customer is..."), known data issues | business owners | **not DbDataStore** (owner, 2026-11-05: DbDataStore holds data structures people author inside the system, not the system's own structures); proposed: the Workspace server's knowledge editor, kept in git (section 2.5) |
| **Curated, technical** | naming conventions, modelling patterns, "how we build a slowly changing dimension here", review checklists | developers and administrators | files in a git project, reviewed like code (**decided**, owner, 2026-11-05) |
| **Verified queries** | a question, the semantic query or SQL that answers it, who verified it and when | people, or an agent's answer a person approved | as curated business knowledge (section 2.5); also the test set for front-end agents (section 3.4) |
| **Documents** | specifications, policies, meeting notes | DbDataStore documents (plan 15 section 6), with extraction | the document plus its extracted text |
| **Learned** | an agent proposes a new term, a correction or a verified query while working | agents | a DbDataStore **draft** (plan 21), kept only once a person approves it, as with agent-built mappings (plan 25 section 3) |

### 2.2 How it is read

1. **Search and show, as commands.** `knowledge search`, `knowledge show`, `knowledge related` on the command surface, so the console, MCP
   clients and embedded agents use the same tools. They extend plan 22's `catalog search` and `catalog show` rather than sitting beside them.
2. **Retrieval by vector search** (decided, owner, 2026-11-05; section 2.4): through `Microsoft.Extensions.AI`'s embedding abstraction
   and the engine's vector support. Without vector search there is no knowledge search; full-text search is not built at first.
3. **Access rules apply to knowledge.** An item about a table, a model or an entity is visible only to someone who may read that object.
   This uses the policy engine of plan 22 section 2, so an agent cannot learn through knowledge what its user could not query.
4. **Served by the serving layer** as a `knowledge` module (reads only), published from the back end like every other definition (plan 20
   principle 1). Curation (writing terms, approving drafts) happens where DbDataStore's writes already happen.

### 2.3 Product or library

Knowledge has no runtime of its own beyond an index and a module. **Proposed:** DbDataKnowledge is a set of DbDataStore models shipped
with the suite, a shared library (`DbDataKit.Knowledge`: the index, retrieval, the access filter), a serving module, and console screens.
It is **not** a new server. **Owner question 1.**

### 2.4 Knowledge stores

**Decided (owner, 2026-11-05).** Where knowledge is kept (section 2.1) is open; this separates the question in two: where items are **kept and
edited** (sources), and where they are **searched** (the index). Every source gives items in one shape (the `knowledge: 1` fields of
section 3.5c), so agents, scopes, budgets and the record of what a turn was given do not care where an item came from.

**Sources:**

| Source | Suits | Edited by | Changes reviewed by | First? |
| --- | --- | --- | --- | --- |
| **Files in git**: a project's `knowledge/` folder, the install knowledge project, an agent's project | technical knowledge, conventions, policy text, anything developers own | the console's editor in a workspace, or any editor | a merge, like code | **yes**: the back-end agent comes first, and it needs nothing else |
| **Generated** from definitions and the catalog | tables, columns, models, members, lineage, descriptions | nobody: derived when a definition is published | the definition's own review | **yes**, with the catalog (plan 22 section 3) |
| **The Workspace server's knowledge editor** (section 2.5, proposed) | business knowledge owned by people who do not use git: glossary, metric definitions, business rules, verified queries | forms in the console; git underneath, never shown | the item's owner, through a change request | after the git files, with the front-end agents |
| **Documents**: files in DbDataStore's documents (plan 15 section 6), with text extracted | specifications, policies, manuals | Office, over WebDAV | the document's own owner | later; references only |
| **Outside systems** (a wiki, SharePoint, Confluence) through an integration (section 4) | knowledge that already lives elsewhere | in that system | that system | later; copied in by an integration task on a schedule, read-only here, references only unless an owner promotes an item |

Rules for every source: each item keeps its **provenance** (source, path or id, version), the record of a turn names it, and items from a
source no one reviews (documents, outside systems) can be **references only**, never instructions or guardrails, until an owner approves
one as such.

**The index:** the `knowledge` module of the unified state (plan 29), rebuilt from the sources:

**Decided (owner, 2026-11-05): knowledge search uses vector search on SQL Server and PostgreSQL**, and the suite's SQL Server test engine
moves to SQL Server 2025.

| Engine | Vector search | Full-text search (not used at first) |
| --- | --- | --- |
| PostgreSQL | the `pgvector` extension | built in (`tsvector`) |
| SQL Server | the vector type of SQL Server 2025 (so knowledge search needs 2025 or later) | full-text indexes, an optional feature (on Linux a separate package, `mssql-server-fts`) |
| SQLite (single instance) | the `sqlite-vec` extension (owner, 2026-11-05) | built in (FTS5) |

**`sqlite-vec`** (its native library per platform, loaded by `Microsoft.Data.Sqlite`) is added during the knowledge-index work, not in the
test-environment slice (owner, 2026-11-06).

**Vector search only, to start (owner, 2026-11-05).** Full-text search is not built at first. An install, channel or engine without
vector search (no embedding model bound, an engine without the extension) has **no knowledge search**: references are not found, and the
console says why. Instructions and guardrails bound by scope are not affected, since they are given by scope, not found by search.

Vectors come from an **embedding model**, a model provider connection (section 3.7) bound per channel, so it can be a local model where
data must not leave the install. Every item is embedded when it is indexed, and every query when it is asked.

**For the test environment** (planned, owner, 2026-11-05; **changed in `testenv/` on 2026-11-06 but not yet run**: the sandbox that made
the change had no Docker daemon, so the engines still have to be started and every suite run against them by the owner or in CI; most
products have already been tested on SQL Server 2025): the `mssql` and `mssql-target` profiles move to SQL Server 2025, with
SQL Server Agent kept on for Sync's CDC; the `postgres` profile gains `pgvector`; the suites that pin 2022 in their text (Build's CI
comments and `CLAUDE.md`) say 2025. Access rules filter results (section 2.2), and a reference found for an agent is only one its
person may see.

### 2.5 Business knowledge: edited in forms, kept in git

**Decided (owner, 2026-11-06)**; proposed 2026-11-05, revised 2026-11-06 after the owner's question: business users cannot reach the Workspace server or, in most
installs, the console). The owner's direction: git suits knowledge that administrators and developers publish; DbDataStore is not the
place for business users' knowledge, because DbDataStore holds the data structures people author inside the system, not the system's own
structures. Business knowledge describes and steers the system, so it belongs with the other definitions, in git.

**The problem.** End users reach only the serving layer (plan 20); the Workspace server and the console stay on the private side. So a
business user can neither open a workspace nor reach the Workspace server, yet their changes must end up as commits there.

**Proposed: the edge accepts and records suggestions; the Workspace server picks them up.** This is the pattern the suite already uses for
DbDataStore's change requests (approved in the serving layer, plan 20 decision 3) and for webhooks (received by the serving layer into the
runner's inbox, plan 28 section 7).

1. **Where business users see and edit knowledge: a knowledge app**, shipped with the suite and served by the serving layer like any app
   (plan 26), so it is reachable wherever the person's other apps are. It shows the items the person may see (access-filtered, section
   2.2), in production and in test, with their history; a form per item; **Suggest a change**; and the state of each suggestion. The same
   screens appear in the console for administrators and developers.
2. **A suggestion is a record at the edge**: the item, the proposed text and fields, the version it was based on, who suggested it, why.
   It is written to the serving group's shared state (a `knowledge_suggestions` table of the `knowledge` module, plan 29), so it is
   stored even when the Workspace server is down.
3. **The Workspace server collects suggestions.** It is paired with the serving group as a Publisher already (plan 30); it follows the
   suggestions the way instances follow definitions, by an event log it polls, and turns each into a branch and a commit in the knowledge
   project, with the suggester as the commit's author. The serving group never calls the Workspace server, so nothing at the edge waits on
   it.
4. **Owners approve where they work: in the knowledge app.** An approval is also a record at the edge, signed with the approver's key from
   the identity home (plan 30 section 6.2). The Workspace server sees it, merges the branch, and publishes the item to the test channel; a
   second approval (or an administrator's rule) promotes it to production. Each step's state shows in the app: waiting for approval,
   approved, waiting for the Workspace server, in test, in production.
5. **When the item changed meanwhile:** the suggestion carries the version it was based on. If the item has moved on, the Workspace server
   marks the suggestion **needs another look**, and the app shows the suggestion beside the current text, instead of a merge conflict.
6. **Agents suggest the same way.** An agent's proposed knowledge (the "learned" kind, section 2.1) is a suggestion through a tool
   (`knowledge suggest`), with its prompting setting, so people and agents have one path and one review.
7. **Ownership per item** comes from its `owner:` (a group of the identity home); who may suggest changes to an item is set per project
   (anyone who can see it, by default), so a person may own a few items without rights on the rest.

So knowledge has **one storage model** (files in git, in projects on the Workspace server) with two ways in: workspaces for developers
and administrators, and the knowledge app for business users, whose suggestions reach git through the edge.

### 2.6 How knowledge is published and read

**Decided (owner, 2026-11-06)**, from the owner's question.

1. **A knowledge project is published like any other definition** (plan 30), as the kind `knowledge-project`, to the serving group: one
   version per merged commit, each file an artifact by its hash, signed by the Workspace server, per channel.
2. **The serving layer's `knowledge` module serves it.** When a version is **staged**, the module reads its files, checks every item
   (format, scopes, owners) and embeds the items that changed (by hash; an unchanged file is not embedded again) into that channel's index
   in its state (plan 29). **Activation** switches the channel to the new items and index at once, so a search never sees half a version.
3. **Everything reads through the module, never the state database directly:** the knowledge app (section 2.5), front-end agents in the
   serving layer, and the `knowledge search` and `knowledge show` commands, each under its caller's access rules.
4. **Agents elsewhere call it too:** back-end agents in the console and unattended agents in runner tasks call the serving layer's
   knowledge API, as their person or service account. Knowledge is published in one place and indexed once.
5. **Two things that are not the published copy:**
   - **A workspace's own changes.** A back-end agent working on a branch also reads the knowledge files of that branch from the Workspace
     server, laid over the published ones, so an instruction it is editing applies to its own work before it is merged.
   - **Generated knowledge** is not published by a knowledge project. The module derives it from the other definitions published to the
     same serving group (semantic models, DbDataStore models, app manifests, data sources), as their subscriber, and indexes it with the
     same rules.

### 2.7 Generated knowledge

**Decided (owner, 2026-11-06).** Knowledge the suite derives from its own definitions, so nothing already described in a definition is typed again.

**Principles:**

- **Derived, never edited.** A generated item is rendered from a definition by a fixed template; to change it, change the definition.
- **References by default.** Generated items are found by search; a curated instruction can bind to the same subject when something must
  always be given (section 3.5).
- **Exactly what is served.** The `knowledge` module subscribes to the definitions published to its serving group (section 2.6), per
  channel, so a channel's generated knowledge is that channel's definitions, no more and no less.
- **Small items, one per subject.** One item per model, column, measure, attribute or app, so no chunking is needed and a search finds the
  exact thing.

**What is derived, from what:**

| Published definition | Items | From its fields |
| --- | --- | --- |
| `semantic-model` | the model; each measure, attribute and hierarchy; each saved query | names, synonyms, formats, how a measure aggregates, the entities and relationships; a saved query's description and its semantic SQL (a reference, not a verified query, until someone verifies it) |
| `build-metadata` (new kind: Build publishes its metadata documents to the serving group, per project and connection, as it publishes them to its tracking store today) | each model and each of its columns | title, description, tags, properties, model kind, columns and types, keys, what it reads and what reads it |
| DbDataStore models | each model and field | names, types, validation rules, relationships |
| `data-source` | each data source | its engine and what it exposes |
| `app`, `agent`, `skill` | each one | title, description; for an app, the models it uses; for an agent or skill, what it is for (so a person or an agent can find the right one) |

**Lineage** is not indexed as a graph: each item names its direct upstream and downstream, and the full graph is a tool (`lineage show`).
**Volatile facts** (freshness, last run, row counts) are not indexed either; they change too often, and tools return them live.

**Values are not knowledge by default.** Distinct values of a column (region codes, product lines) help agents match "Northeast" to `NE`,
but they are data. A column can opt in (`knowledge: values` on a Build column or a semantic attribute, for small sets of values); its
values are then indexed with **the column's own access rules** (plan 22 section 2), so a person never finds a value through knowledge that
they could not query.

**Item shape and scope.** A generated item has the `knowledge: 1` shape: role *reference*, about its own subject, owned by the
definition's owner, with provenance naming the definition, its version and the channel. Its id is stable (`generated:semantic-model:Sales/measure:revenue`),
so a curated item can name it, and a search result can be followed to the definition.

**Access.** A generated item is visible to whoever may read its subject: a semantic model's items to those who may query the model, a
column hidden from a person by a column rule hidden from them as well.

**Refresh.** When a source definition's version is staged in a channel, its items are rendered again; only those whose text changed are
embedded again; activation switches them with the definition; a retired definition's items go with it.

**Filling the gaps.** Generated knowledge is only as good as the descriptions it is rendered from:

- semantic-model measures, attributes and hierarchies gain a `description` (they have synonyms and formats, but no description today);
  DbDataStore model fields gain one too;
- the console shows what is undescribed ("12 measures in Sales have no description");
- a business user can **suggest a description** from the knowledge app (section 2.5). When the definition's project is on the Workspace
  server, the suggestion becomes a change to that project (a commit to the model's file, approved by its owner); until then, it becomes a
  curated item about the same subject.

**The catalog** (plan 22 section 3) is this: search over generated knowledge, with the lineage tool beside it. `catalog search` and
`catalog show` become `knowledge search` and `knowledge show` filtered to generated items.

### 2.8 Chunking long items

**Decided (owner, 2026-11-06).** Vector search compares a question with pieces of text, so a long item is split into **chunks**, each embedded and
found on its own. What is and is not chunked:

| Item | Chunked? | Why |
| --- | --- | --- |
| generated items (section 2.7) | no | already one small item per subject |
| instructions and guardrails | **never** | always given whole; one that does not fit the budget is refused at publish (section 3.5b) |
| short references (one screen of text) | no | the item is the chunk |
| long references: how-tos, specifications, documents, pages from outside systems | **yes** | a question matches a part of them, not the whole |

**1. Split by structure first, then by size.** Markdown is split at its headings, so a chunk is a section; a section that is still too
long is split at paragraphs, then at sentences; a table or a code block is never cut in the middle. Extracted documents (plan 15 section
6) keep their structure the same way: headings, paragraphs, tables as Markdown tables, and page numbers for citing.

**2. The size comes from the embedding model.** Each embedding model has a largest input it reads; its connection says what it is
(section 3.7a). Chunks aim at a share of it (proposed: about half, and at most about 500 tokens, since smaller chunks match questions more
precisely), with a small overlap between neighbouring pieces of one section (proposed: one or two sentences) so a sentence cut at a boundary
is still found. Binding a different embedding model can change the sizes; the index is rebuilt for it anyway (one model per index).

**3. Each chunk carries where it sits.** Before it is embedded, a chunk is prefixed with its item's title and its heading path
("Backfilling an incremental model > Choosing the range"), so a chunk that says "set it to the first missing day" is still found by a
question about backfills. The prefix is part of what is embedded, not of what is stored as the chunk's text.

**4. What the agent is given.** A search returns chunks, then:

- chunks of one item are grouped, so one item does not fill the results (proposed: at most three chunks per item, best first);
- each result is the chunk's text with its item's title, heading path, source and version, so the agent can cite it;
- adjacent chunks of the same section are joined into one excerpt;
- the agent can ask for more with `knowledge show <item> [--section <heading>]`, which returns a whole section or the whole item.

**5. Refresh is per chunk.** A chunk's id comes from its item and its position in the headings, and it records the hash of its text. When
an item changes, it is split again and only the chunks whose text changed are embedded again, so editing one section of a long how-to
re-embeds that section.

**6. Access and scope come from the item.** A chunk is visible to whoever may see its item, and is in scope where its item is. Nothing is
decided per chunk.

**7. An item can say how it is split**, for the cases the defaults get wrong: `chunking: none` (keep it whole: a short glossary that must
stay together), `chunking: { split: heading-level-2 }` (split only at level-two headings). The install sets the defaults per source; an
item's own setting wins.

### 2.9 Verified queries

**Decided (owner, 2026-11-06).** A **verified query** is a question people ask, the query that answers it, and someone's word that the answer is
right. It builds on what exists: saved queries in a semantic model's catalog (plan 19 J26) and people's own saved queries kept by the
serving layer, with versions, shares and a promote-to-model action (J27).

**1. The item.** Business knowledge, so it is a `knowledge: 1` file in a knowledge project (section 2.5), of the kind `verified-query`:

```markdown
---
knowledge: 1
kind: verified-query
title: Net revenue by region, last quarter
about: [{ semantic-model: Sales }]
owner: finance-data
questions:                          # how people ask it; each is embedded and searched
  - What was net revenue by region last quarter?
  - Revenue per region for the previous quarter
query:                              # pinned: the semantic query request (JSON form) as it was verified
  request:
    measures: [orders.net_revenue]
    attributes: [customers.region]
    filters:
      - { attribute: date.fiscal_quarter, operator: last_complete, count: 1 }
    order: [[orders.net_revenue, desc]]
    limit: 100
verified:
  by: user:maria
  at: 2026-11-06
  against: semantic-model:Sales@14   # the model version it was checked against
---
Revenue here is net revenue (gross minus returns minus discounts). Regions follow the 2025 split (EU and MEA).
```

- **The JSON form of a semantic query is the standard form** (owner, 2026-11-06): the request of `docs/semantic/query.md` (measures,
  attributes, filters with relative periods such as `last_complete`, order, limit). It is what is stored, what runs and what is compared.
  A query written in semantic SQL is accepted as input, translated by the semantic SQL translator (`DbDataKit.Semantic/Sql`) into the
  request, and the SQL kept beside it (`semantic-sql:`) only for reading; if the two ever disagree, the request is the truth.
- **The query is pinned**, copied into the item when it is verified. A person's own saved query can change or be deleted, so a verified
  query never just points at one. It may also name a model-level saved query (`saved-query: Sales/revenue_by_region@3`) when the answer is
  one, by version.
- **What is verified is the query's logic, not its numbers.** Run by different people, it applies each one's own access rules (plan 22
  section 2), so a regional manager and the finance director get different rows from the same verified query.

**2. How one is made.** Through the suggestion path of section 2.5, so the owner of the model's knowledge approves it:

- **From an answer:** in the knowledge app, the console, or an agent's conversation, a person who sees a query and its result can choose
  **Verify this answer**, giving the question as they would ask it. The suggestion carries the query as a request, its parameters, the model version
  and a preview of the result.
- **From an agent:** an agent can suggest one (`knowledge suggest`, with its prompting setting) after a person confirms its answer.
- **By hand:** developers and analysts write the file in a workspace.

**3. How agents use them.**

- **Found by the question.** The `questions:` are what is embedded, so a person's question is matched against how others asked it.
- **Given as a verified example**, marked as such, with who verified it and when. The agent can run it directly (`query run-verified
  <id>` with the parameters filled in) or adapt it; either way the turn records which verified query it used.
- **Shown to the person as verified:** an answer that came from a verified query says so ("verified by Maria, 6 November"), and one adapted
  from it says that instead.

**4. They are the front-end agents' evaluation set.** Each verified query is a case (section 3.4): the question goes to the agent, the
agent's query and the verified query are both run, as the same service account against the same data, and their **rows are compared**. No
judge model is needed for these cases, and the set grows as people verify answers.

**5. When the model changes.** When a new version of the semantic model is staged, the `knowledge` module checks each verified query about
it: it still parses, its members still exist, its result has the same columns. One that fails is marked **needs re-verification**: it is
still found, but no longer given as verified, and its owner is told. A model change is never blocked by its verified queries; the console
lists what needs another look.

**6. Access.** A verified query is visible to whoever may query its model, and running it applies that person's rules (item 1).

### 2.10 Commands and screens

**Decided (owner, 2026-11-06).**

**Commands**, on the shared command surface (so the same specs give the console, MCP tools and agents' tools), noun `knowledge`:

| Command | Does | Traits (section 3.3) |
| --- | --- | --- |
| `knowledge search <text> [--about <subject>] [--kind <kind>] [--channel <c>]` | vector search, access-filtered, grouped per item (section 2.8) | reads definitions; reads data when a result holds column values (section 2.7) |
| `knowledge show <id> [--section <heading>] [--version <v>]` | an item, a section of it, or an earlier version | reads definitions |
| `knowledge about <subject>` | everything about a subject: its generated item, the instructions, guardrails, references and verified queries scoped to it | reads definitions |
| `knowledge context <agent> [--in-play <subject> ...]` | what that agent would be given, in order, with the tokens each part uses against its budget (section 3.5b) | reads definitions |
| `knowledge check [<path>]` | checks knowledge files in a workspace: format, subjects, roles, owners, and the budgets of the agents they reach | reads definitions |
| `knowledge suggest <id>\|--new ...`, `knowledge verify --question <q> --request <file>` | a suggestion, or a verified query from an answer (sections 2.5, 2.9) | writes definitions |
| `knowledge suggestions list\|show\|approve\|reject\|withdraw` | the suggestion queue; approving needs to be the item's owner | writes definitions |
| `knowledge undescribed [<subject>]`, `knowledge orphans` | what lacks a description (section 2.7); items whose subject is gone (section 3.5c) | reads definitions |
| `knowledge index status\|rebuild [--channel <c>]` | the index per channel: embedding model, counts, progress; rebuild on request (an administrator) | reads definitions / changes structure |

**The knowledge app** (served by the serving layer, section 2.5), for everyone who may see knowledge:

1. **Search:** one box, results with their excerpts, title, heading path and source, filtered by kind (instruction, guardrail, reference,
   verified query, generated), subject and owner.
2. **A subject's page** (a model, a measure, a project): its generated description, then what is scoped to it, its verified queries, and
   what is undescribed, with **Suggest a description**.
3. **An item's page:** its text and fields, which channels it is in, its history with a comparison of any two versions, **Suggest a
   change**.
4. **The form** for a new item or a change: the `knowledge: 1` fields as fields (role, about, for, owner), the text in a Markdown editor
   with a preview.
5. **My suggestions** and, for owners, **Approvals:** each suggestion beside the current text, the agents it reaches and any budget it
   would exceed, approve or reject with a comment, and the ones that **need another look**.
6. **Verified queries** per model: the list, the ones that need re-verification, and **Verify this answer** wherever a query and its result
   are shown.

**The console adds**, for administrators and developers:

7. **The index:** per channel, the embedding model, item and chunk counts, rebuild progress, items that failed their checks, orphans.
8. **An agent's context:** choose an agent and the subjects in play, and see exactly what it would be given, in order, with each part's
   share of the budget (the same as `knowledge context`).
9. **Knowledge projects in workspaces:** the editor checks knowledge files as they are typed (`knowledge check`) and previews the
   subjects each file reaches.

### 2.10a Standard front-end tools

**Decided (owner, 2026-11-06)**, proposed the same day from the owner's direction that the suite builds standard front-end tools for searching, showing and suggesting
edits to knowledge. They are a shared web package, **`@dbdatakit/knowledge-ui`**, beside `@dbdatakit/shell`, `@dbdatakit/visuals` and
`@dbdatakit/store-ui`, used by the knowledge app, the console, agents' chat and assistant panels, and any app that wants them:

| Component | Shows or does |
| --- | --- |
| **Search** | a search box and grouped results (excerpt, title, heading path, source, kind), with filters; it can query several search sources at once (section 2.11) |
| **Item view** | an item's text, fields, channels and history, with a comparison of two versions |
| **Subject panel** | everything about one subject, for embedding beside that subject in any screen (a model in the console, a measure in a dashboard) |
| **Suggest edit** | the form for a new item or a change, with the Markdown editor and preview, sending a suggestion (section 2.5) |
| **Approvals** | the suggestion queue for an owner, each beside the current text, with the agents it reaches and any budget it would exceed |
| **Verified badge** and **Verify this answer** | the mark on an answer that came from a verified query, and the action that proposes one (section 2.9) |
| **Context view** | what an agent was given in a turn, or would be given (section 2.10, item 8) |

- **One typed client** for the knowledge API, generated from the command specs like the other typed clients.
- **They work inside sandboxed apps** (plan 26): calls go through the shell's token broker, as the person.
- **They look like the suite** because they are built on the shell's components and themed by its variables, so a portal's branding
  applies to them too.

### 2.11 What else is searchable, and how

**Decided (owner, 2026-11-06)**, proposed the same day from the owner's question: do reports, semantic models, agents, source code and other things get into search, and
do they use the knowledge index?

**The rule: the knowledge index holds what describes the system. Content is searched where it is kept, by that system's own rules.**

| What | In the knowledge index? | How it is searched |
| --- | --- | --- |
| **Definitions**: semantic models and their members, Build models and columns, DbDataStore models, data sources, apps, portals and navigation, agents, skills, saved queries, reports and dashboards (plan 16 section 2a), integrations | **yes, as generated knowledge** (section 2.7), one item per thing, access-filtered by who may use it | knowledge search |
| **Knowledge**: instructions, guardrails, references, verified queries | yes (sections 2.1 to 2.9) | knowledge search |
| **Source code and files** in projects (models' SQL, actions, integration code, app source, configuration) | **no** | **workspace search** on the Workspace server: exact text and file names, per repository and branch, under the rights to that project; for developers, administrators and back-end agents (`workspace search` as a tool) |
| **Data**: DbDataStore records, rows of a model | no | through the data's own reads: DbDataStore's search and filters, semantic queries; always under row and column rules |
| **Documents' contents** | only when a knowledge source includes them as references (section 2.4) | otherwise through DbDataStore's documents |
| **Operational records**: runs, notifications, audit, suggestions, work items | no | their own screens and commands, with structured filters (by name, state, time) |

Why code stays out of the knowledge index: it lives on the private side (the Workspace server), not at the edge where the index is; it
changes per branch, many times a day; people search code for exact names and text more than for meaning; and its access is the
project's, not a subject's.

**Definitions not served by the serving group** (integrations run on the runner, plan 28) still get a generated item: when the Workspace
server publishes such a package, it also publishes its description (name, title, description, what it reads and writes, its owner; never
its code) to the serving group, so the knowledge module indexes it like the rest.

**One search box, several sources.** The search component (section 2.10a) asks several **search sources** at once and shows their results
in groups: knowledge always; workspace search in the console for people with rights to projects; DbDataStore records in an app that has
them. Each source applies its own access rules; nothing is copied between them to make the search work.

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

**Decided (owner, 2026-11-06)**, choosing what adds the least: one runtime library, `DbDataKit.Agents` (plan 16 section 1), loading a
published `agent/1` (a plan 30 kind), wherever the work is:

| Agent | Runs in | Acts as |
| --- | --- | --- |
| **Back end**, interactive (developers) | the console, where developers already work | the signed-in person, in an authoring workspace (a branch, plan 22 section 1); never on a published definition |
| **Front end**, interactive (business users, app assistants, outside MCP clients) | the serving layer's `mcp` module, so it works while the back ends are down | the signed-in person |
| **Unattended** (a step of an integration task) | the task's own process, on the runner that runs the task | the task's service account, with section 4.9's rule |
| **Evaluation runs** (section 3.4) | a task on the runner embedded in the console (plan 28 section 6a) | a service account, against a channel's bindings |

For the runner cases, the agent runtime is a library in the task's process, like the integration kit; the agent definition is a
dependency of the package and is published with it; its model connection is one of the package's slots, bound per channel (plan 30);
and its tools are the suite's APIs, called as the task's service account. No new interface is needed between the runner and the serving
layer.

Keys and model calls stay on the server, never in the browser (plan 16).

**First (owner, 2026-11-05): the back-end agent**, because it unlocks building apps, which the front-end agents then live in. It starts
from what exists: Build's command surface and its agent kit (`dbdatabuild project agent-kit`, `DbDataBuild.Cli/AgentKit/SKILL.md`),
which outside agents already use over MCP.

### 3.3 Approvals: prompting per action

**Decided (owner, 2026-11-03): prompting is configured per action**, for every tool an agent may run (a command, or an action from
section 4). The settings, layers and defaults below were **decided by the owner on 2026-11-06**:

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
   gates. These run in CI on every pull request, with no model provider.
2. **Quality** is measured with an **evaluation set** (decided, owner, 2026-11-06): cases in the agent's `tests/evaluation/`, each a task, the
   fixtures it starts from, and what a good result is.
   - **Checked by the suite where it can be:** the right tools were called; a query returns the expected rows; for a back-end agent, the
     workspace it leaves behind compiles (`project compile`), its tests pass (`project tests run`), and its plan has no surprises. Most
     back-end cases can be judged without a model.
   - **Judged by a model only for free text**, against a written rubric, by a fixed judge model bound like any other (section 3.7), and
     shown as a judgement, not a fact.
   - **Run on the runner embedded in the console** (plan 28 section 6a) as a task, against a channel's bindings, by hand or on a schedule; never as a pull request gate,
     since it needs a real model and costs money. Each run records the agent version, the knowledge versions it was given, the model, the
     score per case and the cost.
   - **Promotion:** a version records its last score. An administrator can require a minimum score before a version is promoted to a
     channel, as a recorded choice; by default it is shown, not required.

### 3.5 What an agent has in context: instructions, guardrails and knowledge

**Decided (owner, 2026-11-06)**, proposed 2026-11-05, from the owner's question: what decides an agent's instructions, guardrails and permissions, and are they files
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
  the agent's evaluation (owner question 27-19). Running the evaluation sets of the agents it reaches automatically was
  proposed and is not agreed. Decided instead (owner, 2026-11-06): **knowledge goes through channels like every other definition** (plan 21,
  plan 30). A changed item is published to a test channel first, where the agents of that channel see it; their evaluation sets can be run
  there (by hand, or on a schedule an administrator sets); then the item is promoted to production. The approval shows which agents the
  item reaches. Nothing reaches production agents without being promoted.

### 3.5a A worked example

Explained for readers, outside this plan, in [`docs/agents/context.md`](../agents/context.md) and
[`docs/agents/context-window.md`](../agents/context-window.md), in the console's help format, to register once agents are built.

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
| chosen by **scope**: exact, the same every time | chosen by **search**: vector search (section 2.4), ranked, cut to the room left |
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

### 3.5c How scopes are written, and who owns them

**Decided (owner, 2026-11-05)**, items 1 to 6. It follows conventions the suite already has: Build's folder defaults
(`_dbdatabuild.yml`, nearest wins), its selectors (`tag:`, `property:`, `+model`), the strict YAML reader with diagnostics naming file and
line, and lineage's stable identities for subjects (plan 18 section 4).

**1. The item carries its own scope.** Who owns the knowledge owns its scope; there is no separate central rules file to keep in step. For
an item kept as a file, the scope is a YAML header at the top of the Markdown (front matter):

```markdown
---
knowledge: 1                      # the format and its version (strict: an unknown key is refused, naming the file and line)
title: Revenue means net revenue
role: instruction                 # instruction | guardrail | reference
about:                            # what it is about: any one of these puts it in scope
  - semantic-model: Sales
    measure: revenue              # narrows the entry above: this measure, not the whole model
for:                              # who it is for: each key narrows; leave a key out for "all"
  audience: [front-end, back-end]
owner: finance-data               # a group of the identity home; approves changes to it
---
Revenue in the Sales model means **net revenue**: gross sales minus returns minus discounts. ...
```

The same fields are the record's fields if an item is kept somewhere other than a file; where knowledge is kept (section 2.1) is still
open, and the format does not depend on it.

**2. Defaults by place.** A folder of knowledge can hold `_knowledge.yml` with `defaults:` for everything beneath it, nearest wins, as
Build's `_dbdatabuild.yml` does. And **knowledge kept inside the project it is about needs no `about:` at all**: a file in a Build
project's `knowledge/` folder is about that project, reviewed and versioned with it. So K1 of the worked example is just a sentence in
`sales-warehouse/knowledge/naming.md` with `role: instruction`.

**3. Subjects are named the way the catalog names them** and are checked against it:

| Entry | Means |
| --- | --- |
| `build-project: sales-warehouse` (optionally `folder: models/marts`, `model: marts.dim_region`) | that project, that folder of it, that model |
| `semantic-model: Sales` (optionally `measure:`, `dimension:`) | that semantic model, or one member |
| `store-model: Customers` (optionally `field:`) | a DbDataStore model |
| `connection: warehouse`, `app: expenses`, `integration: orders-feed` | those objects |
| `tag: finance`, `property: tier=1` | anything with that tag or property, as Build's selectors |

An entry with several keys narrows (that measure of that model); several entries widen (any of them). An item applies when **something
in play is its subject or is inside it**: an item about a project applies whenever anything in the project is in play; an item about one
measure only when that measure is.

**4. Agents and administrators can narrow or add, without editing the items:**

- **an agent definition** can limit what it draws on (`knowledge: { from: [...], exclude: [...] }`), and can ask for a reference to be
  always given (`always: [...]`), which then counts against its budget (section 3.5b);
- **the install policy** can give an item to every agent, or to an audience, whatever the item's own `for:` says.

**5. Who checks it: one library.** `DbDataKit.Knowledge` reads the format, resolves every subject against the catalog, and works out which
agents each item reaches. It runs:

- when an item is saved or approved: an unknown key, an unknown subject or a bad role is refused with its file and line;
- when an agent is published or promoted: its always-given set and its budget (section 3.5b);
- on every turn: what is in play, and so what is given.

A subject that later disappears (a model dropped) leaves its items **orphaned**: they reach nobody, and the console lists them for their
owners.

**6. Install-wide instructions, for one product or several** (decided, owner, 2026-11-05). The subjects above are all objects inside a
product, so "every agent working on Build or Sync in this install uses our naming standard" needs two more things:

- **Products and the install as subjects:** `product: build` (or `sync`, `store`, `integration`, `apps`, `serving`, `runner`,
  `console`), a list of them (`product: [build, sync]`), or `install: true` for everything. A product is in play when the agent works on
  something of it: a workspace on a Build project, a Build command, a Sync replication it reads.
- **A home for them: the install's knowledge project.** A git project of its own, authored like any other through workspaces in the
  console (plan 22 section 1), with folders that set their scope by place through `_knowledge.yml` defaults:

```
install-knowledge/
  _knowledge.yml            # defaults: { about: [{ install: true }] }
  shared/                   # everything in the install
  build/_knowledge.yml      # defaults: { about: [{ product: build }] }
  build/naming.md
  sync-and-build/_knowledge.yml   # defaults: { about: [{ product: [build, sync] }] }
  policy/                   # the administrators' text: role guardrail, owner: administrators
```

It is published like other definitions (plan 30) to wherever agents run: the console for back-end agents, the serving group for front-end
ones. If knowledge ends up kept somewhere other than files (section 2.1), this home follows that decision.

**Policy settings and policy text, apart.** The install policy (`agent-policy`) keeps what is not text: prompting minimums, limits, data
rules for cloud providers. What administrators want agents to *read* ("always say an answer was generated") is a knowledge item in the
install project's `policy/` folder, with `role: guardrail` and the administrators as owner, so all text an agent reads is written,
scoped, checked and recorded one way.

**Broad to narrow.** Scoped items are given from the broadest to the narrowest (install, product, project, folder, model, member), and
the agent is told that narrower ones refine broader ones. Guardrails are given in a section of their own, marked as policy, and a narrower
instruction does not override them. Whether two texts contradict each other cannot be worked out automatically; it is a review question
when an item is approved, and the approval shows the other items with the same or a broader scope beside it.

### 3.6 What an agent may touch

Permissions are never instructions. An agent's reach is the **intersection** of:

1. **the person's own access**: roles, and the row and column rules of plan 22 section 2 (an unattended agent: its service account's);
2. **the agent's scopes**: the semantic models, DbDataStore entities and operations (read, write, change request) its definition names,
   as an app's manifest does (plan 16, "Sign-in decisions" item 1: the token's scopes come from the definition);
3. **the install policy**, which can narrow further (no data reads for agents on a cloud provider, for example).

The serving layer enforces this on every call, through the token's audience and scopes, as it does for apps. The only way past the
person's own access is an **elevated action** (section 4.5), which holds its own grants and checks each request itself.

### 3.7 Model providers

**Decided (owner, 2026-11-06):** a model provider is a kind of connection in `connection/1`, like an outside system (question 10): its endpoint,
its key as a secret reference, the models it allows and their prices, bound per channel. An agent names a model slot; each channel binds
it, so a test channel can use a local or cheaper model. An install that must keep data in its tenant binds only Microsoft Foundry
connections. Whether a provider is cloud or local is part of the connection, so the install policy can tell them apart (3.6, 3.3).

### 3.7a Embedding models, including one on the local CPU

**Decided (owner, 2026-11-06).** The built-in kind is the embedded one; Ollama and OpenAI-compatible local servers are supported and
unit tested too, and **double as stand-ins for the public OpenAI API in tests**: a suite can talk to a local OpenAI-compatible server
where it would otherwise need a hosted provider (beside plan 25's recorded responses). Knowledge search needs an embedding model (section 2.4). It is a model provider
connection like any other, with **purpose: embeddings**, bound to the install's embedding slot per channel. Three ways to have one that
runs on the machine's own CPU, with no data leaving it:

| Kind | What it is | What the administrator does |
| --- | --- | --- |
| **Built in** | the suite runs a small embedding model itself, in process, with ONNX Runtime and the model's tokenizer; no other service | choose one of the models the suite offers; its files are a published artifact (plan 30), so an offline install gets them from its own state |
| **Ollama** | a local Ollama server with an embedding model pulled | install Ollama, `ollama pull` a model, add a connection with its address (`http://localhost:11434`) and the model name |
| **Any local server with an OpenAI-compatible endpoint** | llama.cpp's server, Foundry Local, and others | add a connection with its address and model name |

Hosted providers (OpenAI, Microsoft Foundry) are connections of the same purpose. Anthropic offers no embedding endpoint of its own, so an
install that uses Anthropic for chat binds another connection for embeddings.

**In the console:** Connections, New, Model provider; choose the kind; for built in, choose the model; **Test** embeds a sample sentence
and shows that it answered and the number of dimensions; then bind it to the embedding slot of each channel. The index is built in the
background, and the console shows its progress.

**One model per index.** Items and questions must be embedded by the same model, so the index records the model and its number of
dimensions (the vector columns are sized by it: `VECTOR(n)` on SQL Server, `vector(n)` on PostgreSQL). Changing the bound model rebuilds the
index in the background; search keeps using the old index until the new one is complete. A channel bound to a different model has an
index of its own.

**To check when it is built:** which small models to offer built in, and their licences; how fast they embed on a typical server CPU; that
Foundry Local serves embedding models.

### 3.8 Transcripts and the audit

Kept in the `agents` module of the unified state (plan 29), large tool results as payloads.

- **The audit, always kept:** every tool call, its arguments and outcome, every approval and refusal, and the context items given (3.5),
  each naming the person and the agent and its version (owner, 2026-11-05: these are audit records).
- **The conversation text** is history, with the default retention of 30 days (plan 29 section 3.3), adjustable.

### 3.9 Limits and cost

**Decided (owner, 2026-11-06):** per conversation, per person and per install, on turns, tool calls, tokens and cost. Cost is the provider's reported
usage times the price on the connection (3.7). A limit reached stops the conversation with a message; administrators are told at a
threshold.

### 3.10 How an agent is stored

**Decided (owner, 2026-11-05).** **An agent is a project in git**, authored in a workspace like any other (plan 22 section 1), built, checked and
published as the `agent` kind (plan 30). What it learns and does at run time is **not** in git; that is state.

```
agents/build-helper/
  agent.yml                 # the definition (agent: 1)
  instructions/role.md      # its own instructions
  knowledge/                # knowledge about this agent's work; implicit scope: this agent
  skills/                   # skills of its own (3.13); shared ones are referred to by name
  tests/conversations/      # scripted conversations (plan 25)
  tests/evaluation/         # questions with known good answers (section 3.4)
```

```yaml
agent: 1
name: build-helper
title: Build helper
audience: back-end
model: { slot: default }                  # bound per channel (3.7)
instructions: [instructions/role.md]
skills: [dbdatabuild]                     # Build's agent kit, as a skill (3.13)
tools:
  - command: project compile              # prompting from its traits (3.3)
  - command: connection deploy
    prompting: ask
scopes: { build-projects: all }           # within the person's own access (3.6)
knowledge: { from: [install, project] }
memory: { person: true, workspace: true } # 3.11
subagents: [semantic-modeler]             # 3.12
limits: { turns: 60, cost: 5.00 }
```

**Building it** checks what can be checked before anyone talks to it: every tool, skill and subagent exists, every scope resolves, the
always-given set fits the budget (3.5b), the scripted conversations pass. **Publishing** stores the definition and its files as
artifacts by hash, signed (plan 30 section 6), with the model and connections bound per channel.

**At run time, in the `agents` module of the unified state:** conversations and their turns, the record of what each turn was given, tool
calls and approvals (the audit, 3.8), memories (3.11), usage and cost.

### 3.11 Memories

**Decided (owner, 2026-11-05).** What an agent keeps from one conversation to the next, in four kinds, each with its own owner and reach:

| Kind | Example | Kept where | Who sees it | Lasts |
| --- | --- | --- | --- | --- |
| **The conversation** | what was said and done so far | the conversation's turns | the person, and auditors | history (30 days by default); when it grows past the budget, older turns are summarized, and the summary is recorded |
| **The person's** | "I work on the EMEA warehouse"; "show SQL Server examples" | the `agents` module, per person and agent | **only that person**, who can list, edit and delete it | until the person deletes it |
| **The work's** | notes on a branch: "the region fact is half done; the backfill question is open" | with the workspace (plan 22), or a work item (section 5) | whoever works on that workspace or item | as long as the workspace or item |
| **Shared** | "the orders table is late every Monday" | **not a memory**: proposed as knowledge (a draft for an owner to approve, section 2.1) | everyone the item's scope reaches, once approved | as knowledge |

- **Written through a tool** (`memory remember`, `memory forget`), with its own prompting setting, so a person sees what is kept.
- **Read through the same assembly** as everything else: memories are given after the agent's instructions and before references,
  inside the budget, and recorded in the turn.
- **Never secrets**, and nothing an agent read from data the person could not see anyway; a memory is the person's, so it carries no one
  else's access.
- **Nothing silently shared:** anything that would change what *other* people's agents do goes through knowledge and its approval.
- An administrator can turn each kind off per agent or per install.

### 3.12 Subagents

**Decided (owner, 2026-11-05).** An agent can hand part of its work to another agent, for three reasons: **specialization** (a Build agent asks a
semantic-model agent to design the measures), **a clean context** (a subagent reads forty files and returns a two-paragraph summary, so the
main conversation stays small), and **parallel work**.

- **Declared:** an agent lists the agents it may call (`subagents:`); each appears to it as a tool.
- **Never wider:** a subagent acts as the same person, and its reach is the person's access narrowed by the **parent's** scopes and then
  its own. A subagent cannot do what its parent could not.
- **Its own context:** it gets its own instructions, skills and scoped knowledge by the usual assembly, plus only what the parent passes
  in the call, not the parent's whole conversation.
- **Same prompting:** its actions ask the person as their settings say, through the parent's conversation; unattended, section 4.9's rule.
- **Counted to the parent:** turns, tokens and cost count against the parent conversation's limits; depth is limited (two levels by
  default).
- **Recorded inside the parent's transcript**, as a nested part, so the audit reads as one conversation.

### 3.13 Skills

**Decided (owner, 2026-11-05).** A **skill** is a packaged capability an agent picks up when the task needs it: a `SKILL.md` with a name, a
description and instructions, optional resource files (templates, examples, schemas), and optionally the actions it relies on (named,
not code: code stays an action, under section 4.7's trust rules). Build's agent kit is already one: `DbDataBuild.Cli/AgentKit/SKILL.md`
has the name and description in its header.

**How it differs from knowledge:** an instruction is given whenever its scope applies. A skill is **offered**: every turn the agent sees
each of its skills' **names and descriptions** (cheap), and it loads a skill's **body** when it decides the task needs it, through a tool
(`skill load`). So an agent can have many skills without filling its context.

- **Where skills live:** inside an agent's project (for that agent), or published on their own as the `skill` kind (plan 30) and named
  by agents that use them; a product can ship skills with its release (Build's, first).
- **Budget:** descriptions count against the always-given budget; when an agent is published, its largest skill body must also fit in
  the room left, so loading any one skill never forces instructions out.
- **Recorded:** a loaded skill, with its version, is part of the turn's record.
- **Outside agents too:** a published skill can be exported in the format outside agents read, as `dbdatabuild project agent-kit` does
  today, so internal and outside agents learn from the same text.

### 3.14 Conversations across versions

**Decided (owner, 2026-11-06).** A conversation **stays on the agent version it started with**, the way a run stays on its package version (plan 30
section 3.5). When a newer version is active and the person comes back to an open conversation, the agent says so and offers to carry on
with the new version; each turn records the version it ran on. A version that is rolled back because it misbehaved can be **ended**:
open conversations on it are closed with a message, as an administrator's recorded choice.

### 3.15 When a model provider fails

**Decided (owner, 2026-11-06).** A model slot can be bound to an ordered list of connections in a channel (a primary and a fallback). A provider error
is retried with back-off, then the next connection is used, and the turn records which model answered. A fallback is only ever one the
install policy allows for that agent (a tenant-only install never falls back to a cloud provider). With no model left, the conversation
stops with a message; nothing is half-run, since tool calls only start after the model has answered.

### 3.16 Where people talk to agents

**Decided (owner, 2026-11-06):**

| Surface | Agents | First? |
| --- | --- | --- |
| **The console's chat** | back-end agents, and agents for administrators | **yes**, with the back-end agent |
| **An app's assistant panel** (plan 16 section 2) | front-end agents the app's manifest names | with the front-end agents |
| **MCP, for outside clients** | the commands and actions as tools, as today; later, a published agent offered as one tool ("ask the sales agent") | tools now; agents later |
| **Teams, Slack, email** | front-end agents, reached through an integration (section 4) | later |
| **An Excel add-in** (section 3.16a, proposed) | published agents in a task pane, with data-source tools that put results into the workbook | with the front-end agents |

Every surface signs the person in through the identity home, so who they are, what they may touch and what they are asked are the same
everywhere.

### 3.16a An Excel add-in

Proposed (2026-11-06), from the owner: published agents may also need an Excel app that provides agent and data-source tools. Excel is
already a client of the serving layer through WebDAV (documents), XMLA (PivotTables) and Power Query (feeds) (plan 16, "Sign-in decisions"
item 3); none of those gives a person an agent, or a way to pick a semantic model and drop its numbers into a sheet. An **Office add-in for
Excel** would:

| Part | Does |
| --- | --- |
| **Agent pane** | a task pane with a published front-end agent, as in an app's assistant panel; the agent's tools include the workbook (read the selected range, write a table to a sheet), each with its prompting setting |
| **Data-source tools** | browse the semantic models and saved and verified queries the person may use (with `@dbdatakit/knowledge-ui`'s search and subject panel), run one, and insert the result as an Excel table that remembers its query |
| **Refresh** | refresh a table, or all of them, from their queries, under the person's own access rules at the time of the refresh |
| **Functions, later** | Excel custom functions over saved and verified queries (a cell formula that returns a query's value) |

- **Served like any app:** the add-in's pages are an app of the serving layer (plan 26), and its manifest is what an administrator deploys
  to people's Excel (centrally, or by sideloading for a trial).
- **Sign-in through the identity home:** the add-in opens the home's own sign-in page in Office's sign-in dialog and gets the same scoped
  token an app gets (plan 16, "Sign-in decisions" item 1); where the install uses Entra, Office's single sign-on can be accepted the way
  Power Query's Entra tokens are (item 3).
- **The workbook is a surface, not a store:** what the add-in writes into a sheet is a copy at that moment; the rules and the data stay on
  the server.
- **To check when it is built:** the manifest format to use, Office's sign-in options on Windows, Mac and the web, and custom functions'
  limits. None of this has been tried in this repository; plan 14 R7's Excel VM recordings are where it would be verified.

### 3.17 Untrusted content

**Decided (owner, 2026-11-06)**, from plan 16's rule that tool results are data, not instructions:

- **Only the assembly gives instructions** (section 3.5). Tool results, rows, documents, web pages, files in a workspace and messages from
  outside systems are given to the model **marked as data**, with where they came from.
- **Unreviewed sources stay references** (section 2.4) and are marked as such.
- **What could do harm still asks**, whatever the content said: the prompting minimums of the install policy (section 3.3) are enforced by
  the runtime, so text that tells the agent to send, delete or publish something cannot skip the person.
- **Answers are checked:** a view an agent returns must validate against its schema (plan 16 section 2a: inline data only, no URLs), and
  links in an answer are shown, never fetched by the suite on the person's behalf.

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

**Name: DbDataBacklog** (owner, 2026-11-06; the working name since 2026-11-03). "Backlog" names one view of the tool. Other candidates: DbDataWork (plain, and it covers backlog, sprints and boards),
DbDataBoard, DbDataTrack, DbDataTasks. DbDataPlan is ruled out, since Build already uses "plan" for its plans. **Owner question 2.**

**Goal.** Backlog, sprint and work management for teams that build on the suite, where a work item can point at the thing it is about.

### 5.1 Its own data structure, augmented by DbDataStore

**Decided (owner, 2026-11-06)**, replacing an earlier proposal that built it on DbDataStore models: **the core data structure is part of
the application itself.** DbDataBacklog is set up when it is installed and configured, while DbDataStore models are authored after all of
the other configuration, so the backlog cannot depend on them. DbDataStore can **augment** its data (custom fields), never hold its core.

| Part | Built with |
| --- | --- |
| Work items, epics, sprints, boards, comments, links, states and their transitions | **the application's own structure**: a `backlog` module of the unified state (plan 29), owned by the backlog library, created on install and migrated with each release like any other module |
| Its rules (who may move an item to which state, required fields, what a transition does) | the backlog library, served by a `backlog` module of the serving layer (its API), so the app is a client of it like any other app |
| Attachments | the payload store (plan 29 section 3.6) |
| Custom fields | DbDataStore extension models attached to the work item type (section 5.4); optional, added after install |
| The screens: backlog, board, sprint, item | an app shipped with the suite (plan 26), served by the serving layer; a portal for teams that want it alone |
| Notifications and digests | the delivery service (plan 22 section 5) |
| Automatic items: a failed run, a check that blocks, an approval waiting | integration tasks on the runner (section 4) |
| An assistant: write and split items, estimate, plan a sprint, summarize a sprint | an agent definition (section 3) |

It is still **not a server of its own**: its runtime is a module of the serving layer and its state a module of the serving group's
state, as the knowledge module's is.

### 5.2 What makes it worth having in this suite

1. **Links to suite objects:** an item can name a Build model, a Sync replication, a semantic model, a Store entity, an app, a run or a
   change request, and those objects show their open items.
2. **Links to workspaces:** an item can open an authoring workspace (a branch), and the item follows its commits, merge and publish
   (plan 22 section 1). A back-end agent can take an item, work in its workspace, and hand it back for review.
3. **Knowledge from finished work:** a closed item's resolution can be proposed as a knowledge draft (section 2.1).

### 5.3 Scope

**Decided (owner, 2026-11-06):**

1. **Its own items:** work items, epics, sprints, boards, estimates, comments and attachments.
2. **Links to the suite from the start** (section 5.2): to suite objects, to DbDataWorkspace workspaces (following their commits, merge and
   publish), to GitHub pull requests, and back-end agents that take an item and hand it back for review.
3. **Outside tools (Jira, Azure Boards) are extremely low priority and may never happen.** If they ever do, an import would be a
   DbDataIntegration template, not a feature of DbDataBacklog. No two-way sync is planned.

### 5.4 Custom attributes for any object type

**Decided (owner, 2026-11-06)**, the rule and how, for the whole suite: the same holds for every object type, not only work items. An object's core
structure belongs to the product that owns it; **DbDataStore can augment any object type with custom attributes**.

How:

- **An extension model** is a DbDataStore model attached to an object type (`work-item`, `semantic-model`, `build-model`, `app`,
  `connection`, ...): its fields are the custom attributes, and each record is keyed by the object's stable identity (the same identity
  the catalog and lineage use, plan 18 section 4).
- **The object's own screens show its custom attributes**, edited with DbDataStore's validation, history and audit, under DbDataStore's
  permissions; the shared Store controls (`@dbdatakit/store-ui`) render them.
- **They never change what the product does.** A custom attribute cannot make a work item skip a state or a model build differently; the
  owning product's rules stay its own.
- **An object that goes leaves its attributes orphaned**, listed for their owners, as knowledge items are (section 3.5c).
- **Two kinds of attribute, kept apart:** properties written in a definition (Build's `tags` and `properties`, in git, versioned with the
  definition) and custom attributes kept in DbDataStore (edited by business users at run time). Search and generated knowledge (section 2.7)
  can show both, labelled.

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

**Decided (owner, 2026-11-05): option B, as one Workspace server.** The apps back end of plan 22 (DbDataApps) becomes a **unified
Workspace server** where authoring happens: workspaces (git branches), builds in sandboxed child processes (the .NET SDK for integrations
and actions, bun with vendored packages for apps), tests, the publisher's signing key, and Publisher pairings with serving and runner
groups. It hosts app, agent, integration, skill and knowledge projects, each kind keeping its own name, command nouns and screens. **Build
and Sync move into it eventually** (once their runs are on the runner, plan 28 section 8), to reduce the number of services and the size
of the architecture. Runtime stays elsewhere: the serving layer, the runner, the console for back-end agents. **Its name is DbDataWorkspace**
(owner, 2026-11-06); "the Workspace server" in this plan means it.

## 7. Order

| Wave | What | Needs |
| --- | --- | --- |
| 0 | What plan 16's agents need: the suite-wide command spec test, `DbDataKit.Agents` with the scripted fake, the provider proof against the four providers | 13a W4a work; plan 25 section 2 |
| 1 | `agent/1` and the **back-end agent** (owner, 2026-11-05): in the console, working in an authoring workspace on a Build project, with Build's agent kit as its instructions, prompting per action and the traits, context assembly from git files | wave 0; shared authoring (plan 22 section 1) |
| 2 | Actions: the shared script format; Sync's scripts move onto it; a `.csx` or SQL action as a tool of an agent and an endpoint for an app, in warm worker processes in the serving layer | wave 1; the serving contract (plan 20 section 10) |
| 3 | Front-end agents in the serving layer; curated business knowledge in the Workspace server's knowledge editor (glossary, metrics, verified queries), drafts from agents, evaluation sets; embedding search if chosen | DbDataStore drafts (plan 21); access rules (plan 22 section 2) |
| 3b | Elevated actions: action principals, grants to actions with field and row conditions, *run* grants to people, validate then apply or reject, the declarative form, audit of both identities | wave 2; DbDataStore permissions (plan 15); the identity home's service principals (13a U20) |
| 4 | The runner (plan 28) with its progress store on the unified state structure (plan 29), then DbDataIntegration: outside connections, the integration kit, templates, inbound and outbound patterns, webhooks; actions that enqueue integration tasks; back-end agents in workspaces | shared authoring (plan 22 section 1) |
| 5 | DbDataBacklog: its `backlog` modules (state and serving), the app, integration tasks, the assistant; custom fields once DbDataStore's extension models exist | the app framework's views; the delivery service |

Waves 1 and 2 can run side by side once wave 0 is done; wave 5 can start its DbDataStore models as soon as DbDataStore can hold them.

## 8. Questions for the owner

| # | Question | Proposed |
| --- | --- | --- |
| 1 | Which of these are servers? | **Decided** (owner, 2026-11-05): one **Workspace server** for authoring (the apps back end, broadened) hosting app, agent, integration, skill and knowledge projects, with Build and Sync moving into it eventually; DbDataRunner a service of its own; knowledge, actions and DbDataBacklog are not servers (DbDataBacklog's runtime is a serving-layer module with its own state module, section 5.1) (section 6). Its name: **DbDataWorkspace** (owner, 2026-11-06) |
| 2 | The work tool's name | **Decided** (owner, 2026-11-06): DbDataBacklog |
| 3 | Languages and packaging | **Decided for integrations** (owner, 2026-11-04): `.csx` scripts, compiled .NET projects, any executable through the runner protocol, and hybrids such as a .NET notebook over a project (plan 28 section 4). Actions: `.csx`, SQL, command calls and declarative elevated forms; **Python for actions is left out for now** (owner, 2026-11-06) |
| 4 | Embedding search | **Decided** (owner, 2026-11-05): vector search only, to start, on SQL Server 2025, PostgreSQL (`pgvector`) and SQLite (`sqlite-vec`), with an embedding model connection bound per channel; without vector search there is no knowledge search (section 2.4) |
| 5 | Agent approvals | **Decided** (owner, 2026-11-03): prompting is configured per action. **Decided** (owner, 2026-11-06) (section 3.3): 5a the three settings, 5b layers (action default, install policy with a minimum, agent definition; the strictest applies), 5c defaults from traits. **Decided** (owner, 2026-11-05): reading definitions and reading data are separate traits |
| 6 | Unattended agents in integration tasks | **Decided** (owner, 2026-11-04): actions set to *do not ask* run; actions that would ask become proposals (drafts, work items, notifications) (section 4.9) |
| 7 | The work tool's scope | **Decided** (owner, 2026-11-06): its own items, with links to suite objects, workspaces and GitHub pull requests from the start; Jira or Azure Boards extremely low priority, maybe never (section 5.3) |
| 8 | First audience | **Decided** (owner, 2026-11-05): the **back-end** agent first, because it unlocks building apps (section 3.2) |
| 9 | The integrations product's name | **Decided** (owner, 2026-11-04): DbDataIntegration (section 4.11) |
| 10 | Outside systems as connections | **Decided** (owner, 2026-11-04): a new kind in `connection/1`, sharing secrets, channel bindings and the console's view (section 4.6) |
| 11 | Elevated actions: who approves an action's grants on data, and who may give people *run* on it | **Decided** (owner, 2026-11-04): grants on data: an administrator and the DbDataStore model's owner, both, with an audit; *run*: the action's owner or an administrator (section 4.5) |
| 12 | Integrations with or apart from actions and Sync's scripts | **Decided** (owner, 2026-11-04): apart. Integrations are a product publishing to the runner; actions share one script format with Sync (section 4) |
| 13 | `agent` as a published kind (plan 30), with its model as a slot bound per channel | **Decided** (owner, 2026-11-06) (sections 3.1, 3.7) |
| 14 | Where agents run | **Decided** (owner, 2026-11-06): back end in the console, front end in the serving layer, unattended steps in the task's process on its runner, evaluation runs on the console's embedded runner (section 3.2) |
| 15 | Model providers as a kind of connection | **Decided** (owner, 2026-11-06) (section 3.7) |
| 16 | Transcripts | **Decided** (owner, 2026-11-05): tool calls, approvals and context given are audit, always kept; conversation text is history, 30 days by default (section 3.8) |
| 17 | Limits and cost | **Decided** (owner, 2026-11-06) (section 3.9) |
| 18 | Context: four homes, roles (reference, instruction, guardrail) and scopes on knowledge, assembly in a fixed order, hard guardrails enforced by the runtime, a budget for what is always loaded | **Decided** (owner, 2026-11-06) (sections 3.5 to 3.5b); where curated knowledge is kept is not decided (section 2.1) |
| 19 | What to do when a change to a bound instruction changes agents without their evaluation | **Decided** (owner, 2026-11-06): knowledge goes through channels (question 29); automatic evaluation runs are not used |
| 20 | An agent's reach: the person's access, narrowed by the agent's scopes and the install policy, enforced by the serving layer | **Decided** (owner, 2026-11-06) (section 3.6) |
| 21 | How scopes are written: on the item (YAML front matter, or the same fields on a record), folder defaults and implicit scope by place, subjects named as the catalog names them, agents and the install policy narrowing or adding, one library checking it | **Decided** (owner, 2026-11-05), items 1 to 5 of section 3.5c |
| 22 | Install-wide instructions for one or several products: products and the install as subjects, an install knowledge project published like other definitions, policy settings apart from policy text, given broad to narrow with guardrails not overridable | **Decided** (owner, 2026-11-05) (section 3.5c, item 6) |
| 23 | Knowledge stores: sources (git files and generated first; the Workspace server's knowledge editor, documents, outside systems later) giving one shape, provenance kept, unreviewed sources as references only; the index in the unified state | **Decided** (owner, 2026-11-05) (section 2.4); the index's search decided under question 4 |
| 24 | Agents as git projects (`agent.yml`, instructions, knowledge, skills, tests), checked at build, published as the `agent` kind; run-time state in the `agents` module | **Decided** (owner, 2026-11-05) (section 3.10) |
| 25 | Memories: conversation, the person's (theirs only), the work's (with the workspace or item); shared learning goes through knowledge, never memory | **Decided** (owner, 2026-11-05) (section 3.11) |
| 26 | Subagents: declared, never wider than the parent, their own context, same prompting, counted to the parent, depth two, nested in the transcript | **Decided** (owner, 2026-11-05) (section 3.12) |
| 27 | Skills: offered by name and description, loaded when needed; in an agent's project or published as the `skill` kind; Build's agent kit the first | **Decided** (owner, 2026-11-05) (section 3.13) |
| 28 | Evaluation: cases checked by the suite where possible, a judge model only for free text, run on the console's embedded runner, scores recorded per version, a minimum score optional for promotion | **Decided** (owner, 2026-11-06) (section 3.4) |
| 29 | Knowledge changes go through channels, so production agents only see promoted knowledge (the alternative to automatic evaluation runs, for question 19) | **Decided** (owner, 2026-11-06) (section 3.5) |
| 30 | A conversation stays on the agent version it started with | **Decided** (owner, 2026-11-06) (section 3.14) |
| 31 | Model fallback within what the install policy allows | **Decided** (owner, 2026-11-06) (section 3.15) |
| 32 | Surfaces: the console's chat first, then apps, MCP and messaging | **Decided** (owner, 2026-11-06) (section 3.16) |
| 33 | Untrusted content marked as data; harm still asks; answers checked | **Decided** (owner, 2026-11-06) (section 3.17) |
| 34 | Embedding models: a connection with purpose embeddings; built in (ONNX Runtime, in process), Ollama, or a local OpenAI-compatible server for the local CPU; one model per index, rebuilt on change | **Decided** (owner, 2026-11-06); local servers also stand in for the public OpenAI API in tests (section 3.7a) |
| 35 | Business knowledge: not DbDataStore; kept in git in the `knowledge: 1` format; business users edit in a knowledge app served by the serving layer; suggestions and approvals are records at the edge that the Workspace server collects, commits, merges and publishes through channels; agents suggest the same way | **Decided** (owner, 2026-11-06) (section 2.5, revised 2026-11-06) |
| 36 | The glossary and verified queries (plan 22 section 3 put the glossary in DbDataStore) move with business knowledge | **Decided** (owner, 2026-11-06) (section 2.5) |
| 37 | Knowledge projects published as `knowledge-project` to the serving group; the `knowledge` module checks and embeds at staging and switches at activation; all readers go through it; workspace changes laid over for back-end agents; generated knowledge derived from other published definitions | **Decided** (owner, 2026-11-06) (section 2.6) |
| 38 | Generated knowledge: derived from published definitions per channel, one item per subject, references by default, refreshed at staging and switched at activation; lineage and volatile facts through tools, not the index | **Decided** (owner, 2026-11-06) (section 2.7) |
| 39 | Build publishes its metadata documents to the serving group as a `build-metadata` kind | **Decided** (owner, 2026-11-06) (section 2.7) |
| 40 | Column values as knowledge only by opt-in, indexed under the column's access rules | **Decided** (owner, 2026-11-06) (section 2.7) |
| 41 | Descriptions added to semantic-model members and DbDataStore fields; suggested descriptions become changes to the definition's project when it is on the Workspace server | **Decided** (owner, 2026-11-06) (section 2.7) |
| 42 | The catalog is search over generated knowledge | **Decided** (owner, 2026-11-06) (section 2.7) |
| 43 | What is chunked: long references only; generated items, short references, instructions and guardrails never | **Decided** (owner, 2026-11-06) (section 2.8) |
| 44 | How: by structure first (headings, paragraphs, sentences; tables and code kept whole), sized from the embedding model, small overlap, title and heading path prefixed before embedding | **Decided** (owner, 2026-11-06) (section 2.8) |
| 45 | What the agent gets: grouped per item (at most three), joined when adjacent, with title, heading path and source; `knowledge show` for a section or the whole item | **Decided** (owner, 2026-11-06) (section 2.8) |
| 46 | Refresh per chunk by hash; access and scope from the item; `chunking:` settings per item over install defaults | **Decided** (owner, 2026-11-06) (section 2.8) |
| 47 | Verified queries: a `verified-query` knowledge item (questions, a pinned query in the JSON request form, the standard form, with semantic SQL accepted as input, who verified it, against which model version); the logic is verified, rows follow each person's rules | **Decided** (owner, 2026-11-06) (section 2.9) |
| 48 | Made through the suggestion path: "Verify this answer" from any surface, from an agent after a person confirms, or by hand | **Decided** (owner, 2026-11-06) (section 2.9) |
| 49 | Used by agents: found by the question, given as a verified example, run directly or adapted, shown to the person as verified | **Decided** (owner, 2026-11-06) (section 2.9) |
| 50 | Verified queries are the front-end agents' evaluation cases, judged by comparing rows | **Decided** (owner, 2026-11-06) (section 2.9) |
| 51 | Checked when the model changes; failures marked "needs re-verification", never blocking the model | **Decided** (owner, 2026-11-06) (section 2.9) |
| 52 | The `knowledge` commands, with their traits, including `knowledge context` to preview an agent's assembled context and budget | **Decided** (owner, 2026-11-06) (section 2.10) |
| 53 | The knowledge app: search, subject and item pages, the form, suggestions and approvals, verified queries | **Decided** (owner, 2026-11-06) (section 2.10) |
| 54 | The console's additions: the index, an agent's context, knowledge files checked in workspaces | **Decided** (owner, 2026-11-06) (section 2.10) |
| 55 | Standard front-end tools: `@dbdatakit/knowledge-ui` (search, item view, subject panel, suggest edit, approvals, verified badge, context view), one typed client, usable in sandboxed apps | **Decided** (owner, 2026-11-06) (section 2.10a) |
| 56 | Definitions of every kind (reports, semantic models, agents, apps, integrations and the rest) are searchable as generated knowledge in the same index; packages not served by the serving group publish their description to it | **Decided** (owner, 2026-11-06) (section 2.11) |
| 57 | Source code and files are not in the knowledge index; workspace search on the Workspace server, exact text, per repository and branch | **Decided** (owner, 2026-11-06) (section 2.11) |
| 58 | Data, documents' contents and operational records stay with their own systems; one search box asks several sources and groups the results | **Decided** (owner, 2026-11-06) (section 2.11) |
| 59 | DbDataBacklog's core structure is the application's own (a `backlog` state module and a serving-layer module), created on install; DbDataStore only augments it | **Decided** (owner, 2026-11-06) (section 5.1) |
| 60 | Custom attributes for any object type through DbDataStore; the core structure stays the owning product's | **Decided** (owner, 2026-11-06) as a rule; and how, with extension models keyed by object identity (section 5.4) |
| 61 | An Excel add-in: an agent pane, data-source tools that insert query results as refreshable tables, custom functions later; served as an app, signed in through the identity home | proposed (section 3.16a) |

## 9. Detailed plans to follow

Once section 8 is answered, each area gets a detailed plan with its definitions' formats, commands, storage, tests and slices (one slice,
one branch `suite/<id>-<short-name>`, a "Done when" test, as in 13a):

| Plan | Area | New slice letter (not yet used) |
| --- | --- | --- |
| [28](28-runner.md) | The runner (written at high level, 2026-11-04) | to choose when sliced (13a and plans 19 to 26 already use A B C D E F G H J K L M N O P Q R S T U V W X Y Z and AP; D and Q are decision and step numbers, the rest slices) |
| [29](29-unified-state.md) | One structure for the suite's state databases (written at high level, 2026-11-04) | |
| [30](30-publishing.md) | One way to publish, to the serving layer and the runner (direction decided, 2026-11-04) | |
| [31](31-report-catalogs.md) | Report catalogs, external (SSRS, PBIRS) and our own, in one place (written at high level, 2026-11-06) | |
| 32 | DbDataKnowledge | |
| 33 | DbDataAgents | |
| 34 | DbDataIntegration | |
| 35 | Actions: the shared script format, callable and elevated actions | |
| 36 | DbDataBacklog | |
| later | Sync and Build on the runner (plan 28 section 8), after its state design | |

## 10. What this changes in other plans (once approved)

| Plan | Change |
| --- | --- |
| 16 section 1 | the agent runtime loads published `agent/1` definitions; approvals are refined per section 3.3 |
| 16 section 2 (the app is untrusted UI) and "Sign-in decisions" item 1 (a token never wider than the person) | unchanged for an app's front end; a server-side elevated action holds its own grants on the data, people hold *run* on the action, and it validates each request before applying or rejecting it (section 4.5). The rule stays on the server, written in the action |
| 15 (DbDataStore permissions) | grants to action principals, with field and row conditions; history and audit record the action, its version and the person who asked |
| 20 section 4 | new modules: `knowledge` (reads), and action calls under `mcp` and `apps`; the runner (plan 28) as the scheduled and batch counterpart |
| 13 W2, 13a S-series | Sync's script manifest becomes the shared script format, used by apps and agents too; an old manifest is refused with a message |
| 22 section 1 | the apps back end becomes the Workspace server (section 6): shared authoring gains project kinds for agents, integrations, skills and knowledge, and actions inside app and agent projects; Build and Sync move in eventually |
| 22 section 3 | catalog search becomes part of knowledge search; the glossary is business knowledge (section 2.5), not a DbDataStore entity |
| 21 | by default, outbound integration steps in a non-production channel use that channel's connection or are refused; an administrator may choose otherwise, recorded (section 4.7) |
| 18 | outside systems become lineage nodes |
| 06 and `connection/1` | a kind for outside systems (if question 10 is agreed) |
| 25 section 2 | adds evaluation sets beside the behaviour tests |
| 26 | DbDataBacklog is the first app the suite ships, with its own modules (section 5.1) |
| 15 | DbDataStore gains extension models: custom attributes for any object type, keyed by the object's identity (section 5.4) |
