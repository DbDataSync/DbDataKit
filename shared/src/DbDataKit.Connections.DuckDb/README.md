# DbDataKit.Connections.DuckDb

DuckDB provider over DuckDB.NET.Data.Full 1.5.6 with the sandbox recipe (Build's set; closes Proxy S-1 for DuckDB).

Read-only contexts open the file with `access_mode=READ_ONLY`, then run, in order:

```
SET enable_external_access = false
SET autoinstall_known_extensions = false
SET autoload_known_extensions = false
SET lock_configuration = true      -- last: nothing can be set back
```

`ReadOnlyEnforcement.Engine`. The tests run an in-process DuckDB and prove that writes, `read_csv`/`read_text`/`read_blob`/direct file reads, `glob`, `COPY ... TO`, `ATTACH`, `INSTALL`/`LOAD` and every attempt to undo a setting are refused, with an unsandboxed control showing the same file is readable without the recipe.

- `address.database` is the file path; host, port and any credentials are refused (an in-memory database cannot be opened read-only).
- `properties` cannot set `access_mode` (deny-list).
- Failures are mapped from the DuckDB error class (the fixed prefix before the first colon); the message is never kept.
