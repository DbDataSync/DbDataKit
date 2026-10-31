# DbDataKit.Semantic.Cli

The `semantic` noun of a command line (plan 19, J15): written once, mounted by a product with the way it connects.

```
semantic describe <model>
semantic query   <model> (--sql "..." | --request '{...}') [--max-rows N]
semantic explain <model> (--sql "..." | --request '{...}')
semantic members <model> <attribute> [--search TEXT] [--limit N]
```

Each verb sends one statement to the model's source (semantic SQL, or a request of kind `describe`, `query`, `explain` or `members`) through an `ISemanticSession`, which the product opens from its own connection options (`SemanticConnect`). The server runs the statement as the caller, so the caller's access is all there is; the verbs add nothing to it.

Every verb answers the same document in `--format json`: `model`, `columns` (name, type), `rows`, `row_count`, `truncated` and the server's `notes` (`qis.status` and the like). The `data` schema is closed, so an agent can rely on it. A product that mounts the noun for agents chooses its own credentials: the Proxy reads them from `--user` and the `DBPROXY_PASSWORD` environment variable, never a flag an agent would put in a tool call.
