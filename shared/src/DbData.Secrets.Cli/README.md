# DbData.Secrets.Cli

Framework-free command set. `new SecretCli(resolver, store).Execute(args, console)` where `args` are the arguments after the
product's `secret` verb. `IConsole` abstracts output, stdin and hidden prompt (`SystemConsole` is the real one).

| Command | Behaviour |
|---|---|
| `set <name> [--stdin]` | value from stdin (when redirected or `--stdin`) or a hidden, confirmed prompt; a value in argv is a usage error (exit 2) and is never echoed |
| `get <name\|ref> [--reveal]` | prints `***` unless `--reveal` |
| `list [prefix]` | names only (default prefix `dbdata:`) |
| `rm <name>` | delete |
| `test <ref>` | resolves; prints `ok`/`not found`, never the value; DPAPI blobs are elided |

Exit codes: 0 ok, 1 not found/failed, 2 usage. Not yet implemented: `import-env` (Plan 02 section 3.6).
Decision: `set` strips exactly one trailing newline from stdin; other whitespace is kept.
