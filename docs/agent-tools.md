# Agents and the suite: what exists today

An AI agent can drive the suite today **from the outside**, through the commands each product already has. There is **no agent inside the suite yet**: no agent runtime, no agent definition, no chat in the console, no `mcp` module in the serving layer. [Plan 27](plans/27-knowledge-agents-integrations-work.md) decides what the embedded agents will be; [plan 27a](plans/27a-knowledge-slices.md) slices it and says, row by row, what is built. This page is the built half: what an agent can use now, and where each piece is described.

## The commands are the tools

Every product with a command line (`DbDataKit.Cli`: nouns and verbs) gives each verb three things: a **spec** (name, summary, usage), an **effect class** and a **JSON Schema** for the `data` of its document. A router refuses to be built when a verb has no schema (`Noun.cs`), so a command an agent can run is a command with a declared shape. `--format json` prints exactly one document per command; the same document is what an agent receives.

The effect class is the line between "reads" and "changes" (`CommandSpec.cs`): offline only, repository files only, target read-only, target writes, target data writes, tracking tables only. It is printed in `--help` and in the header of each run.

**Traits** (`Traits.cs`, plan 27a AG02) say more: reads definitions, reads data, writes definitions, writes data, changes structure, deletes, outward, elevated. Each effect class maps to traits by a fixed table (`TraitTable.For`); a verb may add traits (a verb that sends mail is `outward`) and may not drop one its effect class implies, or the router refuses it when it is made. The MCP server decides what to offer from traits, not from the effect class: a verb with any changing trait is offered only with `--allow-writes`, a policy that keeps data from a model can leave out every verb that `reads data` (`McpOptions.RefuseData`), and each tool's `annotations.traits` lists them.

## Model Context Protocol: `ui mcp`

`dbdatasync ui mcp` and `dbdatagateway ui mcp` start a stdio MCP server over the product's own command router (`DbDataKit.Cli/Mcp.cs`: newline-delimited JSON-RPC, protocol `2025-06-18` and the two before it). Every offered command is a tool named `noun_verb`; a call runs the command in process with `--format json` and returns its document (cut at 120,000 characters).

- **Only commands that read are offered.** A command whose effect class is anything but offline-only or target read-only is left out unless the operator starts the server with `--allow-writes`.
- **Some commands are never offered**, whatever the flag says (each product's `NotForAgents` list): the Gateway keeps out `secret`, `query` (it returns rows), pairing, `datasource adopt`, `identity` and `apps`; Sync keeps out `secret set`, `secret remove`, `library validate` and pairing. `ui` itself and `server run` and `server setup` are excluded everywhere.
- **The Gateway offers the `semantic` noun** (`describe`, `query`, `explain`, `members`) so an agent can read a published semantic model and ask it questions as the person whose token it holds; see [semantic/architecture.md](semantic/architecture.md).
- **Tests:** `DbDataKit.Cli.Tests/McpServerTests.cs`.

DbDataBuild has its own MCP server (`dbdatabuild ui mcp`: tools, plus the JSON Schemas, the support matrix and the explanation of every diagnostic code as resources, and the workflows of its skill as prompts). It is described in [DbDataBuild/docs/agents.md](../DbDataBuild/docs/agents.md) and [DbDataBuild/docs/interfaces.md](../DbDataBuild/docs/interfaces.md); tests are `AgentKitTests.cs` and `McpServerTests.cs` in `DbDataBuild.Tests.Unit`.

## Telling an agent how to work: Build's agent kit

`dbdatabuild project agent-kit --write` installs a skill (`.claude/skills/dbdatabuild/SKILL.md`, a name and a description in its header, then the rules) and the JSON Schemas beside it; `--check` fails in CI when an installed copy is stale. The text is embedded in the executable, so it always matches the version, and tests keep every command, option, diagnostic code and schema it names real. It is the first **skill** of the design in plan 27 section 3.13: plan 27a keeps it as it is and adds the loader that offers a skill's name and description to an embedded agent.

Two guardrails already hold **outside the model**, as the design wants: a Build plan file carries its own hash and an edited plan is refused, and the write login is a separate variable an agent's shell does not hold.

## What is not built

| Designed in plan 27 | State today |
| --- | --- |
| `DbDataKit.Agents` (runtime on `Microsoft.Extensions.AI`, scripted fake client, provider adapters) | not started; `Microsoft.Extensions.AI` is not in `Directory.Packages.props` |
| `agent/1` definitions, install policy, skills as a published kind | not started |
| Prompting per action (the three settings, the three layers) | not started; `--allow-writes` is the one gate |
| Traits printed in `--help`, the run header and the JSON document | not started (traits exist on every verb and in the MCP tool list, below) |
| The back-end agent in the console's chat; front-end agents in a serving-layer `mcp` module; an app's assistant panel | not started ([serving-layer.md](serving-layer.md) lists `mcp` as not built) |
| Memories, subagents, evaluation sets, transcripts (`agents` state module), limits and cost | not started |
| Actions (scripts as tools) and elevated actions | not started |

The pages [agents/context.md](agents/context.md) and [agents/context-window.md](agents/context-window.md) explain the design of an agent's context and say so at the top; they become the agents' help when agents exist.
