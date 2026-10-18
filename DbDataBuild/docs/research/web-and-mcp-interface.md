# A web interface: as an MCP app in Claude Desktop, standalone, or in VS Code

Status: step 1 of the suggested order is built (`dbdatabuild mcp`, tools and resources without a UI; DESIGN.md 9.7, progress entry 50); `dbdatabuild web` with plans, answers, apply, sample data and table diff is built (entries 51, 53 to 55), and so is the page as an MCP app (entry 56); the VS Code shell and the packaging are still ideas. This note records what it would be and whether the code base can carry it. Where it depends on a host's behavior (Claude Desktop, VS Code) that changes quickly, it says
"verify": those are the things to check against the current specifications before building anything.

## The idea

One user interface, three ways to open it:

1. **An MCP server with a UI extension** that Claude Desktop (or another MCP host) renders next to the conversation: the agent calls a tool, and instead of text the person gets the plan, the lineage graph or the
   diff in a panel they can click.
2. **A standalone web page**, `dbdatabuild web`, bound to the loopback address, for people who do not use an agent.
3. **A VS Code extension** that hosts the same page in a webview and puts the diagnostics in the Problems panel.

The point is to review and decide, which is where text is worst: reading a 60-step plan, seeing which model feeds which, comparing the lowered query with the rendered one for each target, answering the
questions `plan` asks, comparing two tables. Writing models stays in the editor and with the agent.

## Why the code base is already most of the way there

- **The command surface is the API.** Every command has a `--format json` document with a closed schema (`schemas/output.schema.json`) and an effect class (offline, repo files, target read-only, target writes).
  The terminal interface already works this way: it runs commands in process and shows their documents, with no logic of its own. A web interface is a second client of the same surface, so there is nothing to build
  in the core for the read-only screens. The schemas generate the TypeScript types.
- **Commands run in process**, with progress and a stop request flowing through `CommandContext` (the hooks the TUI uses), so a long `plan` or `apply` can report and be cancelled from a page.
- **The tool is one self-contained executable per platform** (`scripts/publish.sh`), which is what an MCP server bundle wants: no runtime to install.
- **The safety model does not move.** Writes still go through `MutationGate` on the write login, `apply` still takes exactly a hashed plan and refuses an edited one, risky and destructive steps still need their
  named allowance. The page cannot do more than the CLI can; it is the CLI with a better view.
- **Principle 8 fits.** An agent must be able to work without seeing data; an operator may look (`diff --show-values`). The two audiences are different channels in MCP (see below), so the page can show values to
  the person without the model ever receiving them.

## What it would show

| Screen | Source (existing command) | Notes |
|---|---|---|
| Project health | `validate`, `define --check`, `render --check`, `test` | findings grouped by model and code, each with its `explain` text; a click opens the file at the line |
| Lineage | `graph --columns`, selectors | a layered graph, column lineage on selection, `changed:<ref>` highlights what a branch touches |
| A model | `metadata`, rendered files | SQL, definition, the lowered query, and the rendered load script per target side by side, with the rewrites that fired (`-- type rules`, `-- target rules`, `-- rewrites off`) |
| Plan review | `plan`, the plan file and report | steps in order with their risk class and the questions, answered in a form that writes the answers file; the diff against what is live |
| Apply | `apply` with progress | step-by-step progress, stop, the statement log; the button is the human approval |
| Tests | `test` | metadata rules and model tests with the violating rows |
| Table diff | `diff` | counts by default; values only when the person asks (`--show-values`) |
| Support matrix and rewrites | `matrix`, `matrix --rewrites` | a table of constructs per engine, and switches that write `rewrites:` into the configuration |
| Sample data | `seed`, `sample` | a preview of what a model gives on the seeded rows |

## As an MCP server

A `dbdatabuild mcp` command would speak MCP over stdio from the same executable:

- **Tools generated from the command tree**, as the TUI generates its forms: one tool per command, inputs from the options (which already have descriptions), outputs from the closed schemas, and the effect
  class mapped to the tool annotations (read-only for the offline and target-read-only classes, destructive for `apply`).
- **Resources:** the schemas, `explain` for every diagnostic, the agent kit (the skill the tool already installs), the rendered files, the metadata documents, the plan reports.
- **Prompts:** the workflows in the skill (add a model, change a model, review a plan).
- **Project root** from the host's roots, or a `--project` argument in the server configuration. **Credentials** stay in the server's environment (`DBDATABUILD_<TARGET>_<READ|WRITE>`), set in the host's
  server configuration, never in a tool argument or a conversation.

### The UI extension (MCP Apps)

The MCP Apps extension lets a tool return a `ui://` resource, an HTML page the host renders in a sandboxed frame, which can call tools back. If the host supports it (verify the current specification and what
Claude Desktop and VS Code support today), the design would be:

- **One HTML bundle**, inlined into a single file (a sandboxed frame cannot reach the network or the disk), served as the `ui://` resource. All data comes from tool calls.
- **Two audiences, two channels.** What the model should see (the summary text, counts, findings) goes in the tool result's content; what only the person should see (rows from `diff --show-values`, sample
  data, the statement text of a plan) goes to the page only. The model cannot read a value it was never given.
- **Approval that the agent cannot give.** `apply` (and `ack`, `publish-metadata`) are tools visible to the page and hidden from the model, so the model proposes a plan and the person presses the button.
  Where the host supports it, MCP elicitation is the fallback for a confirmation inside a conversation.
- **Long commands:** `plan` on a large project and `apply` take longer than a typical tool call. Use progress notifications and the host's long-running-task support (verify); the page polls a started run
  rather than holding a request open.

### Packaging

Claude Desktop installs a local server from a bundle (an `.mcpb` archive with a manifest and the executable per platform). A release job can build it next to the executables. VS Code configures MCP servers in
its own settings (a `.vscode/mcp.json` the extension can write).

## Standalone: `dbdatabuild web`

The same page behind a small HTTP server in the executable: loopback only (the same rule as the test engines), a random token in the URL, Origin and Host checks against DNS rebinding, no CORS, one project
per server. Endpoints are the commands (`POST /run/<command>` returning the JSON document, a stream for progress), plus the files. No second code path to a database, as with the TUI.

## In VS Code

A webview hosting the same page, with the extension host running the executable (or talking to `dbdatabuild web`). Two more things only an editor can give: **diagnostics** (`validate --format json` already has
file, line and column for every finding, so they can fill the Problems panel and offer the `Fix:` text as a quick fix) and **go to definition** from a model name to its file and from a column to its source column
(the lineage has it). The diagnostics may be worth more than the whole webview and need no UI at all.

## One front end, a thin host adapter

```
page  --  host.run(command, args)  /  host.progress  /  host.confirm  /  host.openFile
             |-- MCP app:    tools/call to the server, results in the frame
             |-- web:        fetch to the loopback server
             `-- VS Code:    postMessage to the extension host
```

The page knows only the adapter; the three shells implement it. The adapter is the whole portability cost.

## Feasibility

| Piece | Effort | Risk |
|---|---|---|
| `dbdatabuild mcp` (tools, resources, prompts, no UI) | small to medium: the command tree and schemas are the input | low; a useful feature by itself, since it gives an agent the tool without a shell |
| `dbdatabuild web` and the page (read-only screens) | medium: the page is the work (a graph, a diff view, a plan viewer); the server is thin | low |
| Plan review with answers, and apply with progress | medium | the approval flow has to be right; the existing plan hash and gates already hold |
| The MCP Apps shell | small once the page exists | **the specification and the hosts are young and differ**; keep the adapter thin and do not depend on a feature one host lacks |
| VS Code extension | medium: webview plus diagnostics | low; diagnostics first |
| Packaging (.mcpb, extension) | small, in the release workflow | signing and store review for the extension |

Risks to watch: values reaching the model through a tool result (the channel split above is the control, and a test should assert that no command document sent to the model carries row values); a page that
can start `apply` without a person (app-only tools, and the plan hash); a loopback server reachable from a web page (token, Origin check); large documents (the metadata of a big project) in a tool result
(paging, and resources instead of inline content); the page diverging from the CLI (it must stay a client of the JSON surface, as the TUI does).

## Suggested order

1. `dbdatabuild mcp` without a UI: tools from the command tree, the schemas and `explain` as resources, the agent kit as a prompt. Worth having whatever else is decided.
2. `validate` diagnostics into VS Code's Problems panel (a small extension, no webview).
3. `dbdatabuild web` with the read-only screens (health, lineage, a model, tests), then plan review and apply.
4. The same page as an MCP app, with app-only approval tools.
5. The VS Code webview.

## Open questions

- Which host features can be relied on: MCP Apps in Claude Desktop and in VS Code, elicitation, long-running tasks, roots. (Check the current specifications; they move.)
- Is the page a single-page application built with a toolchain the repository does not have yet (Node), or plain modules with no build step? A build step in a .NET repository is a cost; a single inlined file
  is what the sandboxed frame needs anyway.
- Should the page edit anything beyond the answers file and the `rewrites:` configuration? (Suggestion: no; editing stays in the editor.)
- Does an operator ever want the page to show values by default for a sandbox target? (`diff --show-values` is the precedent: asked for, never default.)
