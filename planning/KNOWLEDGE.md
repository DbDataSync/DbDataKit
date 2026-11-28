# KNOWLEDGE: knowledge, agents and actions on the four services

## Goal

Knowledge (curated and generated items, verified queries, access-filtered search) is served by the Gateway's `knowledge` module and authored in
Workspace projects. Agents run in the console (administrators and authors) and in the Gateway (end users), each over MCP or embedded, as the person
(AGENTTOOLS), and in Runner tasks when unattended.
Actions (one script format) are declarative in Serving and run trusted code as a Runner run through path 1. The design of what is built is in
`docs/knowledge.md`, `docs/agent-tools.md`, `docs/agents/` and `docs/integrations.md`.

## What exists today

- Built: the agent runtime and providers (`DbDataKit.Agents`), `agent/1`, `skill`, `agent-policy/1`, approvals per action, limits and cost, context
  assembly, skills, subagents, memories, checked answers, publishing of the agent kinds; the console's chat over agents whose tools only read; the install
  policy, tool, space and skill screens.
- Built: `knowledge/1`, subjects, scope matching, chunking, the embedding abstraction and the built-in ONNX model (not bindable from the console), the
  `knowledge` and `knowledge_vectors` state modules, the access filter, publishing a knowledge project, re-verification and "verify this answer" as
  libraries, the console's owner worklist and chunk preview.
- Not hosted: the Gateway has no `knowledge` module; the read, index, query and suggestion commands exist as libraries and in the console only.
- Outside-system connections (HTTP sign-in methods, OAuth 2 code, SFTP/SCP/FTPS, SMB, object storage), the integration kit, templates and the
  integration project kind are built (`docs/integrations.md`).
- Evaluation sets: the library and the console's screen; runs were meant for the console's embedded runner, which was deleted.
- Not built: actions (`action/1`), front-end agents, the knowledge app and components, the Workspace's collection of suggestions.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| KNOWLEDGE-2 | The Gateway's `knowledge` module: follows the group's knowledge definitions, stages and embeds a version per index, switches and rolls back, answers search and show as the caller with the access filter | none | L | `DbDataGateway.Tests`: a published knowledge project is searchable through the module; a person who may not read a subject finds nothing of it; a rollback serves the previous index |
| KNOWLEDGE-3 | Generated knowledge in the module: one item per published subject (semantic models and members, Store models, data sources), re-checked when a semantic model version is staged (verified queries) | KNOWLEDGE-2 | M | publishing a semantic model adds its items; a staged version that removes a member flags the verified query that uses it |
| KNOWLEDGE-4 | Build metadata as knowledge: a Workspace build publishes the `build-metadata` kind per project and connection, and the module indexes it | KNOWLEDGE-3 | M | a Workspace build's publish makes items for its models; a search finds a model by its description |
| KNOWLEDGE-5 | Descriptions everywhere: Store entities and attributes carry `description`; lineage emitters write `description` and `owner` into nodes (`LineageDescriptions`) | KNOWLEDGE-3 | M | a Store model's description appears in its knowledge item and its lineage node; `knowledge undescribed` lists one without |
| KNOWLEDGE-6 | The read, check, context and index commands, `query`, verified queries and suggestions hosted by the module and its management API (the libraries exist) | KNOWLEDGE-2 | M | each command runs against a test Gateway; a suggestion is recorded in the group's state |
| KNOWLEDGE-7 | The Workspace collects suggestions (polling the group's event log) and turns them into a change on the knowledge project's branch for review | KNOWLEDGE-6 | M | a suggestion on a Gateway appears as a commit on a review branch of the project; merging publishes it |
| KNOWLEDGE-8 | Column values as knowledge by opt-in (`knowledge: values` on a column or attribute), indexed under the column's own access | KNOWLEDGE-3 | S | a value is found by a person who may read the column and not by one with a column rule hiding it |
| KNOWLEDGE-9 | Workspace search (text and file names per repository and branch under the person's rights) and search sources (knowledge and workspace together); definitions not served by the serving group (runner packages, agent projects, skills) as items | KNOWLEDGE-6 | M | a search lists a workspace file and a knowledge item grouped by source; a runner package's description is found |
| KNOWLEDGE-10 | `@dbdatakit/knowledge-ui`: the client, Search, Subject panel, Suggest edit, Approvals, Verified badge, Verify this answer, Context view | KNOWLEDGE-6 | L | vitest per component; Playwright: search, open a subject, suggest an edit |
| KNOWLEDGE-11 | The knowledge app shipped with the suite and served by the apps module | KNOWLEDGE-10 | M | Playwright: the app searches, shows a subject's page and its verified queries |
| KNOWLEDGE-12 | The console's knowledge screens, the rest: bind the built-in embedding model per channel, index progress, chunking and embedding defaults | KNOWLEDGE-2 | M | Playwright: bind the built-in model to a channel and see the index rebuilt |

## Needs from other subjects

- TRUSTZONES-4: path 1, for code actions.
- AGENTTOOLS-10: the Gateway's operations and agent data rules, the tools of AGENTS-4.
- MASTERDATA-1: the Store change feed, for the change trigger.

## Proposed decisions

None open.

## Open designs

The tasks above are one line each; the design they build is below, as the owner decided it (2026-11-03 to 2026-11-06). What is already built of it as
libraries is in `docs/knowledge.md`. Each part becomes product documentation as its task lands.

### How knowledge is published, read and generated

**What.** A knowledge project is published like any other definition, as the kind `knowledge-project` to the serving group: one version per merged
commit, each file an artifact by its hash, signed by the Workspace, per channel. The Gateway's `knowledge` module serves it (reads only).

- **Staging and activation.** When a version is staged, the module reads its files, checks every item (format, scopes, owners) and embeds the items that
  changed (by hash; an unchanged file is not embedded again) into that channel's index. Activation switches the channel to the new items and index at
  once, so a search never sees half a version; a rollback moves back.
- **Everything reads through the module**, never the state database: the knowledge app, front-end agents, the `knowledge search` and `show` commands,
  back-end agents in the console and unattended agents in Runner tasks, each as its person or service account and under that caller's access rules.
  Knowledge is published in one place and indexed once.
- **Generated knowledge** is not published by a knowledge project: the module derives it from the other definitions published to the same group and
  channel, as their subscriber. It is derived, never edited (to change it, change the definition); references by default (a curated instruction can
  bind to the same subject when something must always be given); exactly what is served in that channel; one small item per subject (model, column,
  measure, attribute, app), so no chunking is needed and a search finds the exact thing. From a `semantic-model`: the model, each measure, attribute,
  hierarchy and saved query (a saved query is a reference, not a verified query, until someone verifies it); from `build-metadata` (Build publishes its
  metadata documents per project and connection): each model and column with its title, description, tags, columns and types, keys and what it reads
  and what reads it; from Store models: each model and field with types, validation rules and relationships; from a `data-source`: its engine and what
  it exposes; from an `app`, `agent` or `skill`: title, description, and for an app the models it uses. A generated item has role *reference*, is owned
  by the definition's owner, names the definition, version and channel, and has a stable id (`generated:semantic-model:Sales/measure:revenue`) a curated
  item can name. It is visible to whoever may read its subject (a column hidden by a column rule is hidden here too). It is rendered again when its
  definition is staged, embedded again only when its text changed, switched at activation, and retired with its definition.
- **Not indexed:** lineage as a graph (each item names its direct upstream and downstream; the full graph is the `lineage` tool) and volatile facts
  (freshness, last run, row counts), which tools return live.
- **Column values only by opt-in** (`knowledge: values` on a Build column or a semantic attribute, for small sets such as region codes), indexed under
  the column's own access rules, so nobody finds a value through knowledge they could not query.
- **Filling the gaps.** Hierarchies and Store fields gain a `description`; the console shows what is undescribed ("12 measures in Sales have no
  description"); a business user suggests a description from the knowledge app, and when the definition's project is in the Workspace the suggestion
  becomes a change to that project approved by its owner.
- **The catalog is this:** search over generated knowledge, with the lineage tool beside it.

**Open questions.** Which kinds are rendered first after semantic models and `build-metadata` (entity models, data sources, apps are not rendered
today).

### Verified queries, as agents and people use them

A verified query is a question people ask, the query that answers it and someone's word that the answer is right (the kind, its index table,
`query run-verified` and the re-check when a model changes are built as libraries). The rest of its design:

- **The JSON request is the standard form**: what is stored, run and compared; semantic SQL is accepted as input, translated, and kept beside it only for
  reading. The query is **pinned** into the item when verified (a person's saved query can change or go), or names a model-level saved query by version.
  What is verified is the query's logic, not its numbers: run by different people it applies each one's rules.
- **Made through the suggestion path:** "Verify this answer" wherever a query and its result are shown (the knowledge app, the console, an agent's
  conversation), giving the question as the person would ask it, with the request, its parameters, the model version and a preview of the result; from
  an agent only after a person confirms its answer; or by hand in a workspace.
- **Used by agents:** found by the question (the `questions:` are what is embedded); given as a verified example, marked with who verified it and when;
  run directly (`query run-verified <id>` with parameters) or adapted, the turn recording which one it used; an answer from a verified query says so to
  the person ("verified by Maria, 6 November"), an adapted one says that instead.
- **They are the front-end agents' evaluation cases:** the question goes to the agent, the agent's query and the verified query both run as the same
  service account against the same data, and their rows are compared; no judge model is needed, and the set grows as people verify answers.
- **When the model changes**, one that fails the re-check is marked "needs re-verification": still found, no longer given as verified, its owner told;
  a model change is never blocked by its verified queries.

### The knowledge app and `@dbdatakit/knowledge-ui`

- **The app**, served by the Gateway's apps module, for everyone who may see knowledge: (1) search, one box, results with excerpt, title, heading path
  and source, filtered by kind (instruction, guardrail, reference, verified query, generated), subject and owner; (2) a subject's page: its generated
  description, what is scoped to it, its verified queries, what is undescribed, with Suggest a description; (3) an item's page: text and fields, the
  channels it is in, its history with a comparison of any two versions, Suggest a change; (4) the form for a new item or a change, the `knowledge/1`
  fields as fields and the text in a Markdown editor with a preview; (5) My suggestions and, for owners, Approvals: each suggestion beside the current
  text, the agents it reaches and any budget it would exceed, approve or reject with a comment, and the ones that need another look; (6) verified
  queries per model, the ones needing re-verification, and Verify this answer.
- **The console adds** for administrators and developers: the index per channel (embedding model, counts, rebuild progress, failed items, orphans),
  an agent's context (`knowledge context`), and knowledge files checked as they are typed in workspaces with the subjects each reaches.
- **The components**, a shared web package beside `@dbdatakit/shell`, `visuals` and `store-ui`, used by the app, the console, agents' chat and
  assistant panels and any app: Search (grouped results, filters, several sources at once), Item view, Subject panel (for embedding beside a subject in
  any screen), Suggest edit, Approvals, Verified badge and Verify this answer, Context view. One typed client generated from the command specs; they
  work inside sandboxed apps (calls through the shell's token broker, as the person) and are themed by the shell's variables, so a portal's branding
  applies.

### What is searchable, and one search box over several sources

The knowledge index holds what describes the system; content is searched where it is kept, by that system's own rules. Definitions of every kind
(semantic models and members, Build models and columns, Store models, data sources, apps, portals, agents, skills, saved queries, reports and
dashboards, integrations) are generated knowledge. Packages not served by the serving group (Runner packages, integrations) still get an item: when the
control service deploys one it also publishes its description (name, title, what it reads and writes, owner; never its code) to the group. Source code and
files are not in the index (they change per branch, people search them for exact text, and their access is the project's): workspace search on the
Workspace, exact text and file names per repository and branch under the person's rights. Data, documents' contents and operational records (runs,
notifications, audit, suggestions, work items) stay with their own systems and screens. The search component asks several search sources at once and
groups the results: knowledge always; workspace search in the console for people with rights to projects; Store records in an app that has them. Each
source applies its own access rules; nothing is copied between them.

### A back-end agent reads its branch's knowledge

An agent working on a branch of a Workspace project reads that branch's `knowledge/` files laid over the published ones, so an instruction it is
editing applies to its own work before it is merged; the published item is unchanged, and nothing remains after the branch is abandoned. **Why:**
otherwise an agent editing an instruction cannot see its effect until a merge and a publish. **Open questions:** whether the Workspace serves the overlay
to the console's agent runtime or the runtime reads the branch itself; how the overlay's items are checked (the same `knowledge check`) and whether a
broken one falls back to the published item. Proven by a scripted run: the agent edits an instruction and its next turn is given the edited text.

## Shared areas touched

- `DbDataKit.Knowledge*`, `DbDataKit.Agents`, a new `DbDataKit.Actions`, `DbDataKit.Authoring.Server` (kind recipe), `DbDataKit.Lineage`
  (descriptions), `DbDataKit.Runner.Kit`, `Shared/web/*` (knowledge-ui, workspace, console).
- `docs/formats.json` (`action/1`), `docs/ui-editors.md`, `docs/knowledge.md`, `docs/agent-tools.md`, `docs/integrations.md`.
