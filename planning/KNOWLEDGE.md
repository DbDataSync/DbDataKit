# KNOWLEDGE: knowledge, agents and actions on the four services

## Goal

Knowledge (curated and generated items, verified queries, access-filtered search) is served by the Gateway's `knowledge` module and authored in
Workspace projects. Agents run in the console for back-end work, in the Gateway's `mcp` module for front-end work and in Runner tasks when unattended.
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
| KNOWLEDGE-17 | Front-end agents in the Gateway's `mcp` module acting as the signed-in person; an app's assistant panel; a published agent as one MCP tool | KNOWLEDGE-2 | L | an MCP client calls a published agent through the module as a person and gets only what that person may read; Playwright of the assistant panel |
| KNOWLEDGE-18 | Unattended agents in integrations: the agent runtime as a library in a task's process, the agent definition a dependency of the package, its model connection a slot | none | M | a Runner task runs an agent step with a recorded provider and records the turn |
| KNOWLEDGE-19 | `action/1` and callable actions: one script format (C# or SQL) with parameters and traits, a spec like a command, published as the kind `action` with the administrator's approval of the exact version | KNOWLEDGE-13 | L | an action is published, refused unapproved, approved, and offered as a command spec |
| KNOWLEDGE-20 | The action host: declarative actions answered in Serving; code actions as a Runner run through path 1 (the caller waits 10 seconds, then gets the run's link); apps and agents call actions through it | KNOWLEDGE-19, TRUSTZONES-4 | L | an app's call of a code action returns its result within the wait or the run's link; a declarative action writes a Store row as the person |
| KNOWLEDGE-21 | Elevated actions: an action principal per channel, grants to it approved by two people, run grants to people, the declarative form (`where`, `from_state`, `to_state`, `fields`), audit and limits | KNOWLEDGE-20 | L | a person with run but no data grant changes a row only through the action; the audit names the action, its version, the principal and the person |
| KNOWLEDGE-22 | The action editor's run with a sample input, and the console's approvals and grants of actions | KNOWLEDGE-21, KNOWLEDGE-13 | M | Playwright: run an action with a sample; approve an exact version; grant run to a group |
| KNOWLEDGE-23 | Integration remainders: outbound steps in a non-production channel use the channel's connection or are refused (the rest), the console's connection form per kind with Test, the Store change trigger | MASTERDATA-1 | M | an outbound step in `test` with no binding is refused naming the slot; a Store change starts a task once; Playwright of the SFTP form's Test |

## Needs from other subjects

- TRUSTZONES-4: path 1, for code actions.
- MASTERDATA-1: the Store change feed, for the change trigger.

## Proposed decisions

None open.

## Shared areas touched

- `DbDataKit.Knowledge*`, `DbDataKit.Agents`, a new `DbDataKit.Actions`, `DbDataKit.Authoring.Server` (kind recipe), `DbDataKit.Lineage`
  (descriptions), `DbDataKit.Runner.Kit`, `Shared/web/*` (knowledge-ui, workspace, console).
- `docs/formats.json` (`action/1`), `docs/ui-editors.md`, `docs/knowledge.md`, `docs/agent-tools.md`, `docs/integrations.md`.
