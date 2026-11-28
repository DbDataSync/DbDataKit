# KNOWLEDGE: knowledge, agents and actions on the four services

## Goal

Knowledge (curated and generated items, verified queries, access-filtered search) is served by the Gateway's `knowledge` module and authored in
Workspace projects. Agents run in the console (administrators and authors) and in the Gateway (end users), each over MCP or embedded, as the person
(AGENTACCESS), and in Runner tasks when unattended.
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
| KNOWLEDGE-1 | Run the other products' suites on the 2025 engines (SQL Server 2025, pgvector PostgreSQL) | none | S | the Gateway, Runner and Build conformance jobs pass on the 2025 profiles |
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
| KNOWLEDGE-13 | One shared step that makes registering a Workspace project kind a recipe (schema, check, build, publish, editor), before the agent, action and knowledge editors | none | M | a test kind registered by the recipe alone gets the editor, check, build and publish |
| KNOWLEDGE-14 | The agent project editor's scripted test conversations, the knowledge file preview of the subjects and agents it reaches, on the recipe | KNOWLEDGE-13 | M | Playwright: run a scripted conversation of an agent project; a knowledge file's preview lists the agents an instruction reaches |
| KNOWLEDGE-15 | The chat over a real model, providers and usage per channel, fallback lists, the console agent that offers the view-drafting tools | none | M | a recorded provider conversation in the console's chat shows usage and falls back to the second connection when the first fails |
| KNOWLEDGE-16 | Evaluation runs as a Runner task: an agent's evaluation set runs on a Runner with the agent's model slot bound there; the promotion check (`EvaluationGate`) asks for the score | none | M | a Runner task runs a set and records scores; a promotion below the minimum is refused naming the score |
| KNOWLEDGE-17 | Front-end agents in the Gateway's `mcp` module acting as the signed-in person, with the operations and agent data rules of AGENTACCESS-10 as their tools; an app's assistant panel; a published agent as one MCP tool | KNOWLEDGE-2, AGENTACCESS-10 | L | an MCP client calls a published agent through the module as a person and gets only what that person may read; Playwright of the assistant panel |
| KNOWLEDGE-18 | Unattended agents in integrations: the agent runtime as a library in a task's process, the agent definition a dependency of the package, its model connection a slot | none | M | a Runner task runs an agent step with a recorded provider and records the turn |
| KNOWLEDGE-19 | `action/1` and callable actions: one script format (C# or SQL) with parameters and traits, a spec like a command, published as the kind `action` with the administrator's approval of the exact version | KNOWLEDGE-13 | L | an action is published, refused unapproved, approved, and offered as a command spec |
| KNOWLEDGE-20 | The action host: declarative actions answered in Serving; code actions as a Runner run through path 1 (the caller waits 10 seconds, then gets the run's link); apps and agents call actions through it | KNOWLEDGE-19, TRUSTZONES-4 | L | an app's call of a code action returns its result within the wait or the run's link; a declarative action writes a Store row as the person |
| KNOWLEDGE-21 | Elevated actions: an action principal per channel, grants to it approved by two people, run grants to people, the declarative form (`where`, `from_state`, `to_state`, `fields`), audit and limits | KNOWLEDGE-20 | L | a person with run but no data grant changes a row only through the action; the audit names the action, its version, the principal and the person |
| KNOWLEDGE-22 | The action editor's run with a sample input, and the console's approvals and grants of actions | KNOWLEDGE-21, KNOWLEDGE-13 | M | Playwright: run an action with a sample; approve an exact version; grant run to a group |
| KNOWLEDGE-24 | The command-surface audit and the golden of MCP tools for every product, as the console has them (`ConsoleCommandSurfaceTests` over `CommandSurface.Audit` and `CommandSurface.Golden`): the Gateway, the Runner, the Workspace, Sync and Store each in its own test project, Build through its spec table, so a command that starts offering something to an agent (a secret, a write) is a line in a review; the goldens become goldens of operations with AGENTACCESS-1, and CLOUDDEV-5's tool list is generated from them | none | S | each product's test passes on its `golden/mcp-tools.txt`; a fake verb with no schema fails naming product and verb; adding a verb an agent may call changes the golden |

## Needs from other subjects

- TRUSTZONES-4: path 1, for code actions.
- AGENTACCESS-10: the Gateway's operations and agent data rules, the tools of KNOWLEDGE-17.
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

### Actions and elevated actions

Confirmed by the owner on 2026-11-03, who approves grants decided on 2026-11-04.

- **An action is one typed operation:** an input schema, an output schema, an effect class and a body; it may have side effects. Actions are agents'
  custom tools and apps' custom server-side logic. One script format, `action/1`, generalises Sync's script manifest (a name, a kind, C# or SQL, declared
  parameters, beside its code and lock file), adding the schemas, traits and the caller's context; Sync's slots stay kinds of it, and an old manifest is
  refused naming the file. A body is a `.csx` script, SQL against a suite connection, a call to a command, or, for an elevated action, the declarative
  form. A callable action has a spec like a command, so it is an MCP tool, an agent tool and an endpoint apps call, under the same access rules. Python
  for actions is left out for now.
- **Where it runs:** declarative actions in Serving, in process; code actions as a Runner run through path 1 (KNOWLEDGE-20). An action that needs an
  outside system or a long job enqueues a task and returns a status. A published version of an action is trusted code and needs the administrator's
  approval of that exact version.
- **Elevated actions: the permission is held by the action, not the person.** Some workflows let a person make one specific change to Store data they
  may not write directly. Two grants: (1) to the action, through an **action principal** of its own in the identity home **per channel** (no password or
  token a person can obtain): the entities and operations (read, create, update, change request) it may use, declared in the action's file
  (`grants:`, versioned with the code) and effective only when **an administrator and the data's owner** (the Store model's owner) both approve that
  exact version, with an audit; in the first version the grants carry entity and operation, and the field and row limits are the action's own
  declarative checks; (2) to people: **run** on the action, given to people and groups in the identity home like any role assignment, by the action's
  owner or an administrator. An action not marked elevated has no grants and runs as the person. It is not tied to an app: an app's manifest names the
  actions it calls and grants nothing. This keeps "an app's token is never wider than the person": elevation exists only inside server-side trusted
  code, the pattern of a stored procedure granted `EXECUTE`.
- **The action decides: validate, then apply or reject.** The caller's identity and attributes are part of the input; the action reads the current
  state with its own grants and checks the request (the row exists, is in the right state, the caller may make this transition, the values are
  acceptable); it rejects with a reason the person or agent can act on and changes nothing, or applies through the Store's normal write path in one
  transaction, so the model's own validation still runs after the action's checks. The declarative form makes the common checks one line each
  (`where: owner = caller.id`, `from_state: Draft`, `to_state: Submitted`, `fields: [status, comment]`); more needs a `.csx` body.
- **The four worked examples** (each a test): a requester moves their own expense claim from Draft to Submitted, and may not edit it afterwards or write
  to the claims entity at all; an approver sets the status and a comment on a claim, no other field, only on claims of their own team; a manager adds a
  new starter to an HR entity through an onboarding app without general write access to it; anyone sees how many tickets are open per team without
  being able to read the tickets.
- **Rules that keep it safe:** a person receives the action's result shaped by its output schema, never its grants, a token or rows it was not written
  to return; no escalation by default (it cannot change grants, roles, definitions or structure, or call another elevated action its definition does
  not name; an administrator can lift this for one action as a recorded choice); the audit always names the action and its version, its principal, the
  person and the app or agent it came through ("Alice ran submit-claim v3, from the Expense app"), and that is fixed, not a setting; an agent acting for
  a person may call an elevated action only if that person may run it, and elevated actions ask the person first by default; grants are per channel,
  and a write in a non-production channel goes to that channel's own data or the preview's draft layer, never production (refused with a message
  until the draft layer exists, except on a channel bound to its own copy of the model's storage); per-caller rate limits, and every rejection logged
  with its reason.
- **When not to use it:** "a person proposes, someone with the role approves" is the Store's change requests. Elevated reads of semantic models (an
  aggregate without row access) follow the same pattern later; the first version covers Store entities.

**Open questions.** Where the approval records live if access grants move to the identity home; whether grants gain field and row conditions after the
first version.

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
