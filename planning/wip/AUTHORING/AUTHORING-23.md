# AUTHORING-23: the Runner's built-in package `dbdatakit-metadata` (`discover`, `describe`)

Branch `backup/authoring-23`, stacked on `backup/authoring-20` (catalog-cache/2, ready). **Nothing is built yet**: the session wound down before the
code started. This file is the design reached by reading the code, for the next worker.

## What the code offers (read, not changed)

- Runs: `POST /api/v1/tasks/{package}/{channel}/{task}/run` with `inputs`, `on_behalf_of` (the run records the person, `RunnerControl.RequestedBy`), and
  `version` for a staged version (`RunnerScheduler.RunVersionAsync`). The queue holds one run per task at a time (409 "queued or running already").
- Slots: the scheduler computes a run's handles (`RunnerScheduler.RunItemAsync`, `slotResolver.Handles(taskRef, task.Connections)`) from the task's own
  package's bindings in the channel; `RunnerSlotResolver.ResolveAsync(slot, connection, role)` gives the grant; a `read` request with no read login is
  refused (no fallback). The Kit's opener asks role `default` always (`ConnectionGrant.Opener`, `Runner.Kit/ConnectionGrant.cs`).
- Answers: a value step result is capped at 64 KiB (`StepStore.DefaultMaxResultBytes`); a stream step keeps a payload, served at
  `GET .../tasks/{p}/{c}/{t}/steps/{name}/payloads/{id}` and read by `RunnerPublishClient.StepPayloadAsync`. The Workspace reads steps of a run with
  `WorkspacePublishing.RunOfAsync`.
- Opening a database from a grant: `RunnerConnectTest` (DbDataRunner.Server) builds a `ResolvedLogin` and uses `IDbProvider.OpenAsync`
  (SqlServer, Postgres, MySql/MariaDB, Oracle providers). `StorageGrant.DefinitionOf` (DataModel.Runner) makes the definition from a grant.
- Readers: `DbDataKit.Metadata` (`SqlServerCatalogReader`, `PostgresCatalogReader`, `MySqlCatalogReader`, `OracleCatalogReader`; `ListCatalogsAsync`,
  `ListSchemasAsync` with empty schemas, `ReadSchemaAsync`, `ReadSchemaKeysAsync`, `DescribeQueryAsync`; `QueryProbe.RequireReadOnly`).
- Shipping a .NET package beside the runner: the server's csproj already publishes `taskhost/` beside the program (`CopyTaskHost`,
  `TaskHostIntoPublish`); `RunnerPackageInstaller.Install` puts a folder in the state served at once, idempotent by content.

## Design proposed (not built)

1. A library `Shared/src/DbDataKit.Runner.Metadata`: jobs `discover` and `describe` (`RunnerJob<Input, NoProgress, MetadataSlots>`, one slot
   `connection` typed `ConnectionGrant`), opening with the providers and reading with `DbDataKit.Metadata`. `discover` input: `schemas` (list) or
   `pattern`, optional `table` (one table, for AUTHORING-21). It answers the JSON a connection has in catalog-cache/2 (`CatalogCache.FromAnswer` reads it;
   `engine`, `databases`, `tables` with keys) as a **stream step** `discover` (the answer can pass 64 KiB). `describe` input `sql`: `QueryProbe.RequireReadOnly`
   then the reader's `DescribeQueryAsync`; answers columns as a value step. No row ever.
2. Shipped: the server's csproj publishes the library with a `package.yaml` into `builtin/dbdatakit-metadata/` beside the program (as `taskhost/`); the
   runner installs it with `RunnerPackageInstaller.Install` into a channel the first time a read is asked there.
3. A Runner route `POST /api/v1/metadata/{channel}/{task}` with `{package, slot, inputs, on_behalf_of}`: refuses a slot the project package does not declare
   (its manifest in the channel, served or staged), refuses `prod` unless the caller says the person is an approver (the Workspace decides the role;
   question 2), picks role `read` when the bound connection has a read login, else `default`, and queues the run with `on_behalf_of`. The generic run
   route refuses the built-in package (so nobody passes another package's slot through inputs).
4. The scheduler: for the built-in package, the handle of `connection` is the project package's binding of the named slot in the channel (the same
   `Handles` call, so the production-binding rule holds). The Kit needs a role: add `Slot<T>.OpenAsync(string role, ct)` (a role-taking opener beside
   `SlotOpener`), or have the protocol take the role from the run's start for this package.

## Open questions for the owner (to become proposals)

1. One run per task at a time: two authors reading in `dev` at once get "queued or running" for the second. Allow overlap for the built-in package
   (its manifest says `allow_overlap`), or queue? Recommendation: allow overlap.
2. A group mixing Windows and Linux runners would install different file hashes of the built-in package (native assets differ) and flip its version.
   Install per platform name, or build the package portable only? Recommendation: portable (framework-dependent, no RID), checked by a test.
3. Who says "approver" for `prod`: the Workspace (it knows project roles) or the Runner (it does not)? Recommendation: the Workspace refuses an author's
   `prod` read before calling; the Runner refuses `prod` reads from a caller without its admin role.

## Not started

AUTHORING-21 (refresh route, difference) and AUTHORING-24 (channel picker and Refresh): nothing built. Note for AUTHORING-24 from the coordinator: the
refreshed cache as an unsaved edit must go through the draft's unsaved edits in the Workspace frame, built on `backup/syncauthoring-1` (ee911645):
`useDraft().edit` from `@dbdatakit/workspace` (`draft.tsx`: `DraftProvider`, `useDraft`, `useDraftFile`, `useDraftFiles`). Accepting a refresh writes
the new `catalog-cache.yaml` text (`writeCatalogCache`, from `backup/authoring-20`) with `useDraft().edit`, so it lands in the same unsaved edits the
Designer and Files share.
