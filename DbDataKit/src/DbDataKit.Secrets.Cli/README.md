# DbDataKit.Secrets.Cli

The shared `secret` commands, framework-free. A product mounts them under its own `secret` noun:
`new SecretCli(resolver, store, admin, references, protect).Execute(args, console)` where `args` are the arguments after `secret`.
`IConsole` abstracts output, stdin and the hidden prompt (`SystemConsole` is the real one). `store` and `admin` are the
`EncryptedFileSecretStore` of `DbDataKit.Secrets.Store`; `references` gives `check` the references in the product's configuration;
`protect` is `DpapiProtector.ToInlineRef` on Windows and absent elsewhere.

**No command prints a secret's value, and none takes one on the command line.** A test runs every command with a sentinel as value and as passphrase and fails if it appears in anything written.

| Command | Behaviour |
|---|---|
| `set <name> [--stdin]` | the value from stdin (when redirected, or `--stdin`) or a hidden, confirmed prompt; a value in argv is a usage error (exit 2) |
| `list [prefix]` | names with when each was written and the key it uses (default prefix `dbdata:`) |
| `delete <name>` (`rm`) | delete |
| `check [ref...]` | resolves each reference given, or every one in the configuration; prints `ok` or `not found`, never a value; exits 1 if any does not resolve; DPAPI blobs are elided |
| `rotate-key` | a new data key, every value encrypted again under it, the old key deleted; an interrupted rotation is finished |
| `export <file> [--prefix p] [--passphrase-stdin]` | the secrets, encrypted under a passphrase (asked twice, hidden, or the first line of stdin), to move a server; the store's key is not in the file |
| `import <file> [--overwrite] [--passphrase-stdin]` | read such a file; names already there are kept unless `--overwrite` |
| `protect [--stdin]` | the inline, encrypted form of one value for a single field (Windows: `dpapi:`); prints that form, not the value |

Exit codes: 0 ok, 1 failed or something did not resolve, 2 usage. `set` strips exactly one trailing newline from stdin; other whitespace is kept.
`get --reveal` and `test` of earlier versions are gone (`check` replaces `test`).
