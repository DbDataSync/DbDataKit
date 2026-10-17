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

- `DuckDbSandboxOptions.AllowedDirectories` (via `new DuckDbProvider(options)`): the one sanctioned exception, for a source that deliberately reads a folder of files. The recipe then starts with `SET allowed_directories = [...]` (taken while the configuration is still open) and still ends with the lock; reads below the listed absolute directories work, every other path, `ATTACH`, `INSTALL`/`LOAD` and every attempt to change a setting stay blocked. It only applies to read-only contexts (a read-write context runs no recipe).
- DuckDB.NET shares one database instance per file within a process and `lock_configuration` belongs to that instance, so only the first connection can run the recipe: a later connection to the same file (a second query while the first still streams) finds the configuration locked and **verifies** it (external access off, extension install/autoload off, the same allowed directories) instead of setting it again, failing closed with a `ConnectionPolicyException` when another data source holds the file open with a different sandbox. Do not point a sandboxed source and a read-write source (or two sources with different `AllowedDirectories`) at the same file in one process.
- An in-memory database (`:memory:`) cannot be opened read-only, so `access_mode` is left out there and the SET statements still run (the database is empty and private to the connection).
- `address.database` is the file path; host, port and any credentials are refused .
- `properties` cannot set `access_mode` (deny-list).
- Failures are mapped from the DuckDB error class (the fixed prefix before the first colon); the message is never kept.
