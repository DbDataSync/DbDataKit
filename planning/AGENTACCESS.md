# AGENTACCESS: agents drive the console and the Gateway, over MCP or embedded, as the person

## Goal

An agent can do what a person does, in the two places a person works, by either of two connections:

| | **External agent over MCP** (the model runs outside the product) | **Embedded agent** (the loop runs in the product, the model is a provider's API) |
| --- | --- | --- |
| **The console** (administrators and authors: configure the install, author, build and deploy every feature: apps, semantic and analytics models, master data models, sync definitions, and the rest) | the console's MCP endpoint | the console's chat |
| **The Gateway** (end users of an app or a data model) | the `mcp` module | front-end agents and an app's assistant panel |

In every case the agent acts **as the person** who connected it, with that person's roles on each server, through the same approvals. Each place offers
the **same operations** to its MCP clients and to its embedded agent, and each operation is **one implementation** that the web API, the command line,
MCP and the embedded agent all call. What an agent may do and see is narrowed by **agent data rules** set per channel, per model provider and per
MCP client (an MCP client is a model provider whose model runs outside the product). The first version allows or refuses the operations that read data;
masking, sample data and rules of other kinds come with the permissions model (Open designs), and the first version meets the requirements that
keep that model from being a rework (R1 to R8).

## What exists today

- **The command surface** (`Shared/src/DbDataKit.Cli`): a `Noun`/`Verb` per command with a spec, a JSON Schema for its output, an effect class and
  traits. `ui mcp` (stdio) and the embedded agent's tools (`CommandTools.From`) are generated from it. These are command-line verbs, run in process.
- **Web controllers do not use the command surface.** They call host services (`KnowledgeController` → `ConsoleKnowledge`, `BacklogController` →
  `ConsoleBacklog`), which some verbs also call; an operation's input schema, permission check and approval rule are written separately for the API and for
  the command.
- **The console's embedded agent** (`ConsoleAgents`, `AgentsController`): tools are the console's own offline verbs that only read (7, in
  `DbDataKit/tests/DbDataKit.Server.Tests/golden/mcp-tools.txt`), run with **the console's authority, not the person's**; writes, `user`, `instance`,
  `server`, `runner`, `secret` are left out. `build-helper` (as the person, through the relay) is dead since the Build server went (AGENTACCESS-8).
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
| AGENTACCESS-1 | The operation, a new type at the center (`Shared/src/DbDataKit.*`): a stable name, summary, a typed input and its schema, a closed output schema, the effect class and traits that exist today, the role it needs by name, the rule kinds it accepts, and a handler that takes the caller; meets R1, R3, R4 (below). Four adapters are generated from it: a web endpoint, an MCP tool, an agent tool, and a command-line verb that parses its arguments into the typed input. Effect classes, traits, `DataSchemas` and the goldens move onto the operation unchanged; existing verbs stay as they are until their area moves, and verbs only a command line needs (`server run`, `service install`) stay plain verbs | none | L | a test operation served by all four adapters gives the same result, the same refusal for a missing role and the same approval prompt; an operation that checks a role itself, outside the decision point, fails a test naming it |
| AGENTACCESS-2 | The caller and the pipeline every adapter goes through, meeting R1, R2, R6 (below): the caller (the person, their roles and groups by name, the scope, the channel, how they connected, the model provider or MCP client), then the decision point (the role check, the agent data rules of AGENTACCESS-3, the approval gate `PromptingResolver`), the handler, the result stage, the audit | AGENTACCESS-1 | M | the same operation called by a person and by an agent is audited with the whole caller for both, and only the agent's call is narrowed by the data rules; a test result stage that changes one field of a closed output schema is applied by every adapter |
| AGENTACCESS-3 | Agent data rules, the first version: one setting on a channel, a model provider definition and an MCP client, `allow` or `refuse`, for the operations whose traits say they read data; combined with the strictest winning; one stage of the pipeline that only narrows what the person may already do, meeting R5, R7, R8 (below), so the permissions model (Open designs) replaces where the stage reads its rules, not the operations or the adapters; replaces `data-rules: cloud-providers` with an adapter that reads the old form. Masking and sample data wait for that model | AGENTACCESS-2 | S | an agent's read in `prod` through a refusing provider is refused naming the rule and where it was set; in `dev` it runs; a person's own call is never affected; the old policy form still reads |
| AGENTACCESS-4 | MCP clients as model provider definitions of an external kind: registered by a person (name, owner, the data rules it carries), issued a token as the person; the console's outside-agent token becomes one of them | AGENTACCESS-3 | M | a registered client signs in with its token as its person; revoking it ends its calls; the old token keeps working through an adapter |
| AGENTACCESS-5 | The console's MCP endpoint over streamable HTTP (`POST /mcp`, as the Gateway's module), signed in with a registered MCP client's token: the console's operations for the person, through the relay to the Workspace, Runner and Gateway with the person's roles there | AGENTACCESS-4 | M | an MCP test lists the tools, reads as the person, and a change waits for the person's approval and runs once |
| AGENTACCESS-6 | The console's embedded agent on operations as the person, replacing the console-authority tools (no console-authority kind is kept; existing agent definitions keep working with the person's reach); the same tools as AGENTACCESS-5 | AGENTACCESS-5 | M | `ConsoleAgentsTests`: the chat offers the same tools as the MCP endpoint for the same person; a viewer's agent cannot change anything |
| AGENTACCESS-7 | Console operations, area by area (one task branch each): users and invitations, servers, connections and secrets references, delivery, releases | AGENTACCESS-1 | L | each area's controller calls its operations and its tests pass unchanged; each operation is in the console's golden of MCP tools |
| AGENTACCESS-8 | Workspace operations through the console: create, edit, check, build, preview and publish a project of every kind; the build-helper agent on a Workspace build project (`InstallBuildHelperAsync` through the Workspace's relay; the relay to the deleted Build server goes) | AGENTACCESS-5 | L | an MCP test authors, builds and publishes a build, a sync and a store project as a person; each change waits for an approval; a denied step changes nothing; `ConsoleAgentsTests`: the helper works on a Workspace build project and no code path names a Build server |
| AGENTACCESS-9 | Runner and Gateway configuration through the console: packages, channels, runs; data sources, apps, semantic models | AGENTACCESS-5 | L | an MCP test publishes a package to `dev` and runs it; adds a data source; each as the person |
| AGENTACCESS-10 | The Gateway's `mcp` module tools as operations, with the agent data rules; the operations the end user's app calls offered as tools (front end) | AGENTACCESS-3 | M | the module's tests pass unchanged; a read through a client whose rule refuses data is refused naming the rule; the person's column masks still apply |
| AGENTACCESS-11 | Screens: the agent data rule of a channel, a model provider and an MCP client; MCP client registration and tokens (`docs/ui-editors.md` rows) | AGENTACCESS-3, AGENTACCESS-4 | M | Playwright: register a client, set its rule to refuse, see the refusal in its record |
| AGENTACCESS-13 | Remove `ui mcp` from every product (Build's own MCP server included) once the console's and the Gateway's MCP endpoints serve operations; with it `--ask`, `--report-to` and the console's `/api/agents/outside/` and the adapter of AGENTACCESS-4 for the old token. The commands answer with the sentence naming the console's and the Gateway's MCP; voids `docs/agent-tools.md` ("Model Context Protocol: `ui mcp`", "Recording and asking in the console"), `DbDataBuild/docs/agents.md` (its MCP server), `McpServerTests`, `McpApprovalTests`, `McpReportTests` and Build's `McpServerTests`, each updated or deleted in the same pull request | AGENTACCESS-5, AGENTACCESS-10 | M | `RemovedCommands` tests: `ui mcp` in each product answers naming the replacement; no code path starts a stdio MCP server; the goldens are of operations |
| AGENTACCESS-12 | Docs: `docs/agent-tools.md` rewritten around the four cases, operations and data rules; `docs/architecture.md` says Control hosts the administrators' and authors' agent tools | AGENTACCESS-6 | S | `check_doc_inputs.py` passes |

## Needs from other subjects

- None. KNOWLEDGE-17 builds on AGENTACCESS-10 and KNOWLEDGE-24's goldens become goldens of operations with AGENTACCESS-1 (both recorded in
  KNOWLEDGE); AUTHORING's tasks for the build-helper and for agents authoring through MCP moved into AGENTACCESS-8.

## Requirements for the permissions model

The full model of roles and permissions is later (Open designs). The first version meets these, so that model is an addition, not a rework:

- **R1 One decision point.** Every adapter calls the same pipeline, and only its decision point decides whether a caller may run an operation. No
  operation, controller or adapter checks a role on its own.
- **R2 The whole caller, every time.** The decision point and the audit get the person, their roles and groups, the scope (server, project or app), the
  channel, how they connected (web, command line, MCP client, embedded agent) and the model provider or MCP client. A condition added later reads what is
  already there.
- **R3 Roles are names.** The decision point compares role names; the five server roles (Viewer, Operator, Author, Approver, Admin) are names to it, so a
  role an application or a person defines later needs no change to an operation or an adapter.
- **R4 An operation says which rules it accepts.** Its definition lists the rule kinds it takes beyond the generic ones (allow, refuse, ask); the list is
  empty in the first version. A rule of a kind the operation does not accept is refused when it is saved, naming both.
- **R5 Rules only narrow.** Sources combine with the strictest winning; an agent's rule never gives more than the person has, and the
  person's own data rules (a model's `access:` section) still apply underneath.
- **R6 A place to shape results.** The pipeline has a stage after the handler that gets the result with its closed output schema; the first version
  only lets it through, so masking and sample data are a stage's work later, not a change to every operation.
- **R7 One stored format with room for kinds.** The rules are data in one versioned format (an entry in `docs/formats.json`, open), each rule naming its
  kind, its source (channel, model provider or MCP client) and its operation or trait; a new kind is an addition to the format, not a new one.
- **R8 Every decision is explained.** A refusal names the rule, its kind and where it was set, and the audit records the same.

## Proposed decisions

### AGENTACCESS-P1: what an operation is (awaiting the owner)

Scenario: "invite a user" is today a controller action and a verb, each with its own checks. Option A: extend the command surface (`Verb` gains an input
schema, the role it needs and a handler that takes typed input), and the web adapter maps a route to it. Option B: a new library of operations beside
the command surface, with the command line as one more adapter. Recommendation: A, so `ui mcp`, the traits, the effect classes and the goldens stay one
system. Revised recommendation (2026-11-19, after reading `Verb`: its handler is `Run(string[] args, CliContext)`, so under A every web and agent call
would be turned into argument strings or each verb would get a second entry point): B, sharing the existing metadata, so an operation is what the
product does and a command is one way to call it.

Answer from the owner, 2026-11-19: "Yes, B. We've moved so far away from the old CLI programs that I'm also not worried about keeping the "ui mcp"
command. If it is ever creating extra maintenance or causing weird architecture choices, get rid of it. That goes for any of the CLI programs in
general as well." Acted on in AGENTACCESS-1 and AGENTACCESS-13; the general rule is written into `docs/architecture.md` (D3, amended).

### AGENTACCESS-P2: how the channel, the provider and the MCP client combine (awaiting the owner)

Scenario: `prod` says mask customer emails, the provider (hosted in your own tenant) says pass, the MCP client says nothing. Option A: the strictest wins
(as approvals do: `PromptingResolver`). Option B: the most specific wins (client, then provider, then channel). Recommendation: A; a wider rule is
loosened only by changing that rule, which the record shows.

Answer from the owner, 2026-11-19: "Strictest wins". Acted on in AGENTACCESS-3 and R5.

### AGENTACCESS-P4: the console's MCP transport and sign-in (awaiting the owner)

Option A: streamable HTTP on the console (`POST /mcp`, like the Gateway's module), signed in with the MCP client's token (AGENTACCESS-4). Option B: stdio
only (`dbdatakit ui mcp`) on the console's host. Recommendation: A; administrators and authors connect from their own machines.

Answer from the owner, 2026-11-19: "HTTP on the console". Acted on in AGENTACCESS-5.

### AGENTACCESS-P5: the console's embedded agent changes authority (awaiting the owner)

Today it reads with the console's authority. As the person, a viewer's agent sees less than today's agent. Option A: switch, and the old agent
definitions keep working with the person's narrower reach. Option B: keep a console-authority agent kind for administrators. Recommendation: A.

Answer from the owner, 2026-11-19: "Switch to the person". Acted on in AGENTACCESS-6.

## Open designs

### A permissions model for people and agents, per environment and per model provider

**Later**: the owner wants the rest of the suite running first (2026-11-19); the first version meets R1 to R8 above so this needs no rework.

**What.** One model of who may do what, which both people and agents go through: **roles**, defined by the application (the five server roles today:
Viewer, Operator, Author, Approver, Admin; an app's own roles) and by the install's users (a role an administrator or an app's owner makes); **permissions**
that a role grants on operations; and **conditions** under which a permission holds or changes: the environment (the channel), and, for an agent, the
model provider or MCP client it runs through. Example: the role `support-analyst` may read `customers` in every channel; in `prod`, through a cloud
provider, its agent sees `email` masked and `notes` replaced with sample text; through the tenant's own model it sees both; a person in that role sees
both everywhere.

**Why.** Rules differ by operation: a read has column treatments (hide, null, mask, sample), a write has limits and approvals, a publish has targets
and channels, an action has its own parameters. A fixed list of rule kinds would either be too narrow for some operations or too loose for others. So
each operation declares the **kinds of rule it accepts** and their parameters (beside its schemas and traits), and the model checks a rule against the
operation it names; generic rules (allow, refuse, ask) apply to every operation.

**What it builds on and must stay consistent with.** The roles and assignments of the identity home (`Shared/src/DbDataKit.Identity`: roles on a server,
a project or an app, groups, external group mappings); the row and column rules of a model's `access:` section (`docs/serving-layer.md`, "Column rules":
hide, null, mask, by person and group); the prompting settings of `agent-policy/1` (the strictest of three layers); the first agent data rules of
AGENTACCESS-3, which become one condition kind of this model; elevated actions (KNOWLEDGE's design: a permission held by the action, not the person).

**Open questions.**

- Where user-defined roles live: the identity home (one place for every server and app) or each server.
- Whether a model's `access:` rules and these permissions are one format, or the model's rules stay and are referred to.
- Sample data as a result treatment: the data itself comes from SAMPLEDATA.
- Whether columns get a classification (personal, financial, free text) that rules can name instead of naming columns one by one.
- How a person sees what their agent will be allowed in each channel before running it (the console's "what each tool will ask" page, extended).

## Shared areas touched

`Shared/src/DbDataKit.Cli` (the operation), `Shared/src/DbDataKit.Agents` (data rules, MCP clients), `Shared/src/DbDataKit.Web` (the web adapter), the
console's controllers, the Gateway's `mcp` module, `docs/formats.json` (the data rules' format), `docs/ui-editors.md`, the goldens of MCP tools.
