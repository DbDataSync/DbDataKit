# SYNCAUTHORING-10: work in progress

The offline checks of a sync draft, over `catalog-cache.yaml` and the files, in the Workspace's process (no call out, no `dbdatasync`).

## Built so far

- nothing yet: reading the code (the check is `SyncProjectKind.CheckAsync`, `DraftCheck.RunAsync`; the cache reader is `DbDataKit.Catalog.Cache`; the
  type tables and the lexer are the shared `DbDataKit.Dialect`, which Sync's built-in drivers read their types through).

## Open questions

## Defaults taken
