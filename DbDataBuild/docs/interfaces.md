# Interfaces: terminal, web page, MCP server and MCP app

All four are clients of the same command layer (`--format json` documents). None has logic or a database path of its own, and none can do more than the command line can.

| | Start it with | What it is for |
|---|---|---|
| Terminal | `dbdatabuild ui terminal` | choose, plan and run commands interactively |
| Web page | `dbdatabuild ui web --project <dir>` | a person reads: health, lineage, models and their lowered and rendered scripts, plans (and answers their questions), sample data, table diff, tests, the support matrix; with `--allow-apply`, applies a plan they confirmed |
| MCP server | `dbdatabuild ui mcp --project <dir>` | an AI agent works in the project: the commands as tools, the skill and schemas as resources, workflows as prompts |
| MCP app | the same `ui mcp` server, in a host that supports MCP Apps | the web page inside the conversation: the person sees and decides, the model cannot press the buttons |

## Web page

```
dbdatabuild ui web --project my-project            # prints http://127.0.0.1:<port>/?token=...  open it
dbdatabuild ui web --project my-project --allow-apply
```

It listens on the loopback address only; every request needs the token printed at start. Without `--allow-apply` it can read, plan and compare, not apply. With it, applying needs the write login (`DBDATABUILD_<CONNECTION>_WRITE`) in the environment of `dbdatabuild ui web`, every allowance named in the page, and the plan's target typed back. The page reads the target with `DBDATABUILD_<CONNECTION>_READ` (plan, table diff). Details: `DESIGN.md` 9.7.

## MCP server

```
dbdatabuild ui mcp --project my-project                 # the commands that do not change a database
dbdatabuild ui mcp --project my-project --allow-writes  # also apply, run, init, ack, ...: each run needs the person's approval through the host
dbdatabuild ui mcp --project my-project --allow-apply   # the app's apply button (see below); the model is not offered it
```

What a model is never given: `connection compare --show-values`, `project sample --data`, a path outside the project. Logins stay in the server's environment (the host's server configuration), never in a tool argument.

### Connecting a host

The server speaks MCP over standard input and output. The settings below are the usual shapes; check your host's current documentation, which changes.

Claude Code: `dbdatabuild project agent-kit --write --mcp` adds the server to the project's `.mcp.json` (the other servers in the file are kept; the entry is `dbdatabuild ui mcp --project .`, read-only, passing the `DBDATABUILD_<CONNECTION>_READ` logins on by name from your environment, never the write login). It needs `dbdatabuild` on the PATH, and Claude Code asks before it first uses a project server. The skill the same command installs tells the agent how to work with the tools, and what is deliberately not offered.

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "dbdatabuild": {
      "command": "/path/to/dbdatabuild",
      "args": ["mcp", "--project", "/path/to/my-project"],
      "env": { "DBDATABUILD_POSTGRES_READ": "Host=...;Database=...;Username=...;Password=..." }
    }
  }
}
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "dbdatabuild": {
      "type": "stdio",
      "command": "/path/to/dbdatabuild",
      "args": ["mcp", "--project", "${workspaceFolder}"]
    }
  }
}
```

`scripts/pack-mcpb.sh` builds an `.mcpb` bundle (a one-click install for hosts that take them) from a published executable. It is read-only by design (no `--allow-writes`, no `--allow-apply`, no logins); use the JSON above when you need those.

## The MCP app

A host that supports the MCP Apps extension and says so when it connects (`io.modelcontextprotocol/ui` in its capabilities) gets, besides the tools, the resource `ui://dbdatabuild/app` and a `show` tool. When the model calls `show`, `project_show_plan`, `connection_deploy`, `project_show_graph`, `connection_compare` or `project_sample`, the host renders the page in the conversation and opens the screen for that result. The page calls the server through tools only the app may call (`ui_run`, `ui_file`, `ui_apply`, ...: the extension has the host hide them from the model). A host that does not advertise the extension sees none of this, by design: it would show those tools to the model. **If you cannot get the app in a host, this is the first thing to check:** at connect the server writes one line to its standard error (where a host keeps a server's log) saying which client it is and whether MCP Apps are advertised. If they are not, the host does not render MCP Apps (Claude Code in the Claude Desktop app on Windows was one such, 2026-10-07), and `show` gives the person a link instead (below).

### `show` in a host without the app

`show` is always offered. In a host that does not advertise MCP Apps its result is a link to the page on this machine's loopback address, in a browser: `http://127.0.0.1:<port>/?token=...&screen=plans`. The link works once and for ten minutes. Opening it makes the browser's own session (a cookie, and a token the page keeps for its calls) and the link is spent, so its text in the conversation opens nothing afterwards. **Whoever opens it first gets the session**: the model is told to hand it to the person and not to open it, but a model that can run commands could, so treat the conversation as able to see the link. The page is the same as `dbdatabuild ui web`: read-only, with an Apply button only if the server was started with `--allow-apply`. It works only when the browser is on the machine that runs the server (not over SSH). **`dbdatabuild ui mcp --no-show`** removes the `show` tool altogether (no link, no app screen from `show`); the other tools, and the app for the tools that carry it, are unchanged. The defaults are guardrails, not barriers: the true barriers are the read-only logins, the write login kept out of the server's environment, and `--allow-writes` / `--allow-apply` being off.

### Trying it

Without Claude Desktop or VS Code:

```
node scripts/mcp-app-host.mjs my-project --allow-apply     # a small stand-in host; open the address it prints
```

With the reference host of the extension (an independent implementation: a double sandbox, the SDK's AppBridge). It talks HTTP, and does not advertise the extension, so two small adapters sit in front of the server:

```
git clone https://github.com/modelcontextprotocol/ext-apps && cd ext-apps && npm install
(cd examples/basic-host && npm run build)
npx -y supergateway --stdio "node scripts/mcp-advertise-ui.mjs -- dbdatabuild ui mcp --project my-project --allow-apply" \
    --outputTransport streamableHttp --stateful --port 3001 --cors
(cd examples/basic-host && SERVERS='["http://localhost:3001/mcp"]' bun serve.ts)     # open http://localhost:8080
```

Pick the tool `show` with `{"screen": "plans"}`, or `project_show_plan` with `{"plan": "plans/<connection>/<id>.plan.yml"}`.

### A checklist for a real host

What differs between hosts is what is worth looking at. In Claude Desktop or VS Code:

1. Does the page appear when the model calls `show`, `project_show_plan`, `connection_deploy`, `project_show_graph`, `connection_compare` or `project_sample`? In which screen?
2. Does it follow the host's light or dark theme, and resize to its content?
3. Are the `ui_*` tools absent from what the model can call (ask it to list its tools)?
4. Click through Health, Lineage, Models (the Lowered and per-target tabs), Plans and Sample data. Anything that stays on "Running the commands…" or shows "Could not load" is a finding; note the text.
5. With `--allow-apply` and a sandbox database: open a plan, tick the allowances, type the target, run a dry run, then apply. The model must not be able to do this on its own.
6. With `--allow-writes` instead: ask the model to apply a plan. The host should show a confirmation (an elicitation) with the command and the plan's steps; declining must run nothing. A host that cannot ask must make the model say what to run by hand.
7. Does a result of the page's own tool calls reach the model? It should not.

Report what you see with the host name and version.
