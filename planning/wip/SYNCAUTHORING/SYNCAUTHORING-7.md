# SYNCAUTHORING-7 (work in progress)

## Built so far

- Moved to `Shared/src/DbDataKit.Sync.*` (namespaces unchanged): Core (the configuration model), Drivers.Abstractions (`DriverRegistry`),
  Scripting.Abstractions (`ScriptSlots`), Drivers.Generic (what Provisioning and the descriptors need), Provisioning (`MappingColumnReader`), and
  the part of Drivers.Descriptor that reads a `driver.yaml` (`DriverDescriptors`, `DescriptorDialect`, `KnownDrivers`). `DriverScriptHost`
  (compiles and runs `driver.csx`), `CompiledDriverManifest`, `DriverLoader` and `DriverDescriptorReader.BuildDriver` (load a library or a type
  by name) stay in DbDataSync.
- The old projects forward (`TypeForwards.cs`); `DriverDescriptorReader` is a thin adapter over `DriverDescriptors`.
- Tests moved to `Shared/tests/DbDataKit.Sync.*.Tests` (xunit v3): Core 530, Drivers.Abstractions 68, Drivers.Generic 330, Drivers.Descriptor 27,
  Scripting.Abstractions 3, all passing.
- `SyncCatalogColumns` in `DbDataKit.Authoring.Server` (Sync's `IColumnCatalog` over the draft's `catalog-cache.yaml`), tested in the new
  `Shared/tests/DbDataKit.Authoring.Server.Tests`.

## Not built yet

- DbDataSync's unit suite run, the Workspace and console builds, the repository checks.

## Defaults taken (each becomes a proposal)

- Namespaces stay `DbDataSync.*` in the shared assemblies. Example: a person's transform script starts `using DbDataSync.Scripting.Abstractions;`
  and keeps compiling; a driver built against `DbDataSync.Drivers.Abstractions` 0.1.0 still loads through the forwards.
- Drivers.Generic and the whole of Provisioning moved (not only `MappingColumnReader`), since the reader and the descriptor need them. Example: the
  Workspace could now call `MappingProvisioner.ApplyAsync`, which runs DDL on a database; nothing in the Workspace calls it.
- Drivers.Generic's three tests that need Sync's engines (GenericDriverTests, GenericDriverTestDatabase, QueryCatalogTests) stay in DbDataSync.
  Example: Shared CI starts only SQL Server and PostgreSQL and these tests do not skip without MySQL.
- The in-process test is a new project `Shared/tests/DbDataKit.Authoring.Server.Tests`, while the Workspace's other sync tests are in
  `DbDataWorkspace.Tests`.
- The capabilities of the built-in drivers (the change tracking reader of SQL Server, its MERGE writers) are declared in DbDataSync.Drivers.MsSql and
  the other engine projects, which did not move. Example: SYNCAUTHORING-3's "the readers and writers of a SQL Server connection" cannot be answered
  from the moved code alone.

## Open questions

- None beyond the defaults.
