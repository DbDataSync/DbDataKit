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
   (its manifest in the channel, served or staged), allows `prod` for anyone with access to the project (the Workspace warns first; owner's answer 3 below), picks role `read` when the bound connection has a read login, else `default`, and queues the run with `on_behalf_of`. The generic run
   route refuses the built-in package (so nobody passes another package's slot through inputs).
4. The scheduler: for the built-in package, the handle of `connection` is the project package's binding of the named slot in the channel (the same
   `Handles` call, so the production-binding rule holds). The Kit needs a role: add `Slot<T>.OpenAsync(string role, ct)` (a role-taking opener beside
   `SlotOpener`), or have the protocol take the role from the run's start for this package.

## The owner's answers (2026-11-18), to build

The three questions this file asked, answered by the owner outside the subject's session; the next worker builds them as answered (they need no
proposal):

1. Two authors reading in `dev` at once. Answer from the owner, 2026-11-18: "Metadata reads overlapping is fine. It shouldn't even need a review, that's
   what databases do." So the built-in package's runs overlap: no queueing and no one-run-per-task limit for `dbdatakit-metadata` (its manifest says
   `allow_overlap`, or the scheduler exempts it); the second author's `discover` starts at once beside the first.
2. A Runner group mixing Windows and Linux. Answer from the owner, 2026-11-18: each Runner installs the metadata package of its own version, "fine for
   now". So no portable-only build and no per-platform names are required: each runner installs the `builtin/dbdatakit-metadata/` shipped beside its own
   program, and a group whose members differ simply holds the versions they shipped.
3. Reads in production. Answer from the owner, 2026-11-18: "we will need some production reads, because without them it's impossible to design a sync
   between systems that don't have a dev equivalent", with the owner's general principle of the same day (docs/architecture.md, "Risky actions: allowed,
   with a clear warning"): anyone with access to the project may discover and describe in `prod`, with a clear warning that it is production; no
   approver role is required. So design point 3 above changes: the Runner route does not refuse `prod` for a non-approver and takes no approver flag; the
   Workspace shows the production warning before the read (AUTHORING-24's picker) and the run records the person (`on_behalf_of`) as for any read. The
   read login is still preferred (`read` role when the bound connection has one).

## Not started

AUTHORING-21 (refresh route, difference) and AUTHORING-24 (channel picker and Refresh): nothing built. Note for AUTHORING-24 from the coordinator: the
refreshed cache as an unsaved edit must go through the draft's unsaved edits in the Workspace frame, built on `backup/syncauthoring-1` (ee911645):
`useDraft().edit` from `@dbdatakit/workspace` (`draft.tsx`: `DraftProvider`, `useDraft`, `useDraftFile`, `useDraftFiles`). Accepting a refresh writes
the new `catalog-cache.yaml` text (`writeCatalogCache`, from `backup/authoring-20`) with `useDraft().edit`, so it lands in the same unsaved edits the
Designer and Files share.
