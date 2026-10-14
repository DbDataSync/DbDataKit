# Follow-up: the Arrow Flight SQL driver's assemblies ship inside the tool, unlike every other driver's

Left deliberately by phase 197F and recorded here so it is not forgotten. **This is meant to be fixed** — "ship
without the dependencies embedded" is the standard for every other driver (phases 109h/109i), and the Flight SQL
driver is the only one that breaks it.

## What's true today

`DbDataSync.Drivers.ArrowFlightSql` references these as ordinary packages, so their assemblies are copied into the
tool's own output and every install carries them, whether or not anyone ever configures a Flight SQL connection:

- `Apache.Arrow.Flight.Sql` 23.0.0 → `Apache.Arrow.Flight`, `Apache.Arrow`, `Grpc.Net.Client`, Google.Protobuf
- `Apache.Arrow.Compression` 23.0.0 → `K4os.Compression.LZ4.Streams`, `ZstdSharp.Port` (added to read compressed
  batches; see phase 197F's retrospective)

All managed; no native assets on this target. Their size has **not been measured** — doing so is the first step.

Every other driver uses `ExcludeAssets="runtime"` so the compiler still sees the types while the DLL is restored on
demand into `<repo>/libraries/<id>/lib/` and resolved through `LibraryRegistry`'s `AssemblyLoadContext.Resolving`
hook (`IDriver.RequiredLibraryId`, `KnownLibraries`).

## Why it was not done in 197F

`KnownLibraries`'s catalog entry is one package plus the `DbProviderFactory` type it registers
(`LibraryCatalogEntry.PackageId`/`FactoryType`). Flight SQL is **several** packages with **no** factory. The
installer itself already takes a list (`LibraryInstaller.InstallOrDeferAsync(..., IReadOnlyList<PackageRef>, ...)`),
but nothing has exercised a multi-package library or a catalog entry that is not a provider.

## What doing it involves

1. A `KnownLibraries` entry (id e.g. `arrow-flight-sql`) that carries **both** packages. The `FactoryType` field has
   to name *some* real public type in an assembly the driver's compiled IL references, purely for
   `DriverLibraryCompatibility`'s surface check — the `ikvm` entry is the precedent (it names `java.sql.Types`).
   Probably needs `LibraryCatalogEntry` to grow a list of packages instead of one.
2. `ExcludeAssets="runtime"` on both package references in the driver csproj, and on the test project's reference if
   it should not double-restore.
3. `ArrowFlightSqlDriver.RequiredLibraryId => "arrow-flight-sql"`, and an entry in `BuiltInDriverLibraries.LibraryIds`
   for **each of the six driver ids** (they share the one library) so the API and task runner install it on first
   use, like `MsSql`/`Postgres`.
4. `Cli/BuiltInDrivers.cs` — add the six drivers (it is the list of drivers with a `RequiredLibraryId`).
5. Check phase 109j's IL-surface check (`DriverLibraryCompatibility`) copes with a driver whose typed references
   span two packages' assemblies.
6. **Verify for real, not by inspection**: on a clean repo, with no `libraries/` directory, configure a Flight SQL
   connection and run a test and a read — including a compressed batch, since `Apache.Arrow.Compression` loads its
   codecs (`K4os.Compression.LZ4`, `ZstdSharp`) lazily by name and a missing one only fails on the first compressed
   batch. Also confirm the transitive closure restores on the runtime-only image (phase 121's in-image catalog
   cache) with no SDK present.
7. Update `docs/drivers-and-libraries.md`, whose Arrow Flight SQL section currently says the driver "ships with the
   tool" and needs no library.

## Not blocked on anything

Nothing else depends on this; the driver works today. The cost of leaving it is install size and that this one
driver is inconsistent with the rest.
