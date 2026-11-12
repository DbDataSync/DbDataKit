# Agents and the suite: what exists today

An AI agent can drive the suite today **from the outside**, through the commands each product already has. There is **a first agent inside the suite**: the console runs back-end agents (below); there is no model provider yet, no front-end agent and no `mcp` module in the serving layer. [Plan 27](plans/27-knowledge-agents-integrations-work.md) decides what the embedded agents will be; [plan 27a](plans/27a-knowledge-slices.md) slices it and says, row by row, what is built. This page is the built half: what an agent can use now, and where each piece is described.

## The commands are the tools

Every product with a command line (`DbDataKit.Cli`: nouns and verbs) gives each verb three things: a **spec** (name, summary, usage), an **effect class** and a **JSON Schema** for the `data` of its document. A router refuses to be built when a verb has no schema (`Noun.cs`), so a command an agent can run is a command with a declared shape. `--format json` prints exactly one document per command; the same document is what an agent receives.

The effect class is the line between "reads" and "changes" (`CommandSpec.cs`): offline only, repository files only, target read-only, target writes, target data writes, tracking tables only. It is printed in `--help` and in the header of each run.

**Traits** (`Traits.cs`, plan 27a AG02) say more: reads definitions, reads data, writes definitions, writes data, changes structure, deletes, outward, elevated. Each effect class maps to traits by a fixed table (`TraitTable.For`); a verb may add traits (a verb that sends mail is `outward`) and may not drop one its effect class implies, or the router refuses it when it is made. The MCP server decides what to offer from traits, not from the effect class: a verb with any changing trait is offered only with `--allow-writes`, a policy that keeps data from a model can leave out every verb that `reads data` (`McpOptions.RefuseData`), and each tool's `annotations.traits` lists them.

## Model Context Protocol: `ui mcp`

`dbdatasync ui mcp` and `dbdatagateway ui mcp` start a stdio MCP server over the product's own command router (`DbDataKit.Cli/Mcp.cs`: newline-delimited JSON-RPC, protocol `2025-06-18` and the two before it). Every offered command is a tool named `noun_verb`; a call runs the command in process with `--format json` and returns its document (cut at 120,000 characters).

- **Only commands that read are offered.** A command whose effect class is anything but offline-only or target read-only is left out unless the operator starts the server with `--allow-writes`.
- **Some commands are never offered**, whatever the flag says (each product's `NotForAgents` list): the Gateway keeps out `secret`, `query` (it returns rows), pairing, `datasource adopt`, `identity` and `apps`; Sync keeps out `secret set`, `secret remove`, `secret send`, `library validate` and pairing. `ui` itself and `server run` and `server setup` are excluded everywhere.
- **The Gateway offers the `semantic` noun** (`describe`, `query`, `explain`, `members`) so an agent can read a published semantic model and ask it questions as the person whose token it holds; see [semantic/architecture.md](semantic/architecture.md).
- **Tests:** `DbDataKit.Cli.Tests/McpServerTests.cs`.

DbDataBuild has its own MCP server (`dbdatabuild ui mcp`: tools, plus the JSON Schemas, the support matrix and the explanation of every diagnostic code as resources, and the workflows of its skill as prompts). It is described in [DbDataBuild/docs/agents.md](../DbDataBuild/docs/agents.md) and [DbDataBuild/docs/interfaces.md](../DbDataBuild/docs/interfaces.md); tests are `AgentKitTests.cs` and `McpServerTests.cs` in `DbDataBuild.Tests.Unit`.

**Approvals through MCP** (`ui mcp --ask`, plan 22a GP36): with `--ask` the commands that change something are offered too, and each call waits for the person. The server asks through the client (MCP elicitation, protocol 2025-06-18); only the client's own answer counts, and a client that cannot ask is refused any change. The default setting follows the traits: reads do not ask, writes of definitions or files ask once per tool, writes of data, structure changes, deletes, outward and elevated steps ask every time. A denied step ends the run. Every ask, approval, denial, refusal and run is a JSON line on standard error naming the person, the agent (the client's name and version), the tool and its arguments. `--refuse-data` leaves out the commands that read data. Tests: `DbDataKit.Cli.Tests/McpApprovalTests.cs`.

**Recording and asking in the console** (`ui mcp --report-to <console address>`, plan 27a AG29; every product's `ui mcp`, Build's own server included): the server signs in to the console as the person with a token and posts what the gate asked, what the person answered and what ran, so the console's Agents page (Approvals and Record) shows outside agents' sessions. The token is made on that page ("Connect an outside agent": shown once, revocable, acts as the person, reaches only `/api/agents/outside/`) and given to the server in `DBDATAKIT_CONSOLE_USER` (the account id the page shows) and `DBDATAKIT_CONSOLE_TOKEN`, never on the command line. With `--ask`, a client that cannot ask the person (no elicitation) asks in the console's inbox and the step waits for the person's own answer there; a console that cannot be reached means the step is not run. Tests: `McpReportTests.cs`, `ConsoleAgentsTests.cs`.

## Telling an agent how to work: Build's agent kit

`dbdatabuild project agent-kit --write` installs a skill (`.claude/skills/dbdatabuild/SKILL.md`, a name and a description in its header, then the rules) and the JSON Schemas beside it; `--check` fails in CI when an installed copy is stale. The text is embedded in the executable, so it always matches the version, and tests keep every command, option, diagnostic code and schema it names real. It is the first **skill** of the design in plan 27 section 3.13: plan 27a keeps it as it is and adds the loader that offers a skill's name and description to an embedded agent.

Two guardrails already hold **outside the model**, as the design wants: a Build plan file carries its own hash and an edited plan is refused, and the write login is a separate variable an agent's shell does not hold.

## `DbDataKit.Agents`: the runtime as a library (plan 27a)

`Shared/src/DbDataKit.Agents` is the agent loop on `Microsoft.Extensions.AI` with no provider, host or screen (its README describes each piece). Built: the loop and the tools bound to command specs (AG03), prompting per action (AG07), limits and cost (AG08), the `agent/1` and `agent-policy/1` readers and `agent check` (AG09), context assembly, bound knowledge, the model's space, skills and model fallback (see the rows of plan 27a). Tested with a scripted fake model; nothing here calls a provider, and no host runs it yet (the back-end agent, AG14, waits for its host and the `agents` module).

**Dashboards and reports** (plan 32 DBR20): `ViewDraftTools` offers `views_validate`, `dashboard_draft`, `report_draft`, `layout_add` and `view_pin` over `DbDataKit.Views`' strict loaders and catalog check; a draft is saved only as the person's own user document and only after the person approves (each time, after a check that spares them a draft that would be refused). The same five are tools of the Gateway's `mcp` module ([serving-layer.md](serving-layer.md)), the drafts waiting for a person there. No console agent offers them yet: the console holds no user-document store (it lives in a serving group), so a host adapter through the relay is what is missing.

## The console hosts back-end agents (plan 27a AG14 to AG17, AG29 to AG32)

The console runs `DbDataKit.Agents` for back-end agents (`ConsoleAgents`, `AgentsController`, the page `Agents` of the console). What a person can do there today:

- **Define an agent** as files in the console's directory: `agents/<name>/agent.yml` (read strictly, with `agent check` as you type) and the instruction files beside it, `skills/<name>/SKILL.md` for skills, `agent-policy.yml` for the install policy. The agent's page says what each tool will ask and from which layer (the strictest of the tool's own default, the install policy and the agent), the model's space (what is always given against its budget, the largest part named when it is over) and each skill (description, body size against the room left, an export as `.claude/skills/<name>/SKILL.md`).
- **Edit the install policy** with a form: minimums per trait, limits per scope, the data rule for cloud providers. A change that makes the install less strict is listed with its effect in a sentence and is saved only after a confirmation; every change is in the record, naming who made it.
- **Talk to an agent** (an administrator): the conversation, the step that waits for you with the command's own preview and traits, what each turn was given and what it used. The tools are the console's own commands **that only read**, run in this process, so they hold the console's authority and not the person's: writes are not offered, and `user`, `identity`, `instance`, `server`, `state`, `runner`, `secret` and `health` are left out. A step is answered only by the person it is for.
- **The build helper** (`build-helper`, AG14): made on the Agents tab from what a Build server offers an agent. It works on one Build project as **you**: its tools are Build's commands run on that server through the console's relay with a token that names you and the roles you hold there, never the console's own authority. It is offered only what you may run and only commands that read or change the project's own files; a command that touches a database (deploy, refresh, init, seeds, publish of metadata) and `project publish` are never offered, whatever your role. It changes files only in a workspace you name when you start the conversation (a branch of the project that is yours), and each such step waits for you (asked once per conversation by default); a denied step is not sent, so the workspace is unchanged. The skill it is given is the one of the Build server's own version.
- **See the record**: per session who (the person), which agent and version, and each step asked, approved, denied, refused or run; usage per person, agent and install. The words of a conversation are kept 30 days; the audit rows stay (the `agents` state module).
- **Approvals of outside agents**: `POST /api/agents/outside/ask` puts a step an outside client could not ask its own person about in the inbox, `GET /api/agents/outside/ask/{id}` is polled for the answer, and `POST /api/agents/outside/events` adds what ran to the record.

## What is not built

| Designed in plan 27 | State today |
| --- | --- |
| Model providers as connections, bound per channel; the provider adapters and recordings (AG04, AG05, AG18) | AG04 and AG05 built for the console install: `DbDataKit.Agents.Providers` (Anthropic and OpenAI-compatible adapters, recorded fixtures), a provider is a connection of kind `model-provider` (IX01: `connections/<name>.yml`) and the slots are `models/_slots.yml`, the Models tab with a typed key kept in the secret store and Test. Not built: per-channel slots of groups, usage per slot (AG18) |
| A workspace of the Workspace server for the back-end agent (AG14) | not started: `build-helper` works in a workspace of the Build server (below); GP32 to GP36 are another batch's |
| The agent project editor in a workspace, the form view of `agent.yml`, scripted conversations (AG16) | not started; the definition is edited as text in the console's folder |
| Traits printed in `--help`, the run header and the JSON document | not started (traits exist on every verb and in the MCP tool list) |
| Front-end agents in a serving-layer `mcp` module; an app's assistant panel | not started ([serving-layer.md](serving-layer.md) lists `mcp` as not built) |
| Memories, subagents, evaluation sets | not started |
| Actions (scripts as tools) and elevated actions | not started |

The pages [agents/context.md](agents/context.md) and [agents/context-window.md](agents/context-window.md) explain the design of an agent's context and say so at the top; they become the agents' help when agents exist.
