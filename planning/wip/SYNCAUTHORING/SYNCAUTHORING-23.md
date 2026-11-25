# SYNCAUTHORING-23: each writer declares the target privileges it needs

## Built so far

- nothing yet: read `SetPreflight`, the writers and their key declarations.

## Plan

- `IChangeWriter.NeedsPrivileges` (INSERT, UPDATE, DELETE as flags), beside `NeedsKey`; carried on `WriterCapability` and the built-in table.
- The preflight asks the engine only for the privileges of the writers the mapping runs (the change writer, the bulk-load writer, the reconcile
  writer when reconcile is on).

## Open questions

- none yet

## Defaults taken

- none yet
