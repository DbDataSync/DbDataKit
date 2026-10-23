# Plan 13: One suite — standardizing DbDataSync, DbDataProxy and DbDataBuild

Status: **proposed** (2026-10-21), **revised twice the same day after the owner's review**:

- the updater is shelved (W9), and one YAML format is used by every product (D2);
- Build will get a built-in scheduler and monitoring, so scheduling and monitoring become shared (W10);
- each product stays a **standalone install option**; the suite is called **dbdatakit** (D3, D4);
- each product keeps its own CLI, with lower-case executables and Build's `<noun> <verb>` grammar everywhere; `dbdatakit` is a CLI only
  for managing and configuring the shared console (W4);
- the console and the products are **separate**: each product is a set of services (a server with a management API, no web UI of its
  own), and the **dbdatakit console is the one shared UI** for all of them (section 3, D1). A standalone install is a product plus the
  console with only that product registered;
- Build gets a `server` noun for a multi-project server (W7), and **project authoring in the web interface** (W11);
- console and servers may run on different machines; **the console always calls the server, never the reverse**, and **each server can
  be paired with several consoles** (W12);
- **DuckDB 2.0.0 now**, using the released engine binary with the DuckDB.NET packages that do not bundle an engine, instead of waiting for
  a DuckDB.NET 2.0 (W1, D6);
- **Proxy's DuckDB data source gets no 2.0 work of its own**: it was only a test backend for early development, and it moves to 2.0 with the shared package (W1, W6);
- **secrets: finish the shared code and drop ClrKernel**: encrypted values in a YAML file, the key protected by DPAPI on Windows and by a
  permission-restricted file elsewhere (W13, D10);
- **a clean break everywhere**: nothing from an existing install is migrated or imported (configs, scripts, libraries, secrets, users,
  state). Old-format files are refused with a message, never converted (section 3, "Clean break");
- every other recommendation in section 7 is accepted.

Scope: everything needed to turn three products that share a repository into one suite with one
management console, one set of definitions and one release, while removing as much duplicated code as possible. Breaking changes are
allowed. Two pieces of work are folded in: **finishing DuckDB 2.0** and **moving DbDataSync's libraries, drivers and scripts to
Roslyn scripting with NuGet package loading** (no SDK at run time; W2).

Inputs: plans 01-12, `docs/plans/STATUS.md`, the overlap review (`docs/review/OVERLAP-AND-CONSOLIDATION.md`, removed in F13; in git history), `DbDataBuild/docs/research/duckdb-2.0/README.md`,
`DbDataBuild/docs/progress/OPEN-ITEMS.md` (section H), `DbDataSync/docs/drivers-and-libraries.md`,
`DbDataSync/architecture/planning/done/csharp-scripting-host.md`, and the code at `90cdbc5`.

## 1. Goal

- **Less code.** Each concern (connections, dialect, metadata, secrets, config, transfer, hosting, update, CLI, web shell, auth) has one
  implementation in `DbDataKit/`, and the products keep only what is really theirs: replication (Sync), the Flight SQL server (Proxy),
  and modelling and lowering (Build).
- **One console.** One web UI, the dbdatakit console, manages all three products. Proxy and Build get management and configuration
  screens for the first time, built from the same shell, components and back-end as Sync's, and Build gets project authoring in it.
- **Each product still stands alone.** A product needs none of the other products. Installed on its own it is the product's services
  plus the console (which then shows only that product), and its CLI works with or without the console.
- **A dbdatakit suite release** with all three products and the console, from one commit, shipped the way products ship today (Docker
  images and the MSI installer, plus the zips and install scripts), alongside each product's own release (section 8). Update work waits
  until the products have settled.

## 2. Where things stand

### 2.1 Adoption of the shared libraries

What each product takes from `DbDataKit/` today (`DbDataKitReference` items in the product `.csproj` files):

| Concern | Shared package | DbDataSync | DbDataProxy | DbDataBuild |
| --- | --- | --- | --- | --- |
| Hosting, service, systemd, certificates | `Hosting.*`, `Security.Acl` | **yes** (TLS rollout, plan 12) | **yes** | no (a CLI; no service) |
| Config (strict YAML/JSON, schemaVersion, migrations) | `Config.*` | no (own `Core/Config`, about 4,900 lines) | **yes** | no (own strict YAML reader) |
| Secrets | `Secrets.*` | no (`ClrKernel.Core.Secrets` keyring) | **yes** | no (env vars only) |
| Dialect, lexer, quoting, read-only classifier | `Dialect.*` | no (own `Core/Sql`, about 2,000 lines) | **yes** | no (own `DialectProfile`) |
| Connections, composers, failure model | `Connections.*` | no (per-driver connection strings) | **yes** | no (`Logins`, `OdbcLogin`, `FlightSqlLogin`, `DriverFailure`) |
| Metadata / catalog readers | `Metadata.*`, `Odbc.Metadata` | no (driver `Metadata`) | **yes** | no (`CatalogReader`, `SourceCatalogReader`, `OdbcCatalogClient`) |
| Transfer, staging, write gate | `Transfer.*` | no | n/a | no (`Transfer.cs`, `MutationGate`, `StatementLog`) |
| Install / update | `Update.*` | no (own `Updates` + `Launcher`, about 1,900 lines) | no (`install.ps1`) | no (Scoop / MSI) |
| Library install | `Connections.Libraries` | no (own `Libraries`, about 1,150 lines) | n/a | n/a |

Two facts stand out:

- **DbDataBuild uses nothing from `DbDataKit/`**, and DbDataSync uses only the hosting and certificate packages from the TLS rollout.
  Proxy is the only full consumer.
- **About 6,900 lines of shared code have no consumer at all:** `Transfer.*` (3,200), `Update.*` (2,000), `Connections.Libraries`,
  `Connections.Descriptor`, `Connections.Postgres`, `Dialect.Arrow`, `Dialect.Conformance`, `Secrets.Cli`, `Secrets.Keyring`. They were
  written from Sync's and Build's code but never adopted. This plan either adopts each one or deletes it (section 4, W3), except
  `Update.*`, which stays shelved with the updater (W9).

### 2.2 Build setup

| | DbDataSync | DbDataProxy | DbDataBuild | DbDataKit |
| --- | --- | --- | --- | --- |
| Solution | `.slnx` | `.slnx` | `.sln` | `.slnx` |
| Central package management | **no** (versions inline) | yes | **no** (versions inline) | yes |
| `DbDataKitReference` mechanism | yes | yes | **no** | n/a |
| Warnings as errors | per project | **false** | true | true |
| `global.json` | none | yes | yes | yes |

Version skew that a shared pin would remove: `Npgsql` 9.0.3 (Sync) vs 10.0.3 (Kit, Build); `Microsoft.Data.SqlClient` 7.0.2 (Sync) vs
7.1.1; `DuckDB.NET.Data.Full` 1.5.5 (Sync) vs 1.5.6 (everyone else). Because the products build against `DbDataKit/` from source at the
same commit, a skew like this becomes a runtime conflict as soon as Sync adopts a shared package that brings the newer version along.

### 2.3 User interfaces

| | DbDataSync | DbDataProxy | DbDataBuild |
| --- | --- | --- | --- |
| Web | React 19 + Vite SPA (about 22,000 lines of TS), ASP.NET API, SignalR, passkeys, Windows sign-in, invites, admin pages (certificates, updates, config history, drivers, libraries, files, docs) | **none** | `ui web`: one 783-line `index.html` with a small server, read-only unless `--allow-apply` |
| Terminal | Terminal.Gui setup | none | Terminal.Gui TUI (a client of the JSON command surface) |
| Agent | none | none | MCP server and MCP app |
| CLI | `dbdatasync` (own parser + `Hosting.Cli`) | `DbDataProxy` (own `Cli.cs`) | `dbdatabuild` (System.CommandLine, every command has a JSON document with a schema) |
| Configuration | git-tracked config repository with auto-commit and history UI, plus `appsettings.json` | `appsettings.json` (strict, validated, migrations) | project files (YAML, committed by the author) |
| Identity | users, sessions, passkeys and invites in the state database | its own users (PBKDF2), Windows accounts, JWT mappings, pass-through | none (local tool) |

### 2.4 DuckDB 2.0

- DuckDB **2.0.0 LTS was released today** (2026-10-21; supported until at least October 2027). It brings a **new storage format that
  DuckDB before 2.0 cannot read**.
- **DuckDB.NET has no 2.0 release yet** (latest `DuckDB.NET.Data.Full` is 1.5.6.1). Build's experiments show DuckDB.NET 1.5.6 runs unchanged
  against the 2.0 native library, because the old C API is still there.
- Build has done the hard part against the alphas (entries 173 and 174: the lowerer reads 2.0's own plans; the clock functions that 2.0
  folds into constants are kept as calls). Still open (OPEN-ITEMS H): ANY/ALL in 2.0's CASE form is refused rather than translated;
  `%` and `//` by zero raise on 2.0 where the lowering writes NULL; decimal `median`/`quantile_cont` differ in the last digit;
  `current_schemas`, `current_setting`, `getvariable` and `getenv` still fold; Windows and macOS were not run; the preview CI job is on hold.
- Sync has not been tested on 2.0. (Proxy's DuckDB data source, a test backend, needs no work of its own: W6.) The shared DuckDB sandbox (`Connections.DuckDb`: read-only, no external access, locked
  configuration, allowed directories) depends on setting names and behaviour that 2.0 may have changed.

### 2.5 DbDataSync's libraries, drivers and scripts

There are three separate code-loading mechanisms today, plus one unused copy:

| Mechanism | How it works | Code |
| --- | --- | --- |
| **Library** (an ADO.NET provider) | writes a throwaway SDK project referencing the packages, runs `dotnet publish`, copies the output to `libraries/<id>/lib/`, writes `library.json`; `LibraryRegistry` hooks `AssemblyLoadContext.Default.Resolving` | `DbDataSync.Libraries` |
| **Compiled plugin driver** | the same restore into `drivers/<id>/lib/` plus `driver.json` naming an assembly and an `IDriver` type; loaded in `DriverPluginLoadContext` | `DbDataSync.Drivers.Descriptor` |
| **Script** (row transform, query builder, metadata provider, hooks, ...) | `config/scripts/<name>.cs` + `.yaml` manifest; Roslyn `CSharpCompilation` with an allow-listed reference set, `ScriptSyntaxGuard` as a lint, cached by hash; no package references | `DbDataSync.Scripting` |
| (unused copy) | the library installer, extracted for plan 06 | `DbDataKit.Connections.Libraries` |

Descriptor drivers (`driver.yaml`) are data, not code, and are not affected. JDBC goes through IKVM and stays separate.

## 3. Target shape

```
              ┌──────────────── dbdatakit console (its own host, its own CLI: dbdatakit) ────────────────┐
              │  shell, sign-in, console users, instances, connections view, monitoring, notifications   │
              │  ┌── Sync module ──┐      ┌── Proxy module ──┐      ┌── Build module (+ authoring) ──┐   │
              └──┴────────┬────────┴──────┴─────────┬────────┴──────┴────────────────┬───────────────┴───┘
                          │ management API          │ management API                 │ management API
                          ▼ (HTTPS, console token)  ▼                                ▼ (JSON command surface)
                 dbdatasync server ×N        dbdataproxy server ×N           dbdatabuild server ×N
                 replication engine,         Flight SQL data plane,          many projects per server,
                 TaskRunner, scheduler       x86 worker                      scheduler, monitoring, authoring
                          │                         │                                │
                          └─────────────────────────┴────────────────────────────────┘
                                                    ▼
                DbDataKit/: Config · Secrets · Dialect · Connections (+ FlightSql) · Metadata · Transfer · Hosting
                            Scheduling · Monitoring · Scripting (Roslyn + NuGet) · Cli (command surface, MCP) · Web · State
```

- **The console and the products are separate.** Each product is its own services: a server process (registered as an OS service) with
  its own config directory, its own state store and a management API, and **no web UI of its own**. `dbdatasync server` is today's
  `dbdatasync serve` (DbDataSync.Api) without the SPA it serves now: its UI moves into the console.
- **The dbdatakit console is the one UI**, a separate host built from the shared web packages (W5). It holds the users, a list of
  registered product servers ("instances"), each product's screens, and the views that span products (connections, monitoring,
  notifications). It reaches each server only through that server's management API, so a product keeps running (schedules, replication,
  queries) while the console is down; only the screens are unavailable. One console can manage several servers of each product.
- **A standalone install** is one product's services plus the console with that product registered. The product's MSI includes the console
  as a feature (on by default), and its Docker image is paired with the console image in a compose file (W9).
- **Proxy stays a data-plane process.** The management API is on a separate listener, never on the Flight port or the hot path.
- **Build becomes a server as well as a CLI** (`dbdatabuild server ...`), because its scheduler, monitoring and authoring need something that
  is always running, and one server can host several projects. The server exposes the JSON command surface Build already has through its
  management API; the TUI, MCP and `ui web` are already clients of that surface, so the console adds no second code path to a database. The
  CLI keeps working without the server. Commands run in child processes, which keeps a crash in the native polyglot library out of the server (D12).
- **Shared definitions.** One `connection/1` format, one secret reference syntax, one user and role model, one git-tracked config layout
  (W8): the same format and code in every product, so the console can show and edit them side by side.

**Clean break** (owner decision, applies to every workstream): the new releases start from nothing. No migration, import, upgrade path or
compatibility reader is written for existing configs, scripts, libraries, drivers, secrets, users, sessions or state databases, and no
legacy names are kept (old CLI forms, old environment variable names such as `DBDATA_BUILD_*` and `DBDATASYNC_SECRET_*`). Instead, a server
or CLI that finds a file in an old format (no `schemaVersion`, an older one, `appsettings.json` where `server.yaml` belongs, a `.cs`
script, a `library.json`) **refuses to start or to load it, with a message naming the file and pointing to the documentation**. Release
notes say plainly that an existing install is set up again. Users' own data (their databases and DuckDB files) is not ours to break and
is outside this rule.

## 4. Workstreams

Sizes: S is days, M is one to three weeks, L is longer. The phases in section 5 decide the order.

### W0. Repository foundations (M)

1. **One root build setup.** Root `Directory.Build.props`, `Directory.Packages.props` and `global.json`. The product files import them and
   keep only product-specific settings. Every project uses net10.0, nullable, and warnings as errors (Proxy moves from `false`, fixing what that surfaces).
2. **Central package management for Sync and Build**, with the skew in 2.2 resolved to one version per package (Npgsql 10, SqlClient 7.1.1,
   one DuckDB.NET pin, one Arrow, one YamlDotNet, one Terminal.Gui, one System.CommandLine).
3. **Build adopts `DbDataKitReference`** (copy the Proxy/Sync `Directory.Build.targets`), so the first shared package it takes is a one-line change.
4. **A root `DbDataKit.slnx`** with every project. The per-product solutions become solution filters (`.slnf`).
5. **One reusable CI workflow** (`_product-ci.yml`: changes job, build, test with MTP floors, artifact names) called by the three product
   workflows. One engine set from `testenv/` replaces Build's `scripts/test-engines.sh` and Sync's `docker/` compose files (plan 11).
6. **A root `CLAUDE.md`** saying which folder owns what, the "shared behaviour goes to `DbDataKit/`" rule, and how to build and test the whole
   repository. Move the root `docs/review/` material into the products or delete it (the cleanup that plan 09 deferred).

Exit: `dotnet build DbDataKit.slnx` with warnings as errors; one version per package across the repository; all CI green.

### W1. DuckDB 2.0.0 now (M)

Decided (D6, D7): move to **DuckDB 2.0.0 now**, with the **released engine binary** and the DuckDB.NET packages that carry no engine
(`DuckDB.NET.Data` and `DuckDB.NET.Bindings`, 1.5.6.x), instead of `DuckDB.NET.Data.Full`, which bundles a 1.5 engine. Build's experiments
show DuckDB.NET 1.5.6 runs unchanged on the 2.0 library, because 2.0 keeps the old C API. 1.x is not kept.

1. **One pinned engine.** `duckdb.pin` at the repository root names the 2.0.0 release, the download URL of `libduckdb` for each platform
   (linux-x64, linux-arm64, win-x64, osx-arm64, osx-x64), and the sha256 of each archive and of the library inside it. `scripts/fetch-duckdb.sh`
   and `.ps1` download and check them, the way `DbDataBuild/scripts/polyglot.pin` works. Changing the engine is a one-file change.
2. **Packaging.** A shared MSBuild file (`DbDataKit/build/DuckDb.Native.targets`) puts the library at `runtimes/<rid>/native/` in build,
   test and publish output, so DuckDB.NET's loader finds it, and fails the build with the fetch command when it is missing. Build and Proxy
   publish per platform and bundle it. **Sync** never bundled DuckDB: it installs the `duckdb` library from NuGet the first time its server
   starts (`KnownLibraries`). That install now takes `DuckDB.NET.Data` from NuGet and the engine for the running platform from the pin,
   hash-checked; an offline install points it at a local copy. Docker images and MSIs carry the engine.
3. **Switch, product by product.** The shared packages first (`Connections.DuckDb`, `Metadata`, `Transfer.Conformance`, with the sandbox
   recipe: no external access, locked configuration, allowed directories, read-only), then Sync, then Build. Each switch runs that
   product's whole suite on 2.0.0, with any setting names 2.0 changed fixed in the same slice. **Proxy rides along** with the shared
   packages: no work of its own, its existing tests run on 2.0.0 in the same slice (W6).
4. **Storage format.** 2.0 writes a format that 1.x cannot read. Before the first switch: a test that opens a 1.x file read-only and checks
   that its bytes are unchanged, and an audit of every read-write open in the products, so no user file is converted silently.
   Extensions (Sync's S3 and httpfs sources) install for the engine's version; a test covers one.
5. **Build's open 2.0 items, with the accepted defaults (D8):**
   - ANY/ALL in 2.0's CASE form: refused with a named diagnostic (as today).
   - `%` and `//` by zero: 2.0 raises, and so do SQL Server and PostgreSQL, so the lowering writes the plain operator and drops the
     `NULLIF` it wrote to copy 1.x's NULL. Exact, and simpler.
   - decimal `median`/`quantile_cont`: match 2.0's interpolation if the targets can, otherwise refuse over DECIMAL with a named diagnostic.
   - `current_schemas`, `current_setting`, `getvariable`, `getenv` (still folded by 2.0): refused with a named diagnostic.
6. **2.0 only.** Regenerate Build's committed lowered artifacts and template goldens (their headers carry the DuckDB version), make the
   2.0 runs the normal CI jobs on Linux and Windows, retire `scripts/test-duckdb-preview.sh`, and delete code that existed only for 1.x plans.
7. **When DuckDB.NET 2.x ships:** move to its packages and decide then whether to keep the pin (control over the engine version) or take
   the engine its `.Full` package bundles.

Exit: one DuckDB engine (2.0.0) in every product, from one pin; every suite green on it on Linux and Windows; no 1.x-only code left.

### W2. Libraries, drivers and scripts on Roslyn scripting and NuGet (L)

Goal: **one resolver and one loader** for all code DbDataSync loads at runtime, built on the native Roslyn scripting API
(`Microsoft.CodeAnalysis.CSharp.Scripting`) and the NuGet libraries (`NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Frameworks`,
`NuGet.Versioning`), resolving `#r "nuget: id, version"` in process, so **no .NET SDK is needed at run time**, only the runtime. This replaces
the dotnet-script route this section first proposed: dotnet-script runs `dotnet restore`, which needs the SDK. The design, the spike
that proved it (DuckDB's native library, SQLite, Npgsql, offline, in a runtime-only container) and its risks are in
[14-roslyn-nuget-scripting-spike.md](14-roslyn-nuget-scripting-spike.md). The shared package is `DbDataKit.Scripting`.

| Today | After |
| --- | --- |
| library: throwaway project + `dotnet publish` + `library.json` + a Resolving hook on the default load context | `libraries/<id>/library.csx` holding only `#r "nuget: <id>, <exact version>"` lines; the resolver gives the runtime and native asset paths, cached by hash with a lock file; one `AssemblyLoadContext` per library |
| compiled plugin driver: you build and package an assembly, then `driver.json` names it | `drivers/<id>/driver.csx`: `#r "nuget: ..."`, `#load` for helpers, and a class implementing `IDriver`, compiled by the same host; no packaging step. Descriptor YAML drivers are unchanged |
| script: `config/scripts/<name>.cs` + manifest, no package references | `config/scripts/<name>.csx` + the same manifest. A script may `#r` packages that are on the allowed list or already installed as libraries, and `#load` shared helpers. The allow-listed reference set and `ScriptSyntaxGuard` stay as lints, documented as not a security boundary, as they are today |

Steps:

1. **Spike: done** (section 1 of plan 14; D9 decided). A Windows pass of the spike comes before S02.
2. **`DbDataKit.Scripting`**: a resolver, a compiler, a loader and diagnostics (slices S02-S04). All package input goes through
   `Connections.Libraries.Validation` (the H1 corpus: id, exact version, source, path safety). Package source mapping restricts feeds.
   Unload is best effort and native libraries are never unloaded; where that matters, code runs in a child process (plan 14, section 7).
3. **Port Sync** onto it in three PRs (libraries, drivers, scripts), adapter first. The web pages (Libraries, Drivers, script editor with
   Monaco) keep working. Add `dbdatasync script test <name>` and editor support: generate a stub so VS Code's C# tooling resolves
   `#r` to the scripting abstractions.
4. **Clean break.** No conversion: a `.cs` script, a `library.json` or a `driver.json` found in a config repository is refused with a
   message naming it (section 3, "Clean break").
5. **Delete** `DbDataSync.Libraries`, the plugin loading in `Drivers.Descriptor`, `ScriptCompiler`'s compilation path, and
   `DbDataKit.Connections.Libraries`' installer (its `Validation` moves into `DbDataKit.Scripting`).
6. **Reuse elsewhere.** Proxy can offer "any ADO.NET provider" data sources through the same library catalog and the generic composer
   in `Connections`; because Proxy loads no code today, the first version runs them in a child process or not at all. Build hooks could
   accept `.csx` later. Both are optional.

Exit: one way to restore and load code at runtime; old-format files are refused with a message; the web pages work as before; no SDK on the machine that runs it.

### W3. Shared libraries in Sync and Build (L, ongoing)

Plan 10's rules apply: adapter first, delete second, one concern per PR, existing tests unchanged.

**DbDataSync** (order: what the console and W2 need first):

1. `Config.*` replaces `Core/Config`'s YAML handling: strict parsing and a required `schemaVersion`; a file without one, or with an older
   one, is refused (clean break). The migration framework stays available in `Config` for later versions, but none is written now.
2. `Secrets.*` replaces `ClrKernel.Core.Secrets`, on the encrypted store of W13. Nothing is imported from the OS keychain (clean break);
   ClrKernel is removed.
3. `Dialect.*` replaces `Core/Sql` (quoting, lexer, type map). Sync's `IDriver` stays: it is the replication contract, not the connection.
4. `Connections.*` composes every driver's connection string. Failures go through the shared classifier. Sync's DuckDB source driver
   opens through `Connections.DuckDb`, which gives it the read-only sandbox.
5. `Metadata.*` for catalog reads (the readers exist for SQL Server, Postgres, DuckDB, ODBC; MySQL and Oracle readers are added to the shared package).
6. `Transfer.SqlServer` / `Transfer.Postgres` bulk writers and the strategy builders replace the hand-written staging paths, measured with the benchmark stub first.

**DbDataBuild** (nothing adopted yet):

1. `Dialect.*` behind `DialectProfile` (keep Build's matrix and rules; the shared package gets Build's fixtures as conformance cases).
2. `Connections.*` replaces `Logins`, `OdbcLogin`, `FlightSqlLogin`, `DriverFailure`. Logins come from `DBDATA_BUILD_<CONN>_<READ|WRITE>`;
   the old `DBDATA_BUILD_*` names are dropped, here and in the shared resolver (clean break). Build's rule "driver messages are never echoed" becomes a shared-classifier test.
3. `Metadata.*` replaces `CatalogReader`, `SourceCatalogReader`, `OdbcCatalogClient`. `ServerFacts` becomes the shared record (plan 04's open question).
4. `Transfer.*` replaces `Transfer.cs`, `MutationGate` and `StatementLog`. The shared `WriteGate` was modelled on Build's gate, so
   `GateInvariantTests` moves to the shared package and runs against both.

**New shared package: `DbDataKit.Connections.FlightSql`**, a Flight SQL client. It replaces Sync's `Drivers.ArrowFlightSql` connection code
(the driver keeps only its readers) and Build's `FlightSqlRowSource`. CI tests it against **DbDataProxy itself**: the suite's own server
becomes an end-to-end test of the suite's own client.

**Unused shared packages:** each is adopted by the end of this workstream or deleted: `Transfer.*` (adopted above), `Update.*` (shelved, not deleted: W9),
`Connections.Libraries` (folded into W2), `Connections.Descriptor` (Sync's descriptor reader moves onto it or it goes),
`Connections.Postgres` (Sync and Build use it), `Dialect.Arrow` (Proxy's C7 benchmark decides), `Secrets.Keyring` (deleted: W13), `Secrets.Cli` (W4, W13).

Exit: no product has its own dialect, connection composer, catalog reader, secret resolver, or YAML loader; no shared package is unused.

### W4. One command surface; one grammar for four CLIs (M)

**4a. The command contract.** Generalize Build's command contract into `DbDataKit.Cli` (alongside `Hosting.Cli`): each command has a spec
with an effect class (reads, writes files, writes a database), a `--format json` document with a closed schema, and generated help. TUI
catalog entries, MCP tools and the management APIs of W6, W7 and the console come from the same specs. Sync and Proxy commands adopt it;
System.CommandLine everywhere (Build already uses it). The result is JSON output everywhere and the same exit codes across products.

**4b. Executables and names (decided, D3).**

- **Each product keeps its own CLI.** **`dbdatakit` is reserved for managing and configuring the shared console**; it does not forward
  to the product CLIs.
- **Executables are lower case:** `dbdatasync`, `dbdataproxy` (was `DbDataProxy`), `dbdataproxy-worker` (was `DbDataProxy.Worker`),
  `dbdatabuild`, `dbdatakit`. Windows service names and display names follow (the SCM compares names without case, so existing registrations
  are updated by `service install`, which is already idempotent).
- **Build's `<noun> <verb>` grammar everywhere.** Nouns are grouped by what a command touches, as in Build. Breaking changes are fine,
  so old forms are removed, not aliased; release notes carry the table below.
- **Shared nouns, identical in every CLI where they apply** (they come from `DbDataKit.Cli`, so the code exists once):

| Noun | Verbs | Meaning |
| --- | --- | --- |
| `server` | `run`, `pair`, `unpair`, `consoles` (plus product verbs) | the long-running process in the foreground (what the OS service runs), and its pairings with consoles (W12) |
| `service` | `install`, `uninstall`, `status`, `start`, `stop`, `restart`, `doctor` | registration of `server run` with Windows or systemd |
| `cert` | `show`, `ensure`, `check`, `renew` (plus product verbs) | the server's TLS certificate |
| `config` | `check`, `show`, `get`, `set`, `schema` | the server's own configuration (no `migrate`: clean break) |
| `secret` | `protect`, `hash`, `check` | secret references and protected values |
| `user` | `invite`, `list`, `role` | people who sign in: **`dbdatakit` only**, since users live in the console (W8) |
| `health` | `check` | is a running server answering (what container health checks run) |
| `ui` | `terminal`, `mcp` | the local interfaces |
| `help` | `code`, ... | explanations |

`server` (not `service`) is the noun for the long-running process, because `service` is already the OS-registration noun in all
products; it also leaves room for a Build server that hosts several projects (`dbdatabuild server project add`).

What changes in each CLI:

| Today | After | Notes |
| --- | --- | --- |
| `dbdatasync serve` | `dbdatasync server run` | |
| `dbdatasync setup` | `dbdatasync server setup` | the interactive first-run walk-through |
| `dbdatasync config cert ...` | `dbdatasync cert ...` | Sync's registered verbs (`use-pem`, `enroll`, `bind`, ...) stay on `cert` |
| `dbdatasync config secret ...` | `dbdatasync secret ...` | |
| `dbdatasync config library ...`, `config driver ...` | `dbdatasync library ...`, `dbdatasync driver ...` | `script` joins them (W2: `script test`) |
| `dbdatasync invite` | `dbdatakit user invite` | users move to the console (W8) |
| `dbdatasync health` | `dbdatasync health check` | |
| `dbdatasync update ...`, `launcher repair`, `tool install` | unchanged for now | the updater is shelved (D5); renamed when it is reopened |
| `DbDataProxy` (no verb runs the server), `DbDataProxy run` | `dbdataproxy server run` | no default command any more; the service's command line and `dist.json` change with it |
| `DbDataProxy install`, `uninstall` | removed | `service install` / `service uninstall` (kept today only for `install.ps1`) |
| `DbDataProxy hash-password`, `protect-secret` | `dbdataproxy secret hash`, `secret protect` | |
| `DbDataProxy query` | `dbdataproxy query run` | |
| `dbdatabuild ui web` | removed | the console replaces it; `dbdatabuild server run --project .` (loopback by default) with a local console is the old local use |
| (new) | `dbdatabuild server project add`, `remove`, `list` | projects hosted by one server |
| (new) | `dbdatakit server run`, `server setup`, `instance add`, `remove`, `list`, `user ...`, `cert ...`, `config ...`, `service ...` | the console only: `instance add` pairs a product server with it (W12) |

Build's own nouns (`project`, `connection`) are unchanged. Sync gains nouns for what its CLI cannot do yet (`connection`, `replication`, `run`)
when its CLI and API parity work (`DbDataSync/architecture/planning/todo/cli-setup-and-api-parity.md`) is done, in the same grammar.

Exit: every command in the suite has a JSON form and a schema; the four CLIs share the nouns above; the renames are done and documented.

### W5. Shared web platform (L)

Extract first, extend second: Sync's UI must look and work the same after the extraction. The front-end has one host, the dbdatakit
console. The back-end packages are used by the console (users, sign-in, pages) and, for their management APIs, by the product servers.

**Back-end: `DbDataKit.Web` and `DbDataKit.State`**, taken out of `DbDataSync.Api` and `DbDataSync.State`. The user-facing parts
(sign-in, users, sessions, invites, notifications) end up in the console; the parts every server needs (management API hosting, token
validation, `PlainHttpProtection`, health, certificates) are used by each product server:

- auth: the session handler, passkeys (Fido2), Windows Negotiate sign-in, bootstrap invite, policies and roles;
- stores: users, sessions, invites, notifications, audit, on the state database's existing SQLite / SQL Server / PostgreSQL dialects;
- hosting: `PlainHttpProtection` (already shared), HTTP.sys hosting, web root location, the SignalR hub base;
- admin endpoints: certificates, updates, config history (Sync's `GitCommitService` becomes `DbDataKit.Config.Git`), restart, about,
  health, metrics, docs viewer, files.

**Front-end: an npm workspace at `DbDataKit/web/`**, made from `DbDataSync.Web`:

| Package | Contents |
| --- | --- |
| `@dbdatakit/shell` | `AppShell`, `SignIn`, `AdminTabs`, `NotificationBell`, `ErrorBanner`, `Field`, `KeyValueTable`, `CodeEditor` (Monaco), `DiffViewer`, `Markdown`, `StatusBadge`, `PreviewGrid`, the API client and hooks, WebAuthn |
| `@dbdatakit/admin` | the shared pages: users and invites, certificates, updates, config history, connections catalog (W8), libraries and drivers (W2), files, docs |
| `@dbdatakit/sync` | replications, mappings, runs, monitoring: the Sync pages that are Sync's own |
| `@dbdatakit/proxy` | W6 |
| `@dbdatakit/build` | W7 and W11 |
| `@dbdatakit/console` | the console app: instances, cross-product views, and the modules talking to remote servers |

One Vite build, served only by the console. Each module is written against its product's management API and shows the servers of that
product registered with the console. vitest and Playwright are set up once. Sync's update page moves into `@dbdatakit/admin` as it is (the updater is shelved, D5): Sync-only, unchanged.

Two steps, each with its own exit:

1. **Extraction.** Sync's UI is built from the workspace and still served by Sync's server, with no visible change (Playwright suite and
   screenshots unchanged); `@dbdatakit/shell` and `@dbdatakit/admin` import nothing from `@dbdatakit/sync`.
2. **The console host.** `dbdatakit server run` serves the workspace build; Sync's pages work through the console against a registered
   `dbdatasync server`, which then stops serving the SPA. Users start fresh in the console (first-run invite); nothing is copied from
   Sync's state database (clean break). The same Playwright suite passes against the console.

### W6. DbDataProxy management (M-L)

Today Proxy is configured by editing `appsettings.json` and running CLI verbs. It has no runtime admin interface.

0. **DuckDB stays as it is**, with no work of its own for 2.0 (owner decision: it was a test backend for early development, not a
   product feature). Proxy opens DuckDB through `Connections.DuckDb`, so it moves to the 2.0.0 engine when that package does (W1);
   its existing tests run on 2.0 as part of that switch. Removing it later is optional and not scheduled.
1. **Management API** on a separate listener, never the Flight port, secured as every product's is (W12): HTTPS, console tokens, the
   local CLI over a local channel. Never on the hot path. Endpoints: health, version and drain (plan 03's seams); effective config,
   redacted, with origins; validation; data sources (CRUD and test connection); Proxy's client identities (Flight users, Windows accounts, JWT mappings, pass-through);
   live sessions and running queries (with cancel); concurrency gates and limits; login throttle state; certificate status and renewal;
   recent classified failures.
2. **Config moves to YAML in a git-tracked directory** (D2, which reverses plan 10's D2). Each data source becomes a `connection/1`
   definition with a `proxy:` extension (classifier mode, limits, bitness, pass-through); users are a separate file; secrets are references.
   Writes go through `Config.Yaml`'s comment-preserving editor and are applied by hot reload (Proxy already reads options through
   `IOptionsMonitor`). An existing `appsettings.json` is refused with a message (clean break), and Proxy's JSON config migrations are deleted.
3. **Proxy module `@dbdatakit/proxy`** in the console (which can manage several proxies): Overview (health, version, sessions, throughput); Data sources
   (catalog-backed editor, classifier mode, limits, test); Users and auth; TLS (the shared certificate page); Activity (queries and
   failures); Config (diff and history); Service (doctor).

Exit: everything in today's `appsettings.example.json` can be edited from the console with validation; adding or changing a data source or
user needs no restart; a server that finds an old `appsettings.json` refuses to start and says why (test).

### W7. DbDataBuild server and management (M-L)

Today Build has a CLI, a TUI, an MCP server and app, and `ui web`. All are clients of the JSON command surface. Refresh plans already
carry `expected_every`, `on_busy`, `on_failure`, `parallel` and `max_runtime`, and `connection status` / `connection monitor` read the
run history from the tracking tables; what is missing is something that starts runs on time and watches them (W10).

1. **`dbdatabuild server`**: a long-running server hosting **several projects** (`server project add|remove|list`), registered as an OS
   service with the shared `service` noun. It runs the built-in scheduler (W10) for its projects, keeps the monitoring view current, hosts
   authoring workspaces (W11), and serves a management API that exposes the JSON command surface (W4a) per project. It has no web UI of
   its own; its state (schedules' last runs, workspaces, approvals) is in a SQLite state store by default. Commands run in child processes (D12).
   The CLI works without the server exactly as today; a person running `connection refresh` by hand and the scheduler share the per-load
   lock and `on_busy` that already exist.
2. **Build module `@dbdatakit/build`** in the console, a client of that management API: projects; compile
   and diagnostics (with `help code` text); graph and lineage (Mermaid already exists); connection status and drift; **plans: review,
   approve, apply**, with the approver's identity and time recorded in the plan's events (something the CLI cannot give); schedules and
   monitoring (W10); compare; sample; authoring (W11).
3. **Logins from secret references.** The server maps secret references into each command's environment
   (`DBDATA_<APP>_<CONN>_<READ|WRITE>`), so Build's "no fallback from read to write" rule holds unchanged.
4. **Retire `ui web`** (`Web/WebServer.cs`, `WebBackend.cs`, `WebActions.cs`, `index.html`): the console replaces it (for the old local
   use: `dbdatabuild server run --project .` and a console on the same machine). The TUI stays; the MCP
   server moves to the shared surface (W4a).

Exit: a person can take a project from compile to an approved, applied plan and a scheduled, monitored refresh in the console, and the
schedule runs with the console switched off.

### W8. Shared definitions: connections, secrets, identity, config layout (M-L)

Each product server keeps its own definitions (it must run without the console and without the other products); what is shared is the
**format and the code**, so the console can show and edit them side by side.

1. **One connection format.** `connection/1` (`DbDataKit.Connections`) is the on-disk format for Sync connections, Proxy data sources and
   Build connections. Product-specific fields live under `extensions.<product>`. The console's Connections view lists the connections of
   every registered server and can copy a definition from one to another (a Sync source that Proxy should also serve, for example). Build
   projects keep their own connection files (they are committed by the author).
2. **One secret syntax and resolver** (`env:`, `file:`, `secret:`, `dpapi:`) in every product.
3. **One identity model and implementation.** Users (passkeys, Windows) with roles: Admin, Operator, Viewer, Author (W11) and Approver
   (Build plans). **Users live in the consoles**, the only place people sign in. Product servers have no user accounts: they accept a
   paired console's token for their management API, which names the user and their roles, so each server still enforces roles and records
   who did what (W12). Proxy's client identities (its Flight users,
   Windows accounts, JWT mappings) stay Proxy's data-plane auth. One PBKDF2 hasher (plan 10 C3).
4. **One config layout**, git-tracked with auto-commit and the history UI (Sync's `GitCommitService` becomes `DbDataKit.Config.Git`), the
   same in every product's data directory:

```
<data>/config/
  connections/*.yaml            this server's connections (connection/1)
  server.yaml                   the server's own settings (listen, TLS, state store, the consoles it is paired with)
  sync/        (Sync)           replications, scripts (*.csx), drivers, libraries
  proxy/       (Proxy)          data source policy, client users, JWT mappings
  build/       (Build)          projects.yaml: the hosted projects (the projects themselves stay in their own repositories)
  console/     (dbdatakit)      instances.yaml: the registered servers; users are in the console's state database
```

Sync's host settings move from `appsettings.json` to `server.yaml` with the rest (D2); an old `appsettings.json` is refused (clean break).

Exit: the same connection, secret and config code in every product and the console; one user list, in the console; the console shows the
connections of all registered servers.

### W9. Distribution and releases (M)

1. **One suite release** (D4) from one workflow, gated on green product CI and `dbdatakit-ci.yml` for that exact commit, shipped through
   the channels that already exist: **Docker images** and **the MSI** (`dist/msi.py`), plus the zips, `install.ps1`/`install.sh` and
   `SHA256SUMS`. Every product stays a **standalone install** (D4): its own image (`dbdatasync`, `dbdataproxy`, `dbdatabuild`), MSI and zip.
   A product's MSI includes the console as a feature (on by default) and pairs the product with it (W12); each product image ships with a
   compose file that pairs it with the `dbdatakit` console image. The suite adds a `dbdatakit` MSI with one feature per product plus the console. All of it
   comes from the same workflow and the same commit (`dist.json` already templates the per-product parts, so this costs little); the suite
   tag is `dbdatakit/v<calver>`, and the product tags stay as they are.
2. **The updater is shelved** (owner decision, D5). No update work until the products have settled and unified. Sync's `update` command,
   `Updates` and `Launcher` keep working as they are; `DbDataKit.Update.*` stays in the repository unused and is neither adopted nor deleted
   by this plan. When the topic is reopened, the choice between the two is still the one to make.
3. **NuGet** stays paused (plan 09). When it comes back, decide whether the suite ships as a dotnet tool.

Exit: one release workflow producing every image and MSI (per product and suite). Each product's release workflow is deleted or reduced to a call into the suite's.

### W10. Shared scheduling and monitoring (M-L)

Build is getting a built-in scheduler and monitoring, and Sync already has both. Writing Build's from scratch would make a third copy of
the hardest part of each product, so the reusable core moves to `DbDataKit/` first and Build is its second consumer.

What exists:

| | DbDataSync | DbDataBuild |
| --- | --- | --- |
| When to run | `SchedulerService`, `SchedulingEvaluator`, `TaskScheduling` (Cronos), per replication | nothing time-based: something outside (cron, Task Scheduler) runs `connection refresh <plan>` |
| Queue and locks | `WorkQueueStore` (lease, collision), `RunLockStore`, the TaskRunner's worker lanes | per-load lock, `on_busy` (skip, wait), `parallel`, `max_parallel` per connection, inside one run |
| Run records | `TaskRunStore`, `RunMetricsStore`, run history paging, `RunPruningService` | the tracking tables on a connection (events, rows and duration per load), read by `status` and `monitor` |
| Health rules | lag monitoring (`ReaderLagService`), run monitor | `expected_every` gives `OVERDUE` and `SLOW`; `max_runtime`; outcomes `completed_with_failures`, `stopped` |
| Telling a person | `NotificationStore`, the notification bell | none (exit codes and the views) |
| Retention | `RunPruningService` | none: plan files and statement logs pile up (OPEN-ITEMS D0) |

Design:

1. **`DbDataKit.Scheduling`**: schedule definitions (cron with time zone, interval, calendar windows, paused, misfire policy: run once,
   skip), the evaluator (next due time, what was missed while the server was down), the work queue with leases and the run lock, and
   overlap policies that map onto Build's `on_busy` and Sync's existing behaviour. It holds no product logic: a job is "run this command
   with these inputs", and the product decides what that means. Taken from Sync's code, with its tests.
2. **`DbDataKit.Monitoring`**: a neutral run model (job, run, outcome, start, duration, rows, the item that failed, a classified failure
   from `Connections`), health rules over it (overdue and slow from an expected interval, failed, lag), notifications with
   channels (the console's notification bell first; email or webhook later; each server keeps its notifications until the console reads them), and retention. Each product supplies its run history through an
   interface: Sync from its state database, Build from its tracking tables, which stay where they are (Build's run history travels with
   the data by design and is not copied into the console's state database).
3. **Schedules are data in Build projects.** In keeping with Build's rule that everything is a file a person can inspect and commit, a
   refresh plan declares its schedule next to `expected_every` (for example `schedule: "15 * * * *"`, `time_zone:`). The Build server reads them
   from the registered projects; `project show refresh` shows them; the console shows and can pause them, and a change is a commit to the
   project. A schedule the operator adds only at the server (not in the project) is the open question in D13.
4. **Sync moves onto the shared packages** behind its current behaviour (its tests unchanged), which proves them before Build depends on them.
5. **One monitoring view in the console**, across servers, for both products: runs, outcomes, overdue and slow, lag, failures, notifications; the Build and
   Sync pages are filters of it. Proxy can feed the same model later (failed logins, refused statements, limits reached).
6. **Build's open items it closes:** time-based scheduling; the warnings in `status` and `monitor` are evaluated continuously instead of when
   someone looks; retention of plan files and statement logs from scheduled runs (OPEN-ITEMS D0) becomes a retention policy.

Exit: a Build refresh plan runs on its declared schedule from the Build server, its overdue and failed runs reach a person, and Sync's
scheduler and run monitor run on the same packages with no behaviour change.

### W11. Authoring DbDataBuild projects in the web interface (L)

Goal: a person can create and change a Build project in the console with the same checks
the CLI gives, and take it through commit, plan, approval and apply.

What it builds on: every authoring step already exists as a command with a JSON document: `project create` (templates),
`project model create` / `project model update` (definitions from the query, with questions and an answers schema), `project compile
--content` (the lowered and rendered text per target, with diagnostics), `project sample` (rows on generated data), `project tests run`,
`project show graph`, `project import` (mapped models from a connection, as a diff), and the JSON Schemas in `DbDataBuild/schemas/`.

1. **Workspaces.** Projects are git repositories. Authoring happens in a **per-user workspace**, a git worktree on a branch, never in the
   checkout the scheduler runs from: schedules and deploys run from a pinned ref (the deploy branch or the deployed commit), so a draft
   cannot change what runs. Workspaces have an owner, are listed, and are cleaned up when merged or abandoned.
2. **File commands in the JSON surface.** To keep Build's rule that every interface is a client of the command surface, add
   `project file list|read|write|move|delete`, confined to the project tree, with a content hash for optimistic concurrency (a write against
   a stale hash is refused). The console never writes files any other way, and agents (MCP, `project agent-kit`) get the same commands.
   Anything these commands parse falls under the existing seeded mutation tests (`FuzzTests`).
3. **Editors.** Model SQL in Monaco (the shared `CodeEditor`) with DuckDB highlighting and diagnostics as markers; model, folder,
   connection, refresh plan, test and seed files as forms generated from `schemas/*.json`, with a YAML tab (comment-preserving, through
   `Config.Yaml`); the questions of `project model update` as a form whose answers are the answers file.
4. **Live feedback.** On a pause in typing, compile the changed model and show the lowered and rendered text per target side by side
   (`DiffViewer`), the diagnostics with their `help code` text, sample rows on generated data (`PreviewGrid`), affected tests, and the graph
   around the model. A **long-lived child process per workspace** keeps DuckDB and the parser warm so this is fast (D12); it is still a child
   process, so a native crash loses only that workspace's session.
5. **Flows.** New project from a template; new model; import mapped models from a connection (diff, then write); seeds; tests.
6. **Git.** Diff, commit, history and branch for a workspace through `DbDataKit.Config.Git`; push to the project's remote and open a pull
   request where one is configured; merge into the deploy branch inside the server otherwise. Conflicts are shown and refused, never
   resolved silently.
7. **From draft to database.** Commit, merge, `connection deploy --write-plan`, review and approve (W7, Approver role), apply. The `Author`
   role can edit and commit; it cannot approve or apply.

Exit: a new project can be created from a template, a model added and checked against every target, committed, planned, approved and
applied, all from the console, and the same files and checks result as from the CLI.

### W12. Console and server: trust, authentication and authorization (M)

The console and the servers may run on the same machine (a standalone install) or on different ones (one console managing many servers,
or containers on a shared network). Nothing in this design depends on which.

**Rules**

1. **The console calls the server; the server never calls the console.** A server needs no credentials for any console, keeps running
   when every console is gone, and monitoring, notifications and run history are pulled by the console. A server that a console cannot
   reach inbound (behind a firewall) is out of scope; a later option is the server dialling out to the console.
2. **A server can be paired with several consoles** (for example a team console and an operations console, or a standby). Each pairing
   is independent: its own key, its own role ceiling, revoked on its own.
3. **The server is the security boundary.** A console hides what a user may not do, but the server checks every call itself and never
   trusts the UI.

**Transport.** The management API is always HTTPS on its own listener (on Proxy, never the Flight port). With the console installed on
the same machine it binds to loopback; for remote consoles the bind address is set at install or with `config set`, and a startup warning
names an address that is not loopback and not covered by TLS. The certificate comes from the shared certificate code (W3, plan 12);
self-signed is fine because the console pins its fingerprint at pairing.

**Pairing** (the same verbs in every product):

1. On the server's machine an administrator runs `<product> server pair [--max-role <role>] [--name <label>]`. It prints a single-use
   code that expires after a few minutes; needing local administrator rights is the proof of ownership. For a container the code can come
   from an environment variable at first start.
2. In a console an administrator runs `dbdatakit instance add <url> --code <code>` (or uses the console's screen).
3. The exchange gives the console the server's certificate fingerprint and instance id, and gives the server the console's id, its name
   and its public token-signing key (ed25519). The server adds an entry to the list of paired consoles in `server.yaml`; pairing again
   with another console adds another entry.
4. `--max-role` caps what any user of that console can do on this server (a read-only operations console, for example). A token claiming
   more is refused.
5. `<product> server consoles` lists the pairings (name, id, key fingerprint, ceiling, when last used); `server unpair <console>` removes
   one, effective at once. A console rotates its signing key by sending the new key signed with the old one, so its servers need no re-pairing.

**Authentication on every call.** The console signs a short-lived token (about 5 minutes) with: issuer (console id), audience (the
server's instance id), subject (the user's id and name, or the console itself for background polling), the user's roles on that server
(and, for Build, per project), a unique id and an expiry. The server checks the signature against the key of the console named as issuer,
the audience, the expiry, the ceiling, and that the id has not been used before. Tokens are headers, not cookies, so they work through
reverse proxies. mTLS is optional extra hardening later, not a requirement.

**Authorization.** Roles are assigned in each console, per user and per server (and per Build project). The role a call needs follows
from the command's effect class (W4a):

| Call | Role |
| --- | --- |
| reads (status, config, runs, catalog) | Viewer |
| operations (run now, pause a schedule, cancel a query, restart) | Operator |
| configuration changes (connections, data sources, replications, schedules, certificates) | Admin |
| project file writes and commits (W11) | Author |
| approving and applying a Build plan | Approver |

The effective role is the lower of the user's role in the token and the console's ceiling. A long operation (applying a deploy) is
authorized when it starts and runs to the end under that user.

**Several consoles at once.**

- **Concurrent edits.** Every write carries the version it was based on (the config file's hash, the `project file` content hash of W11);
  a write against a stale version is refused with the current one, never merged silently.
- **Audit.** The server records the console id and the user for every change: in its audit log, and as the author of the git commit in
  its config directory (`user name <console name>`). A Build plan approval records the approver and the console.
- **Users are per console.** Consoles do not share users or role assignments with each other; the same person can have different roles
  in two consoles, bounded on each server by that console's ceiling. Single sign-on across consoles is out of scope.
- **Notifications** are kept on the server; each console tracks what it has read.

**Secrets.** No console holds database credentials. Each server resolves its own secret references and redacts its responses; a secret
typed into a console is sent once to the server, which stores it in its own secret store, and the console keeps only the reference.

**The local CLI.** On the server's own machine the product CLI talks to the running server over a named pipe (Windows) or Unix socket,
restricted by the OS to administrators, root or the service's group. Work on the machine never needs a console, and nothing of it is on
the network. `server pair` and `server unpair` work this way.

**Consoles' own sign-in** is Sync's today, moved into the console: passkeys, Windows sign-in, invites, sessions, HTTPS, `PlainHttpProtection`.

**Shared code.** All of it is in `DbDataKit.Web` (token issuing in the console; validation, ceilings, replay check and the role check in
each server) and `DbDataKit.Hosting` (the local channel, pairing codes), so the four hosts behave the same.

Exit: a console on another machine pairs with a server of each product and manages it; a second console pairs with the same server with a
lower ceiling and is refused above it; unpairing one console leaves the other working; every change on the server names its user and console.

### W13. The shared secret store (M)

Decided (D10): finish the shared secrets code and **do not use ClrKernel** at all; nothing is imported from it (clean break). Secrets are stored **encrypted in a YAML file**; the encryption key is protected by **DPAPI on Windows** and by a
**permission-restricted file** on Linux and macOS. It works the same for a service, a CLI and a container, and needs no OS keychain (which
is per user and does not suit services).

What exists in `DbDataKit/`: reference parsing (`secret:`, `env:`, `file:`, `dpapi:`), the resolver chain, `env:` and `file:` providers
(including systemd's `$CREDENTIALS_DIRECTORY` and Docker's `/run/secrets`), the redactor, `SecretValue` (zeroed on dispose), the DPAPI
protector and store (one file per secret, Windows only), `AclHelper` (Windows ACLs, Unix 0600/0700), the PBKDF2 hasher. Missing: a store
that works on every platform, key management, and the commands.

**Design**

| | |
| --- | --- |
| Store | `<data>/secrets/secrets.yaml`: `name -> { v: 1, key: <key id>, value: <base64 nonce + ciphertext + tag> }`. Outside the git-tracked config directory, so no secret, even encrypted, is in the history. Config files hold only references (`secret:dbdata:proxy:datasource:erp:password`) |
| Cipher | AES-256-GCM (.NET's `AesGcm`), a random 96-bit nonce per write, the secret's **name as associated data**, so a value copied to another name fails to decrypt |
| Key | a random 256-bit data key with an id, in a key ring (`<data>/secrets/keys/<id>.key`) so rotation can keep the old key until every value is re-encrypted |
| Key at rest, Windows | DPAPI, LocalMachine scope with per-product entropy, file ACL to the service account, SYSTEM and Administrators (`DpapiProtector`, `AclHelper`) |
| Key at rest, Linux and macOS | the raw key in a 0600 file owned by the service user, in a 0700 directory; **the server refuses to start if the key file or directory is readable by anyone else** (as ssh does with keys) |
| Key from outside | `secrets.keyFile` in `server.yaml` (or `DBDATA_SECRETS_KEY_FILE`) points at a key provided by the platform: a Docker or Kubernetes secret, a systemd `LoadCredentialEncrypted` credential. Images never contain a key; a container without one generates it on first start only into a mounted volume, and says so |
| Writes | atomic (write a temporary file, then rename), the file restricted before content is written (`AclHelper.WriteAllBytesRestricted`) |
| Reading | `EncryptedFileSecretStore : ISecretStore, ISecretProvider` answers `secret:` references in the resolver chain, ahead of `env:`/`file:`; inline `dpapi:` values (`secret protect`, Windows) resolve too. `LegacyEnvProvider` (the old `DBDATA_BUILD_*` and `DBDATASYNC_SECRET_*` names) is deleted |
| Rotation | `secret rotate-key`: a new key, every value re-encrypted, the old key deleted after the last value is rewritten; interrupted rotation resumes |
| Moving a server | `secret export --to <file>` / `secret import <file>`: values re-encrypted under a passphrase (PBKDF2-SHA256, 600,000 iterations, the existing hasher's primitive). The key file itself is never copied by a command |
| Commands | the shared `secret` noun (W4): `set <name>` (value from a prompt or stdin, never an argument), `list` (names and dates, never values), `delete`, `check` (every reference in the config resolves), `rotate-key`, `export`, `import`, `protect` (an inline value for a single field, where a store is not wanted). There is no command that prints a value |
| Console | a secret typed in the console is sent once to the server's management API, which stores it; the console keeps only the reference (W12) |
| Backup | the documentation says plainly: back up the key file (or the platform's key) separately from `secrets.yaml`; either one alone is useless |

Exit: the store works on Windows, Linux and macOS (tests on each CI runner), all three products and the console resolve `secret:` through
it, and `ClrKernel.Core.Secrets`, `LegacyEnvProvider` and the `Secrets.Keyring` stub are gone.

## 5. Phases

```
P0 foundations ─► P1 engines and loaders ─► P2 console ─────► P3 Proxy and Build ─► P4 authoring, release ─► P5 retire
  W0                W2 (Roslyn + NuGet)       W5 back-end      W6 Proxy API/module    W11 authoring            ui web,
  W1 (DuckDB 2.0.0) W3 Config/Secrets/        W5 console host  W7 Build server/module W9 suite release         dead shared
  W13 secret store  Dialect in Sync, Build    (Sync's UI moves W10 Build schedules                             packages
  W2.1 spike        W5 front-end extraction   into it)         W3 Metadata/Transfer
                                              W8, W4, W10 extract
                                              W3 Connections
```

| Phase | Contents | Exit |
| --- | --- | --- |
| **P0** (now) | W0; **W1 (DuckDB 2.0.0, all of it)**; **W13 (the secret store)**; W2 spike | one build setup and one version per package; every product on DuckDB 2.0.0; the encrypted secret store on every platform; a spike report |
| **P1** | W2; W3 Config, Secrets (Sync onto W13, ClrKernel removed), Dialect in Sync and Dialect, Connections in Build; W5 front-end extraction | one code loader; Sync's UI built from the workspace; no ClrKernel |
| **P2** | W5 back-end and step 2 (the dbdatakit console host and `dbdatakit` CLI; Sync's UI and users move into it); W12 (pairing, tokens, roles, the local channel; needed before Sync's UI can leave its server); W8; W4 (contract and the renames of 4b); W10 steps 1, 2 and 4 (extract from Sync, Sync on the shared packages); W3 Connections in Sync and Metadata in both; `Connections.FlightSql` | Sync is managed through the console and its server serves no UI; one connection, secret, user and config implementation; the new command names; Sync's scheduler runs on `DbDataKit.Scheduling` |
| **P3** | W6 (Proxy management API and console module); W7 (the Build server and console module); W10 steps 3, 5 and 6 (Build schedules, monitoring); W3 Transfer | all three products are managed from the console; Build refreshes run on schedule and are monitored |
| **P4** | W11; W9 | Build projects are authored in the console; the first suite release, and product MSIs and images that include or pair with the console |
| **P5** | `ui web`, and any shared package still unused (except `Update.*`, shelved) | nothing duplicated remains |

The workstreams run in parallel where they touch different code: W1 and W2 are independent of each other; W5's front-end extraction does not
wait for W3; W6 and W7 need W5 and W8 but not each other; W11 needs W7's server and can start its file commands (step 2) earlier; W10's extraction from Sync can start as soon as W0 is done, and must be
finished before Build's scheduler is built, so that Build's scheduler is the second user of the shared code rather than a third copy.

## 6. Code to remove

These are rough line counts of today's code that this plan replaces. They show where the reduction comes from; they are not a promise.

| Today | Lines | Replaced by | Workstream |
| --- | --- | --- | --- |
| Sync `Core/Sql` (dialects, quoting) | ~2,000 | `Dialect.*` | W3 |
| Sync `Core/Config` YAML handling (part of ~4,900) | part | `Config.*` | W3 |
| Sync `Libraries` + plugin loading in `Drivers.Descriptor` + `ScriptCompiler` path + `Connections.Libraries` installer | ~2,000 | `DbDataKit.Scripting` | W2 |
| Sync `Drivers.ArrowFlightSql` connection code + Build `FlightSqlLogin`/`FlightSqlRowSource` | part of ~2,300 | `Connections.FlightSql` | W3 |
| Build `Execution` logins, failures, catalog readers, gate, statement log, transfer (of ~4,400) | most | `Connections`, `Metadata`, `Transfer` | W3 |
| Build `ui web` (server + 783-line page) | ~1,270 | console module | W7 |
| Proxy `Auth` (~590) and Sync `Api/Auth` (~710), user and session stores | part | `DbDataKit.Web`, `DbDataKit.State` | W5, W8 |
| Three CLI parsers, their help and JSON conventions, and four copies of `service`/`cert`/`config`/`secret`/`user` verbs | part | `DbDataKit.Cli` | W4 |
| Three release workflows and their dist steps | most | one workflow producing the product and suite artifacts | W9 |
| Sync's scheduler, work queue, run lock, run metrics, notifications and pruning (`Api/Services`, `State`; ~2,000 in the stores alone) | moved, not removed | `DbDataKit.Scheduling`, `DbDataKit.Monitoring` | W10 |
| Build's future scheduler and monitoring | never written | the same packages | W10 |
| Unused shared packages not adopted by P5 | up to ~4,900 (the ~2,000 of `Update.*` stay, shelved) | deleted | W3 |

## 7. Decisions

**All decisions are made** (2026-10-21): the owner accepted every recommendation below, chose to move to DuckDB 2.0.0 now (D6) and
chose the secret store design (D10). D9 is settled by the W2 spike (2026-10-23). Nothing in [13a-suite-slices.md](13a-suite-slices.md) waits on a
decision any more.

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Console topology | **decided: the console and the products are separate.** Each product is its own services (a server with a management API and no UI); the dbdatakit console is the one shared UI and manages registered servers through their management APIs. A standalone install is a product plus the console |
| D2 | Config format | **agreed: one YAML format for every product** (Proxy moves off JSON, reversing plan 10 D2; no migration from `appsettings.json`, clean break) |
| D3 | Executables and names | **decided:** each product keeps its own CLI; `dbdatakit` is the console's CLI only; lower-case executables; Build's `<noun> <verb>` everywhere, old forms removed; Build's long-running process is the `server` noun (W4b) |
| D4 | Release model | **decided: each product stays a standalone install option**; the dbdatakit suite release (console image, suite MSI) comes from the same workflow and commit |
| D5 | Updater | **agreed: shelved** until the products have settled and unified; Docker images and the MSI are the distribution. Nothing is deleted |
| D6 | DuckDB.NET 2.0 lag | **decided: do not wait.** DuckDB 2.0.0 now: the released engine, pinned, with DuckDB.NET's packages that carry no engine (W1) |
| D7 | Keep DuckDB 1.x after 2.0 is the default | **decided: no.** 2.0 only; code that exists only for 1.x plans is deleted |
| D8 | Build's open 2.0 semantics | **decided:** refusal with a named diagnostic for anything not reproduced exactly (ANY/ALL, decimal quantiles if the targets cannot match, the functions that still fold); `%` and `//` by zero follow 2.0 and the targets (they raise), so the `NULLIF` goes (W1 step 5) |
| D9 | dotnet-script embedded (`Dotnet.Script.Core`) vs the `dotnet script` CLI as a subprocess | **decided (2026-10-23): neither.** Roslyn scripting plus the NuGet libraries, embedded, with no SDK at run time (plan 14); the first two options needed `dotnet restore` |
| D10 | Secret store | **decided:** finish the shared code, no ClrKernel and nothing imported from it; encrypted values in a YAML file, the key protected by DPAPI on Windows and a permission-restricted file elsewhere (W13) |
| D11 | Suite binaries self-contained vs framework-dependent | **decided: self-contained**, with the .NET SDK required only for library, driver and script features (as today). Sync needs one change first: `ProcessSupervisor` starts its workers with `dotnet exec`, which needs the shared runtime; it must start them as their own executables |
| D12 | How the Build server runs commands | **decided (accepted):** **child processes**: one per command for runs and deploys, and one long-lived child per authoring workspace (W11) so feedback is fast; isolation from the native library either way |
| D13 | Where a Build schedule lives: only in the project (committed with the refresh plan), or also added by an operator at the server | **decided (accepted):** **project first** (fits Build's "everything is a file" rule and its review of plans); an operator can pause or run now from the console, but adding a schedule is a commit. Revisit if operators need schedules a project author should not own |
| D14 | How product servers trust consoles | **decided:** the console calls the server, never the reverse; a server can be paired with several consoles, each with its own key and role ceiling; short-lived signed tokens per call; the server enforces roles; the local CLI over a named pipe or Unix socket (W12) |
| D15 | Where authoring merges | **decided (accepted):** push to the project's git remote and open a pull request when a remote is configured; otherwise merge into the deploy branch inside the Build server, recorded with the author's identity |
| D16 | How the browser reaches product servers | **decided (accepted):** **the console's back-end relays every call** (the browser talks only to the console): servers stay off the browser's network, pinned self-signed certificates work, no CORS, and live updates (Sync's SignalR run hub) are relayed by the console. The other option, browsers calling servers directly with tokens, is rejected |
| D17 | Version compatibility between a console and its servers | **decided (accepted):** products release on their own (D4), so a console and a server can differ in version. The management API carries its own integer version, reported at pairing and on every call; a console supports its own API version and the one before, and shows a clear "upgrade this server / this console" message otherwise |
| D18 | A state store for Proxy | **decided (accepted):** Proxy has none today; it needs one for its audit log, notifications and per-console read state. **SQLite through `DbDataKit.State`**, as Build's server |
| D19 | Where a Build server's projects come from | **decided (accepted):** **both**: a local path (as the CLI works today) or a git remote and branch that the server clones into its data directory and pulls before each scheduled run |

## 8. Individual releases

Decided (D4): **each product stays a standalone install option** for now: its own image, MSI and zip, its own CLI, and no dependency on
the other products. Its UI is the shared console, which its MSI includes (on by default) and its image is paired with. The dbdatakit suite
is an addition, not a replacement: the console, plus a suite MSI with one feature per product. Producing everything from one workflow and one commit keeps the cost of having both low. Dropping the per-product
artifacts can be reconsidered once the console is in use.

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| We own the package-resolution rules, where `dotnet restore` owns them today (odd package graphs, distro-specific runtime ids) | a lock file with pinned hashes in stored configs, the spike's conflict warnings, and everything behind `DbDataKit.Scripting`'s own interfaces so the engine can be swapped (for example .NET 10's file-based apps with `#:package`) without changing configs |
| DuckDB.NET 1.5.6 against the 2.0 engine: a C API call 2.0 removed or changed fails only when it is called | every product's whole suite runs on 2.0.0 before its switch merges; Build's runs on the 2.0 alphas already cover its calls, including scalar-function registration |
| The engine is a native file per platform that DuckDB.NET no longer brings | one pin with hashes; the build fails when the file is missing; a test per published artifact (zip, image, MSI) loads the engine |
| The secrets key is lost (disk, rebuild) | the documentation and `server setup` say to back it up separately; `secret export` moves values between machines; the store is useless without it, by design |
| The secrets key is readable by another local user | refuse to start on loose permissions; DPAPI plus ACL on Windows; keys from the platform's secret mechanism in containers |
| DuckDB 2.0's storage format is one-way | W1 step 2 audit before 2.0 is the default; read-only opens for user files; release note |
| Sync is large (about 68,000 lines of product code) and adoption touches its core | strangler pattern from plan 10: adapter first, existing tests unchanged, one concern per PR |
| The console work turns into a UI rewrite | W5 is extraction only, with Sync's Playwright suite and screenshots as the gate; new screens come after it |
| A management API weakens Proxy's security | separate listener, HTTPS with a pinned certificate, signed console tokens checked on every call, roles enforced on the server (W12), no shared code on the query path, the same failure classification and redaction |
| The clean break strands existing installs | release notes say an install is set up again; every server and CLI refuses old-format files with a message naming them (tested per file kind), so nothing half-works |
| Windows-only paths (service, ACLs, AD CS, the x86 worker) cannot be tested on Linux | the Windows CI jobs and `service-e2e.ps1` stay required for any PR that touches them |
| A schedule runs twice (CLI by hand and the server, or two servers hosting one project) | the per-load lock and `on_busy` already cover a manual run; the work queue's lease and run lock (W10) cover two servers; a project is hosted by one server at a time |
| A standalone install now runs two processes (the product's server and the console) | the MSI installs and registers both as one feature set; the compose file pairs the images; the product keeps working when the console is stopped |
| The console holds every product's credentials to manage it | per-server pairing, short-lived tokens scoped to one server, role ceilings per console, no database secrets in any console (each server resolves its own secret references) |
| A stolen console signing key acts on every server paired with that console | the key never leaves the console's protected store (`Secrets`, `Security.Acl`); ceilings limit the damage; `server unpair` on each server, or a signed key rotation, cuts it off |
| Two consoles change the same thing | every write is checked against the version it was based on; refused when stale |
| Authoring on a server lets a web user write files that a scheduled run then executes | drafts live in per-user worktrees; schedules run from a pinned ref; merging needs a role; every write goes through the confined `project file` commands |
| Monitoring depends on the console being up | each server evaluates its own health rules and keeps its notifications; the console only shows them |
| One maintainer, many parallel streams | the phases cap work in flight; each phase has an exit to check before the next |

## 10. Relation to earlier plans

- Plans 01-08 designed the shared packages; this plan finishes their **adoption** (W3) and decides the unused ones.
- Plan 08 (update system) stays shelved (D5).
- Plan 09 (trunk, CalVer, per-product releases) stays; W9 adds the suite release on top of it.
- Plan 10 (Proxy adoption) is done in its C-steps; its D2 (JSON config) is reversed by D2 here.
- Plan 11 (CI and testing) provides W0 step 5.
- Plan 12 (shared TLS rollout) is the model for how the slices are run: one branch and PR per slice, each with its own test, and the open
  follow-ups listed in the plan.
