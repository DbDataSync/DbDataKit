# DbDataKit.Cli

The one command grammar and command contract of every command line in the suite.

## Grammar

`<program> <noun> <verb> [options]`. Executables are lower case (`dbdatasync`, `dbdataproxy`, `dbdatabuild`, `dbdatakit`, `dbdatastore`, `dbdatarunner`). A product keeps its own command line; `dbdatakit` manages the console and does not forward to the others.
Old command forms are removed, not aliased. `NounRouter` dispatches (`help` and `--help` work at every level; an unknown noun or verb is exit 2) and prints the help, the same way everywhere.

A noun that exists in several command lines is built once and means the same thing in each; a product adds verbs of its own with `Noun.With`:

| Noun | Verbs | Meaning |
| --- | --- | --- |
| `server` | `run`, plus `pair`, `clients`, `unpair` (`DbDataKit.Web`'s `PairingVerbs`) and product verbs | the long-running process in the foreground (what the operating system's service runs), and its pairings |
| `service` | `install`, `uninstall`, `status`, `start`, `stop`, `restart`, `doctor`, `unit`, `run` | registration of `server run` with Windows or systemd (`DbDataKit.Hosting.Cli`) |
| `cert` | `show`, `ensure`, `check`, `renew`, plus product verbs | the server's TLS certificate (`DbDataKit.Hosting.Cli`) |
| `config` | `check`, `show`, `get`, `set`, `schema` | the server's own configuration (`ConfigNoun`); a secret is set with `secret set`, never here |
| `secret` | `set`, `list`, `delete`, `check`, `rotate-key`, `export`, `import`, `protect` | secret references and the store (`DbDataKit.Secrets.Cli`) |
| `health` | `check` | is a running server answering (what container health checks run) |
| `ui` | `mcp`, plus a product's own (Build's `terminal`) | the local interfaces |

`user` (invitations, roles) exists in `dbdatakit` only: people live in the identity home (`DbDataKit.Identity`), not in a product server.

## Command contract

- Every verb names an **effect class** (`EffectClass`: offline only, repo files only, target read-only, target writes, target data writes, tracking tables only). It is printed in the help, and it decides which role a management call needs (`DbDataKit.Web`'s `ManagementAuthorization.RoleFor`) and what the MCP server offers by default.
- Every verb has a **data schema**: the JSON Schema of the `data` of its document (`DataSchemas` builds closed ones; `DataSchemas.None` for a verb that reports nothing). A router refuses to be made when a verb has none.
- `--format json` (anywhere after the noun; `json` is the only other value) makes the command write one document to standard output: `{ schema: "<program>.output/1", command, tool_version, exit_code, ok, data, messages, errors }`. `messages` and `errors` are the lines the text form wrote to standard output and standard error. `NounRouter.OutputSchema()` is the JSON Schema of the document for every verb (deterministic, so it can be checked in).
- Exit codes (`CliExit`): 0 ok, 1 failed (or, for `check` verbs, something is wrong), 2 usage. `DbDataKit.Hosting.Cli` adds 3 for "not elevated".
- No command prints a secret's value, and none takes one on the command line.

## MCP

`McpServer` (what `ui mcp` runs) serves the Model Context Protocol over standard input and output for any router: each offered verb is a tool, a call runs it in process with `--format json`, and the result is its document. Verbs that run for ever or ask questions on a terminal (`McpOptions.Always`: `ui`, `server run`, `server setup`) are never offered; without `AllowWrites` only verbs that read are.
