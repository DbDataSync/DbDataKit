# AGENTACCESS: agents drive the console and the Gateway, over MCP or embedded, as the person

## Goal

An agent can do what a person does, in the two places a person works, by either of two connections:

| | **External agent over MCP** (the model runs outside the product) | **Embedded agent** (the loop runs in the product, the model is a provider's API) |
| --- | --- | --- |
| **The console** (administrators and authors: configure the install, author, build and deploy every feature: apps, semantic and analytics models, master data models, sync definitions, and the rest) | the console's MCP endpoint | the console's chat |
| **The Gateway** (end users of an app or a data model) | the `mcp` module | front-end agents and an app's assistant panel |

In every case the agent acts **as the person** who connected it, with that person's roles on each server, through the same approvals. Each place offers
the **same operations** to its MCP clients and to its embedded agent, and each operation is **one implementation** that the web API, the command line,
MCP and the embedded agent all call. What an operation returns to an agent is shaped by **agent data rules** set per channel, per model provider and per
MCP client (an MCP client is a model provider whose model runs outside the product): passed, masked, replaced with sample data, or refused.

## What exists today

- **The command surface** (`Shared/src/DbDataKit.Cli`): a `Noun`/`Verb` per command with a spec, a JSON Schema for its output, an effect class and
  traits. `ui mcp` (stdio) and the embedded agent's tools (`CommandTools.From`) are generated from it. These are command-line verbs, run in process.
- **Web controllers do not use the command surface.** They call host services (`KnowledgeController` → `ConsoleKnowledge`, `BacklogController` →
  `ConsoleBacklog`), which some verbs also call; an operation's input schema, permission check and approval rule are written separately for the API and for
  the command.
- **The console's embedded agent** (`ConsoleAgents`, `AgentsController`): tools are the console's own offline verbs that only read (7, in
  `DbDataKit/tests/DbDataKit.Server.Tests/golden/mcp-tools.txt`), run with **the console's authority, not the person's**; writes, `user`, `instance`,
  `server`, `runner`, `secret` are left out. `build-helper` (as the person, through the relay) is dead since the Build server went (AUTHORING-8).
- **The console has no MCP endpoint.** Its outside-agent token (`AgentTokenCredentials`) reaches only `/api/agents/outside/`, where a local `ui mcp
  --report-to` records what it asked and ran.
- **The Gateway's `mcp` module** (`POST /mcp`, `docs/serving-layer.md`, "The mcp module"): as the signed-in person by an identity-home token; tools call the
  serving services the other modules use (`IServingServices`, `GroupStoreApi`), so per-person access rules and column masks apply; changes wait for a
  person's approval.
- **Model providers** (`DbDataKit.Agents.Providers`, connection kind `model-provider`, `models/_slots.yml`); the agent loop, approvals by traits
  (`PromptingResolver`: the strictest of three layers), limits, context.
- **Data rules for agents**: the trait `reads-data`, `McpOptions.RefuseData` (drops every tool that reads data) and one install policy rule
  (`data-rules: cloud-providers: { reads-data: refuse }`). No masking or sample data for agents; nothing per channel or per MCP client.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AGENTACCESS-1 | The operation, as AGENTACCESS-P1 says: one definition (name, summary, input and output schemas, traits, the role it needs, its handler) and four adapters: a web endpoint, a command-line verb, an MCP tool, an agent tool. The existing command surface keeps its tests (adapter first) | AGENTACCESS-P1 | L | a test operation served by all four adapters gives the same result, the same refusal for a missing role and the same approval prompt |
| AGENTACCESS-2 | The caller and the pipeline every adapter goes through: who (the person), how (web, command line, MCP client, embedded agent), the model provider or MCP client, the channel; then the role check, the approval gate (`PromptingResolver`), the agent data rules (AGENTACCESS-3), the audit | AGENTACCESS-1 | M | the same operation called by a person and by an agent is audited with both, and only the agent's call is shaped by the data rules |
| AGENTACCESS-3 | Agent data rules: set on a channel, a model provider definition and an MCP client, combined as AGENTACCESS-P2 says; per trait and per column classification: pass, mask, sample (AGENTACCESS-P3), refuse; replaces `data-rules: cloud-providers` with an adapter that reads the old form | AGENTACCESS-2, AGENTACCESS-P2 | M | a table read through an agent in `prod` with a cloud provider is masked, in `dev` is passed, and with a refusing MCP client is refused naming the rule; the old policy form still reads |
| AGENTACCESS-4 | MCP clients as model provider definitions of an external kind: registered by a person (name, owner, the data rules it carries), issued a token as the person; the console's outside-agent token becomes one of them | AGENTACCESS-3 | M | a registered client signs in with its token as its person; revoking it ends its calls; the old token keeps working through an adapter |
| AGENTACCESS-5 | The console's MCP endpoint, as AGENTACCESS-P4 says: the console's operations for the person, through the relay to the Workspace, Runner and Gateway with the person's roles there | AGENTACCESS-4 | M | an MCP test lists the tools, reads as the person, and a change waits for the person's approval and runs once |
| AGENTACCESS-6 | The console's embedded agent on operations as the person, replacing the console-authority tools (AGENTACCESS-P5); the same tools as AGENTACCESS-5 | AGENTACCESS-5, AGENTACCESS-P5 | M | `ConsoleAgentsTests`: the chat offers the same tools as the MCP endpoint for the same person; a viewer's agent cannot change anything |
| AGENTACCESS-7 | Console operations, area by area (one task branch each): users and invitations, servers, connections and secrets references, delivery, releases | AGENTACCESS-1 | L | each area's controller calls its operations and its tests pass unchanged; each operation is in the console's golden of MCP tools |
| AGENTACCESS-8 | Workspace operations through the console: create, edit, check, build, preview and publish a project of every kind; absorbs AUTHORING-11 and AUTHORING-8 | AGENTACCESS-5 | L | an MCP test authors, builds and publishes a build, a sync and a store project as a person; each change waits for an approval; a denied step changes nothing |
| AGENTACCESS-9 | Runner and Gateway configuration through the console: packages, channels, runs; data sources, apps, semantic models | AGENTACCESS-5 | L | an MCP test publishes a package to `dev` and runs it; adds a data source; each as the person |
| AGENTACCESS-10 | The Gateway's `mcp` module tools as operations, with the agent data rules; the operations the end user's app calls offered as tools (front end) | AGENTACCESS-3 | M | the module's tests pass unchanged; a masked column through MCP is masked as the rules say for that client |
| AGENTACCESS-11 | Screens: the agent data rules of a channel, a model provider and an MCP client; MCP client registration and tokens (`docs/ui-editors.md` rows) | AGENTACCESS-3, AGENTACCESS-4 | M | Playwright: register a client, set its rule to mask, see a masked answer in its record |
| AGENTACCESS-12 | Docs: `docs/agent-tools.md` rewritten around the four cases, operations and data rules; `docs/architecture.md` says Control hosts the administrators' and authors' agent tools | AGENTACCESS-6 | S | `check_doc_inputs.py` passes |

## Needs from other subjects

- KNOWLEDGE-17 (front-end embedded agents in the Gateway) uses the operations and data rules of AGENTACCESS-10 instead of its own tool set.
- KNOWLEDGE-24 (a golden of MCP tools per product) becomes the golden of operations per product, from AGENTACCESS-1.
- AUTHORING-8 and AUTHORING-11 are absorbed by AGENTACCESS-8 (a proposal to the owner before either leaves AUTHORING).
- KNOWLEDGE's goal ("agents run in the console for back-end work, in the Gateway's `mcp` module for front-end work") is reworded to the four cases.

## Proposed decisions

### AGENTACCESS-P1: what an operation is (awaiting the owner)

Scenario: "invite a user" is today a controller action and a verb, each with its own checks. Option A: extend the command surface (`Verb` gains an input
schema, the role it needs and a handler that takes typed input), and the web adapter maps a route to it. Option B: a new library of operations beside
the command surface, with the command line as one more adapter. Recommendation: A, so `ui mcp`, the traits, the effect classes and the goldens stay one
system.

### AGENTACCESS-P2: how the channel, the provider and the MCP client combine (awaiting the owner)

Scenario: `prod` says mask customer emails, the provider (hosted in your own tenant) says pass, the MCP client says nothing. Option A: the strictest wins
(as approvals do: `PromptingResolver`). Option B: the most specific wins (client, then provider, then channel). Recommendation: A; a wider rule is
loosened only by changing that rule, which the record shows.

### AGENTACCESS-P3: where sample data comes from (awaiting the owner)

Option A: generated per column from its type and classification (fake emails, shuffled values, ranges). Option B: a sample set kept with the model or
the project, per channel (the channel's test data). Option C: A by default, B where one is published. Recommendation: C.

### AGENTACCESS-P4: the console's MCP transport and sign-in (awaiting the owner)

Option A: streamable HTTP on the console (`POST /mcp`, like the Gateway's module), signed in with the MCP client's token (AGENTACCESS-4). Option B: stdio
only (`dbdatakit ui mcp`) on the console's host. Recommendation: A; administrators and authors connect from their own machines.

### AGENTACCESS-P5: the console's embedded agent changes authority (awaiting the owner)

Today it reads with the console's authority. As the person, a viewer's agent sees less than today's agent. Option A: switch, and the old agent
definitions keep working with the person's narrower reach. Option B: keep a console-authority agent kind for administrators. Recommendation: A.

## Shared areas touched

`Shared/src/DbDataKit.Cli` (the operation), `Shared/src/DbDataKit.Agents` (data rules, MCP clients), `Shared/src/DbDataKit.Web` (the web adapter), the
console's controllers, the Gateway's `mcp` module, `docs/formats.json` (the data rules' format), `docs/ui-editors.md`, the goldens of MCP tools.
