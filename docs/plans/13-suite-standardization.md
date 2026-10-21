# Plan 13: One suite — standardizing DbDataSync, DbDataProxy and DbDataBuild

Status: **proposed** (2026-10-21), **revised the same day after the owner's review**: the updater is shelved (W9); a single YAML
format for every product is agreed (D2); the one-CLI idea is open for review (W4); DbDataBuild will get a built-in scheduler and
monitoring, so scheduling and monitoring become shared (W10) and Build becomes a service the console talks to (section 3). Scope: everything needed to turn three products that share a repository into one suite with one
management console, one set of definitions and one release, while removing as much duplicated code as possible. Breaking changes are
allowed. Two pieces of work are folded in: **finishing DuckDB 2.0** and **moving DbDataSync's libraries, drivers and scripts to
dotnet-script**.

Inputs: plans 01-12, `docs/plans/STATUS.md`, `docs/review/OVERLAP-AND-CONSOLIDATION.md`, `DbDataBuild/docs/research/duckdb-2.0/README.md`,
`DbDataBuild/docs/progress/OPEN-ITEMS.md` (section H), `DbDataSync/docs/drivers-and-libraries.md`,
`DbDataSync/architecture/planning/done/csharp-scripting-host.md`, and the code at `90cdbc5`.

## 1. Goal

- **Less code.** Each concern (connections, dialect, metadata, secrets, config, transfer, hosting, update, CLI, web shell, auth) has one
  implementation in `DbDataKit/`, and the products keep only what is really theirs: replication (Sync), the Flight SQL server (Proxy),
  and modelling and lowering (Build).
- **One console.** One web UI manages all three products. Proxy and Build get management and configuration screens for the first time,
  built from the same shell, components and back-end as Sync's.
- **One suite release** with all three products, from one commit, shipped the way products ship today (Docker images and the MSI installer, plus
  the zips and install scripts). Individual releases stay possible for a while (section 8). Update work waits until the products have settled.

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
- **DuckDB.NET has no 2.0 release yet** (latest `DuckDB.NET.Data.Full` is 1.5.6). Build's experiments show DuckDB.NET 1.5.6 runs unchanged
  against the 2.0 native library, because the old C API is still there.
- Build has done the hard part against the alphas (entries 173 and 174: the lowerer reads 2.0's own plans; the clock functions that 2.0
  folds into constants are kept as calls). Still open (OPEN-ITEMS H): ANY/ALL in 2.0's CASE form is refused rather than translated;
  `%` and `//` by zero raise on 2.0 where the lowering writes NULL; decimal `median`/`quantile_cont` differ in the last digit;
  `current_schemas`, `current_setting`, `getvariable` and `getenv` still fold; Windows and macOS were not run; the preview CI job is on hold.
- Sync and Proxy have not been tested on 2.0. Proxy's DuckDB sandbox (`Connections.DuckDb`: read-only, no external access, locked
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
                         ┌──────────────────────────── DbData Console (one web UI) ────────────────────────────┐
                         │  shell, sign-in, users, certificates, updates, config history, connections, secrets │
                         │  ┌── Sync module ──┐   ┌── Proxy module ──┐   ┌── Build module ──┐                  │
                         └──┴────────┬────────┴───┴─────────┬────────┴───┴─────────┬────────┴──────────────────┘
                                     │ in-process           │ management API       │ management API
                                     ▼                      ▼ (HTTPS, admin token) ▼ (HTTPS, admin token; JSON documents)
 console host (from DbDataSync.Api) ─ Sync engine      DbDataProxy service ×N     DbDataBuild service ×N
   state DB (users, sessions, audit) │  TaskRunner      Flight SQL data plane      built-in scheduler and monitoring
                                     │  scheduler                                  projects are their own repos
                                     ▼
                     DbDataKit/: Config · Secrets · Dialect · Connections (+ FlightSql) · Metadata · Transfer
                                 Hosting · Scheduling · Monitoring · Scripting (dotnet-script) · Cli (command surface, MCP) · Web · State
```

- **One console host.** DbDataSync's API already has users, sessions, passkeys, a state database, a scheduler, a git-tracked config
  repository, certificate and update pages, and service install. It becomes the console host, with the Sync engine as one module.
- **Proxy stays a separate data-plane process.** It has its own uptime, its x86 worker and its hot path. It gains a **management API** on a
  separate listener that the console calls. One console can manage several proxies.
- **Build becomes a long-running service as well as a CLI**, because its built-in scheduler and monitoring need something that is always
  running. The service runs scheduled refreshes, keeps the monitoring view, and exposes the JSON command surface Build already has through a
  management API, in the same shape as Proxy's. The TUI, MCP and `ui web` are already clients of that surface, so the console adds no second
  code path to a database. The CLI keeps working without the service, as it does today. Each command still runs in a child process inside
  the service, which keeps a crash in the native polyglot library out of it (D12).
- **Every product has the same shape:** a service with a management API, managed by the console. Sync is the exception for now, because
  its engine already lives in the console host; it can be split out later the same way if that is ever needed.
- **Shared definitions.** One `connection/1` catalog, one secret reference syntax and store, one user list with roles, one git-tracked
  config repository.

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

### W1. Finish DuckDB 2.0 (M)

1. **Run the suites against 2.0.0 final now**, using the native-library swap. Generalize `DbDataBuild/scripts/test-duckdb-preview.sh` into
   a repository-level `scripts/duckdb-native-swap.sh` that runs any test project against a pinned, hash-checked `libduckdb`. Run Build
   unit and conformance tests, `DbDataKit.Connections.DuckDb.Tests` (the sandbox recipe), Proxy's DuckDB tests, and Sync's
   `Drivers.DuckDb`, `Verification` and `Scripting` tests. Record the results in the research README.
2. **Check the storage format.** List every place a product creates or opens a DuckDB file. Proxy and Sync open user files read-only:
   confirm that 2.0 opens a 1.x file read-only without upgrading it, and that a read-write open (Sync's scripting and verification, any
   scratch files) does not silently convert a user's file. Document that files written by the suite are 2.0-only.
3. **Close Build's open 2.0 items** (decision D8): ANY/ALL (translate or keep the refusal), `%`/`//` by zero (match 2.0's error or keep
   writing NULL), decimal quantiles (tolerance or refusal), and the functions that still fold (refuse them with a named diagnostic).
4. **Package.** When DuckDB.NET ships a 2.0 build, bump the one central pin (W0). If it has not shipped by the cut-off in D6, ship
   DuckDB.NET 1.5.6 managed with the 2.0.0 native library, fetched pinned and hash-checked the way `polyglot.pin` works. This is an
   interim step only.
5. **Make 2.0 the default.** Regenerate Build's committed lowered artifacts and template goldens (their headers carry the DuckDB version),
   turn the preview job into the normal DuckDB job, run Windows (the Wine script and the Windows CI jobs). Drop the 1.x path per D7.

Exit: one DuckDB pin in the repository, every suite green on 2.0 on Linux and Windows, and no 1.x-only code left (except what D7 keeps).

### W2. Libraries, drivers and scripts on dotnet-script (L)

Goal: **one resolver and one loader** for all code DbDataSync loads at runtime, built on dotnet-script (`Dotnet.Script.Core` and
`Dotnet.Script.DependencyModel`). dotnet-script 2.0 supports .NET 10 and C# 14, needs SDK 10.0.100 or later, and loads assemblies in an
isolated load context. The shared package is `DbDataKit.Scripting`.

| Today | After |
| --- | --- |
| library: throwaway project + `dotnet publish` + `library.json` + a Resolving hook on the default load context | `libraries/<id>/library.csx` holding only `#r "nuget: <id>, <exact version>"` lines; dotnet-script's dependency model gives the runtime and native asset paths, cached by hash; one `AssemblyLoadContext` per library |
| compiled plugin driver: you build and package an assembly, then `driver.json` names it | `drivers/<id>/driver.csx`: `#r "nuget: ..."`, `#load` for helpers, and a class implementing `IDriver`, compiled by the same host; no packaging step. Descriptor YAML drivers are unchanged |
| script: `config/scripts/<name>.cs` + manifest, no package references | `config/scripts/<name>.csx` + the same manifest. A script may `#r` packages that are on the allowed list or already installed as libraries, and `#load` shared helpers. The allow-listed reference set and `ScriptSyntaxGuard` stay as lints, documented as not a security boundary, as they are today |

Steps:

1. **Spike (S, first).** Embed `Dotnet.Script.Core` 2.x in the ASP.NET host on .NET 10. Check: restore from nuget.org and from an offline
   local feed; native assets (DuckDB, SQLite, Oracle); unloading; cold-compile time against today's Roslyn path; the Docker image (needs the
   SDK, which library installs already require: `SdkAvailability`); Windows service account profile paths for the NuGet cache. Decide D9
   (embed or call `dotnet script` as a subprocess) from the result.
2. **`DbDataKit.Scripting`**: a resolver (package references to a resolved closure on disk), a compiler (`.csx` to a cached assembly with an
   allow-list policy), a loader (a collectible load context per library, driver or script), and diagnostics. All package input
   goes through `Connections.Libraries.Validation` (the H1 corpus: id, exact version, source, path safety). Package source mapping
   restricts feeds.
3. **Port Sync** onto it in three PRs (libraries, drivers, scripts), adapter first. The web pages (Libraries, Drivers, script editor with
   Monaco) keep working. Add `dbdatasync script test <name>` and editor support: generate a stub so VS Code's C# tooling resolves
   `#r` to the scripting abstractions.
4. **Migration (breaking).** A config migration (W3, `Config` migrations with `.bak`) converts `scripts/*.cs` to `.csx`, `library.json` to
   `library.csx`, and a compiled plugin's `driver.json` to a shim `driver.csx` that `#r`s the same package and returns the same type.
   Libraries are restored again on first start.
5. **Delete** `DbDataSync.Libraries`, the plugin loading in `Drivers.Descriptor`, `ScriptCompiler`'s compilation path, and
   `DbDataKit.Connections.Libraries`' installer (its `Validation` moves into `DbDataKit.Scripting`).
6. **Reuse elsewhere.** Proxy can offer "any ADO.NET provider" data sources through the same library catalog and the generic composer
   in `Connections`. Build hooks could accept `.csx` later. Both are optional.

Exit: one way to restore and load code at runtime; existing configs migrate with one command; the web pages work as before.

### W3. Shared libraries in Sync and Build (L, ongoing)

Plan 10's rules apply: adapter first, delete second, one concern per PR, existing tests unchanged.

**DbDataSync** (order: what the console and W2 need first):

1. `Config.*` replaces `Core/Config`'s YAML handling: strict parsing, `schemaVersion`, migrations. W2 and W8 need it for their breaking-change migrations.
2. `Secrets.*` replaces `ClrKernel.Core.Secrets`. This needs D10 (a real keyring backend or a wrapper behind `ISecretResolver`).
3. `Dialect.*` replaces `Core/Sql` (quoting, lexer, type map). Sync's `IDriver` stays: it is the replication contract, not the connection.
4. `Connections.*` composes every driver's connection string. Failures go through the shared classifier.
5. `Metadata.*` for catalog reads (the readers exist for SQL Server, Postgres, DuckDB, ODBC; MySQL and Oracle readers are added to the shared package).
6. `Transfer.SqlServer` / `Transfer.Postgres` bulk writers and the strategy builders replace the hand-written staging paths, measured with the benchmark stub first.

**DbDataBuild** (nothing adopted yet):

1. `Dialect.*` behind `DialectProfile` (keep Build's matrix and rules; the shared package gets Build's fixtures as conformance cases).
2. `Connections.*` replaces `Logins`, `OdbcLogin`, `FlightSqlLogin`, `DriverFailure` (the login convention `DBDATA_<APP>_<CONN>_<READ|WRITE>`
   already accepts `DBDATABUILD_*`). Build's rule "driver messages are never echoed" becomes a shared-classifier test.
3. `Metadata.*` replaces `CatalogReader`, `SourceCatalogReader`, `OdbcCatalogClient`. `ServerFacts` becomes the shared record (plan 04's open question).
4. `Transfer.*` replaces `Transfer.cs`, `MutationGate` and `StatementLog`. The shared `WriteGate` was modelled on Build's gate, so
   `GateInvariantTests` moves to the shared package and runs against both.

**New shared package: `DbDataKit.Connections.FlightSql`**, a Flight SQL client. It replaces Sync's `Drivers.ArrowFlightSql` connection code
(the driver keeps only its readers) and Build's `FlightSqlRowSource`. CI tests it against **DbDataProxy itself**: the suite's own server
becomes an end-to-end test of the suite's own client.

**Unused shared packages:** each is adopted by the end of this workstream or deleted: `Transfer.*` (adopted above), `Update.*` (shelved, not deleted: W9),
`Connections.Libraries` (folded into W2), `Connections.Descriptor` (Sync's descriptor reader moves onto it or it goes),
`Connections.Postgres` (Sync and Build use it), `Dialect.Arrow` (Proxy's C7 benchmark decides), `Secrets.Keyring` (D10), `Secrets.Cli` (W4).

Exit: no product has its own dialect, connection composer, catalog reader, secret resolver, or YAML loader; no shared package is unused.

### W4. One command surface; the executables and their names under review (M)

Two separate questions. The first is agreed in substance; the second is **open for the owner's review** (D3).

**4a. The command contract (not dependent on naming).** Generalize Build's command contract into `DbDataKit.Cli` (alongside
`Hosting.Cli`): each command has a spec with an effect class (reads, writes files, writes a database), a `--format json` document with a
closed schema, and generated help. TUI catalog entries, MCP tools and the management APIs of W6, W7 and W10 come from the same specs.
Sync and Proxy commands adopt it; System.CommandLine everywhere (Build already uses it). The result is JSON output everywhere, the same
exit codes across products, and one MCP surface. This is worth doing whatever 4b decides.

**4b. Executables and names (for review).** Today:

| | Executable | Command shape | Examples |
| --- | --- | --- | --- |
| Sync | `dbdatasync` (also the dotnet tool name) | mixed: verbs and `config <noun> <verb>` | `setup`, `serve`, `update`, `service install`, `config library install`, `config cert enroll` |
| Proxy | `DbDataProxy` (PascalCase, unlike the other two) plus `DbDataProxy.Worker` | flat verbs | `hash-password`, `protect-secret`, `query`, `config check`, `service install`, `cert show`, `run` |
| Build | `dbdatabuild` | `<noun> <verb>`, nouns by what they touch | `project compile`, `connection deploy`, `connection refresh`, `ui web` |

The options:

| Option | What it is | For | Against |
| --- | --- | --- | --- |
| **A. One executable** | `dbdata sync ...`, `dbdata proxy ...`, `dbdata build ...`, plus shared `dbdata service`, `cert`, `config`, `secret`, `doctor` | one thing to install and put on the PATH; shared verbs exist once; fits a suite release | every product's commands get longer; one binary carries everything (Build's native library, DuckDB, Arrow) even for someone who wants only Proxy; the three names people know go away |
| **B. Three executables, one grammar** | keep three product executables, all lower case (`dbdataproxy` too), all `<noun> <verb>`, the shared verbs (`service`, `cert`, `config`, `secret`, `doctor`) identical in each because they come from `DbDataKit.Cli` | least churn; each product stays installable on its own; names people know stay | shared verbs repeated in three places (the code is shared, the surface is triplicated); nothing that speaks for the suite |
| **C. Three executables plus a thin suite dispatcher** | B, plus `dbdata`, which owns only suite-level verbs (`dbdata status`, `dbdata doctor`, `dbdata console`) and forwards `dbdata sync ...` to `dbdatasync ...` (the way `git foo` runs `git-foo`) | B's independence with one entry point for the suite; nothing is forced on someone who uses one product | two ways to run the same command; one more small executable |

Questions for the review:

1. Is one-product-only installation something to keep? It decides between A and B/C more than anything else.
2. The suite name: `dbdata` (short, generic), `dbdatakit` (already the repository and the shared library prefix, so users would see the
   library name), or another name. Whatever is chosen also names the console, the MSI and the Docker images.
3. Proxy's executable casing: `DbDataProxy` to `dbdataproxy` is a breaking change worth making in any option (service registrations and
   `install.ps1` refer to it).
4. Grammar: Build's `<noun> <verb>` with nouns grouped by what a command touches (`project` is files only, `connection` is a database) is
   the most deliberate of the three. Adopting it means renaming Sync's top-level verbs (`setup`, `serve`) and Proxy's flat ones
   (`hash-password` becomes `secret hash`, for example).
5. Build's service (section 3, W10) needs a verb: `dbdatabuild serve` (Sync's word), `run` (Proxy's word, but Build's `run` already means
   something), or `service run`.

Until D3 is decided, the plan assumes nothing beyond 4a and the casing fix in question 3.

Exit: every command in the suite has a JSON form and a schema; one MCP surface covers all three products; D3 decided and its renames done.

### W5. Shared web platform (L)

Extract first, extend second: Sync's UI must look and work the same after the extraction.

**Back-end: `DbDataKit.Web` and `DbDataKit.State`**, taken out of `DbDataSync.Api` and `DbDataSync.State`:

- auth: the session handler, passkeys (Fido2), Windows Negotiate sign-in, bootstrap invite, policies and roles;
- stores: users, sessions, invites, notifications, audit, on the state database's existing SQLite / SQL Server / PostgreSQL dialects;
- hosting: `PlainHttpProtection` (already shared), HTTP.sys hosting, web root location, the SignalR hub base;
- admin endpoints: certificates, updates, config history (Sync's `GitCommitService` becomes `DbDataKit.Config.Git`), restart, about,
  health, metrics, docs viewer, files.

**Front-end: an npm workspace at `DbDataKit/web/`**, made from `DbDataSync.Web`:

| Package | Contents |
| --- | --- |
| `@dbdata/shell` | `AppShell`, `SignIn`, `AdminTabs`, `NotificationBell`, `ErrorBanner`, `Field`, `KeyValueTable`, `CodeEditor` (Monaco), `DiffViewer`, `Markdown`, `StatusBadge`, `PreviewGrid`, the API client and hooks, WebAuthn |
| `@dbdata/admin` | the shared pages: users and invites, certificates, updates, config history, connections catalog (W8), libraries and drivers (W2), files, docs |
| `@dbdata/sync` | replications, mappings, runs, monitoring: the Sync pages that are Sync's own |
| `@dbdata/proxy` | W6 |
| `@dbdata/build` | W7 |
| `@dbdata/console` | the app that composes the modules; one Vite build; vitest and Playwright set up once |

The `@dbdata/` scope is a placeholder until the suite name is chosen (D3). Sync's update page moves into `@dbdata/admin` as it is
(the updater is shelved, D5): Sync-only, unchanged.

Exit: Sync's UI is built from the workspace with no visible change (Playwright suite and screenshots unchanged); `@dbdata/shell` and
`@dbdata/admin` import nothing from `@dbdata/sync`.

### W6. DbDataProxy management (M-L)

Today Proxy is configured by editing `appsettings.json` and running CLI verbs. It has no runtime admin interface.

1. **Management API** on a separate listener: loopback by default, TLS, authenticated with a token the console issues (or mTLS, D1).
   It is never on the Flight port and never on the hot path. Endpoints: health, version and drain (plan 03's seams); effective config,
   redacted, with origins; validation; data sources (CRUD and test connection); users, Windows accounts, JWT mappings and pass-through;
   live sessions and running queries (with cancel); concurrency gates and limits; login throttle state; certificate status and renewal;
   recent classified failures.
2. **Config moves to YAML in a git-tracked directory** (D2, which reverses plan 10's D2). Each data source becomes a `connection/1`
   definition with a `proxy:` extension (classifier mode, limits, bitness, pass-through); users are a separate file; secrets are references.
   Writes go through `Config.Yaml`'s comment-preserving editor and are applied by hot reload (Proxy already reads options through
   `IOptionsMonitor`). A config migration converts an existing `appsettings.json`.
3. **Console module `@dbdata/proxy`**: register one or more proxies; Overview (health, version, sessions, throughput); Data sources
   (catalog-backed editor, classifier mode, limits, test); Users and auth; TLS (the shared certificate page); Activity (queries and
   failures); Config (diff and history); Service (doctor).

Exit: everything in today's `appsettings.example.json` can be edited from the console with validation; adding or changing a data source or
user needs no restart; the upgrade from the last JSON release is tested in CI.

### W7. DbDataBuild management (M-L)

Today Build has a CLI, a TUI, an MCP server and app, and `ui web`. All are clients of the JSON command surface. Refresh plans already
carry `expected_every`, `on_busy`, `on_failure`, `parallel` and `max_runtime`, and `connection status` / `connection monitor` read the
run history from the tracking tables; what is missing is something that starts runs on time and watches them (W10).

1. **A Build service** (`Hosting.WindowsService` / `Hosting.Systemd`, installed like Sync's and Proxy's): it runs the built-in scheduler
   (W10) for the projects registered with it, keeps the monitoring view current, and serves a management API that exposes the JSON command
   surface (W4a) for those projects. Commands run in a child process per command inside the service (D12). The CLI works without the service
   exactly as today; a person running `connection refresh` by hand and the scheduler share the per-load lock and `on_busy` that already exist.
2. **Console module `@dbdata/build`**, a client of that management API: a project registry (paths or git remotes); compile and diagnostics
   (with `help code` text); graph and lineage (Mermaid already exists); connection status and drift; **plans: review, approve, apply**, with
   the approver's identity and time recorded in the plan's events (something the CLI cannot give); schedules and monitoring (W10); compare; sample.
3. **Logins from the shared secret store.** The service maps secret references into each command's environment
   (`DBDATA_<APP>_<CONN>_<READ|WRITE>`), so Build's "no fallback from read to write" rule holds unchanged.
4. **Retire `ui web`** (`Web/WebServer.cs`, `WebBackend.cs`, `WebActions.cs`, `index.html`) once the module covers it. The TUI stays; the MCP
   server moves to the shared surface (W4a).

Exit: a person can take a project from compile to an approved, applied plan and a scheduled, monitored refresh without leaving the console,
and the same schedule runs with the console switched off.

### W8. Shared definitions: connections, secrets, identity, config repository (M-L)

1. **One connection catalog.** `connection/1` (`DbDataKit.Connections`) is the one on-disk connection format for Sync connections, Proxy
   data sources, and Build connections. Product-specific fields live under `extensions.<product>`. Build projects keep their own connection
   files (they are committed by the author) and can also refer to a catalog entry by name.
2. **One secret syntax and store** (`env:`, `file:`, `secret:`, `dpapi:`) in every product.
3. **One identity model.** Console users (passkeys, Windows) with roles: Admin, Operator, Viewer, and Approver (for Build plans). Proxy's
   client identities stay Proxy's data-plane auth but are managed from the console. One PBKDF2 hasher (plan 10 C3).
4. **One config repository layout**, git-tracked with auto-commit and the history UI:

```
<data>/config/
  connections/*.yaml            shared catalog
  sync/                         replications, scripts (*.csx), drivers, libraries
  proxy/<name>/                 per managed proxy (or the proxy's own directory when it runs alone)
  build/projects.yaml           registered projects (the projects themselves stay in their own repositories)
```

Exit: one Connections page, one secret store, one user list.

### W9. Distribution and releases (M)

1. **One suite release** (D4) from one workflow, gated on green product CI and `dbdatakit-ci.yml` for that exact commit, shipped through
   the channels that already exist: **Docker images** (console, proxy, and a Build service image once W7 exists) and **the MSI** (`dist/msi.py`),
   which becomes one MSI with features (Console and Sync, Proxy service, Build), plus the zips, `install.ps1`/`install.sh` and `SHA256SUMS`.
   The same workflow also produces today's per-product zips and tags from the same commit for a transition period (`dist.json` already
   templates them, so this costs little). The tag prefix and artifact names follow D3's suite name.
2. **The updater is shelved** (owner decision, D5). No update work until the products have settled and unified. Sync's `update` command,
   `Updates` and `Launcher` keep working as they are; `DbDataKit.Update.*` stays in the repository unused and is neither adopted nor deleted
   by this plan. When the topic is reopened, the choice between the two is still the one to make.
3. **NuGet** stays paused (plan 09). When it comes back, decide whether the suite ships as a dotnet tool.

Exit: one release workflow producing the Docker images and one MSI. Each product's release workflow is deleted or reduced to a call into the suite's.

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
   skip), the evaluator (next due time, what was missed while the service was down), the work queue with leases and the run lock, and
   overlap policies that map onto Build's `on_busy` and Sync's existing behaviour. It holds no product logic: a job is "run this command
   with these inputs", and the product decides what that means. Taken from Sync's code, with its tests.
2. **`DbDataKit.Monitoring`**: a neutral run model (job, run, outcome, start, duration, rows, the item that failed, a classified failure
   from `Connections`), health rules over it (overdue and slow from an expected interval, failed, lag), notifications with
   channels (in the console's bell first; email or webhook later), and retention. Each product supplies its run history through an
   interface: Sync from its state database, Build from its tracking tables, which stay where they are (Build's run history travels with
   the data by design and is not copied into the console's state database).
3. **Schedules are data in Build projects.** In keeping with Build's rule that everything is a file a person can inspect and commit, a
   refresh plan declares its schedule next to `expected_every` (for example `schedule: "15 * * * *"`, `time_zone:`). The service reads them
   from the registered projects; `project show refresh` shows them; the console shows and can pause them, and a change is a commit to the
   project. A schedule the operator adds only at the service (not in the project) is the open question in D13.
4. **Sync moves onto the shared packages** behind its current behaviour (its tests unchanged), which proves them before Build depends on them.
5. **One monitoring view in the console** for both products: runs, outcomes, overdue and slow, lag, failures, notifications; the Build and
   Sync pages are filters of it. Proxy can feed the same model later (failed logins, refused statements, limits reached).
6. **Build's open items it closes:** time-based scheduling; the warnings in `status` and `monitor` are evaluated continuously instead of when
   someone looks; retention of plan files and statement logs from scheduled runs (OPEN-ITEMS D0) becomes a retention policy.

Exit: a Build refresh plan runs on its declared schedule from the Build service, its overdue and failed runs reach a person, and Sync's
scheduler and run monitor run on the same packages with no behaviour change.

## 5. Phases

```
P0 foundations ─► P1 engines and loaders ─► P2 platform ─► P3 new consoles ─► P4 suite release ─► P5 retire
  W0                W1 (2.0 default)          W5 back-end    W6 Proxy           W9                  per-product releases,
  W1.1-2 probes     W2 (dotnet-script)        W8 catalog     W7 Build service   (images, MSI)       old CLI names, ui web,
  W2.1 spike        W3 Config/Secrets/        W4a commands   W10 Build schedule                     dead shared packages
  D3 review         Dialect in Sync, Build    W10 extract    W3 Metadata/
  decisions         W5 front-end extraction   from Sync      Transfer
                                              W3 Connections
```

| Phase | Contents | Exit |
| --- | --- | --- |
| **P0** (now) | W0; W1 steps 1-2 on 2.0.0 final; W2 spike; the review of D3 (W4b); the other decisions in section 7 | one build setup and one version per package; a written 2.0 result for all three products; a spike report |
| **P1** | W1 to the end (when DuckDB.NET 2.0 lands, or the interim per D6); W2; W3 Config, Secrets, Dialect in Sync and Dialect, Connections in Build; W5 front-end extraction | 2.0 is the default; one code loader; Sync's UI built from the workspace |
| **P2** | W5 back-end; W8; W4a (and W4b's renames once D3 is decided); W10 steps 1, 2 and 4 (extract from Sync, Sync on the shared packages); W3 Connections in Sync and Metadata in both; `Connections.FlightSql` | the console host serves the shared admin pages; one catalog, secret store and user list; Sync's scheduler runs on `DbDataKit.Scheduling` |
| **P3** | W6; W7 (the Build service and console module); W10 steps 3, 5 and 6 (Build schedules, one monitoring view); W3 Transfer | Proxy and Build are managed from the console; Build refreshes run on schedule and are monitored |
| **P4** | W9; the first suite release | one release with all three products: Docker images and one MSI |
| **P5** | after two suite releases: remove the per-product tags and workflows (if D4 says so), the old CLI names, `ui web`, and any shared package still unused | nothing duplicated remains |

The workstreams run in parallel where they touch different code: W1 and W2 are independent of each other; W5's front-end extraction does not
wait for W3; W6 and W7 need W5 and W8 but not each other; W10's extraction from Sync can start as soon as W0 is done, and must be
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
| Three CLI parsers and help/JSON conventions | part | `DbDataKit.Cli` | W4 |
| Three release workflows and their dist steps | most | one suite workflow | W9 |
| Sync's scheduler, work queue, run lock, run metrics, notifications and pruning (`Api/Services`, `State`; ~2,000 in the stores alone) | moved, not removed | `DbDataKit.Scheduling`, `DbDataKit.Monitoring` | W10 |
| Build's future scheduler and monitoring | never written | the same packages | W10 |
| Unused shared packages not adopted by P5 | up to ~4,900 (the ~2,000 of `Update.*` stay, shelved) | deleted | W3 |

## 7. Decisions

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Console topology: one console host (from `DbDataSync.Api`) that manages Proxy and Build services, vs each product serving its own copy of the shared console | **agreed: one console host** (the management console). Console-to-service auth: a console-issued token first, mTLS later |
| D2 | Config format | **agreed: one YAML format for every product** (Proxy moves off JSON, reversing plan 10 D2, with a migration from `appsettings.json`) |
| D3 | Executables and names | **open, for the owner's review**: options A, B and C and the questions in W4b |
| D4 | Release model | **one suite release**, plus per-product artifacts from the same workflow for two suite releases, then drop them |
| D5 | Updater | **agreed: shelved** until the products have settled and unified; Docker images and the MSI are the distribution. Nothing is deleted |
| D6 | DuckDB.NET 2.0 lag | wait up to **two weeks** from today; after that, the interim build (DuckDB.NET 1.5.6 managed with the pinned 2.0.0 native library) |
| D7 | Keep DuckDB 1.x after 2.0 is the default | **no** (breaking changes are allowed): 2.0 only, and delete 1.x-shape code that 2.0 no longer needs |
| D8 | Build's open 2.0 semantics: ANY/ALL, `%`/`//` by zero, decimal quantiles, functions that still fold | owner's call, item by item; the default is refusal with a named diagnostic, which is Build's rule for anything it cannot reproduce exactly |
| D9 | dotnet-script embedded (`Dotnet.Script.Core`) vs the `dotnet script` CLI as a subprocess | **embedded**, if the W2 spike passes |
| D10 | Secret store backend: wrap `ClrKernel.Core.Secrets` vs implement `Secrets.Keyring` | decide in P0; it blocks Sync's Secrets adoption |
| D11 | Suite binaries self-contained vs framework-dependent | **self-contained**, with the .NET SDK required only for library, driver and script features (as today) |
| D12 | How the Build service runs a command: child process per command vs in-process | **child process** (isolation from the native library; Build's JSON documents are already the interface) |
| D13 | Where a Build schedule lives: only in the project (committed with the refresh plan), or also added by an operator at the service | **project first** (fits Build's "everything is a file" rule and its review of plans); an operator can pause or run now from the console, but adding a schedule is a commit. Revisit if operators need schedules a project author should not own |

## 8. Individual releases

Keeping them costs little while they come from the same workflow and commit as the suite (W9 step 1). Dropping them later simplifies
three things: one version number to talk about, one place for images and installers, and no per-product release workflows.
Breaking changes are expected anyway, so the first suite release is a natural cut-over point. The recommendation (D4) is to publish both
for two suite releases and then publish only the suite. A user who wants one product installs the suite with `--components` (or picks
the MSI feature or the image). Whether one product can still be installed on its own also feeds the CLI review (W4b, question 1).

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| dotnet-script is a small open-source project (maintenance, .NET release lag) | everything goes through `DbDataKit.Scripting`'s own interfaces, so the engine behind them can be swapped (back to plain Roslyn, or .NET 10's file-based apps with `#:package`) without changing configs |
| DuckDB.NET lags DuckDB 2.0 | D6 interim build; the old C API is proven to work against 2.0 |
| DuckDB 2.0's storage format is one-way | W1 step 2 audit before 2.0 is the default; read-only opens for user files; release note |
| Sync is large (about 68,000 lines of product code) and adoption touches its core | strangler pattern from plan 10: adapter first, existing tests unchanged, one concern per PR |
| The console work turns into a UI rewrite | W5 is extraction only, with Sync's Playwright suite and screenshots as the gate; new screens come after it |
| A management API weakens Proxy's security | separate listener, loopback by default, TLS, admin token, no shared code on the query path, the same failure classification and redaction |
| Config migrations break installs (Sync scripts, Proxy JSON to YAML) | `Config` migrations write `.bak`; golden migration pairs (`Config.Testing`); an upgrade test from the last release of each product in CI |
| Windows-only paths (service, ACLs, AD CS, the x86 worker) cannot be tested on Linux | the Windows CI jobs and `service-e2e.ps1` stay required for any PR that touches them |
| A schedule runs twice (CLI by hand and the service, or two services registered for one project) | the per-load lock and `on_busy` already cover a manual run; the work queue's lease and run lock (W10) cover two services; a project is registered with one service at a time |
| Monitoring depends on the console being up | each service evaluates its own health rules and keeps its notifications; the console only shows them |
| One maintainer, many parallel streams | the phases cap work in flight; each phase has an exit to check before the next |

## 10. Relation to earlier plans

- Plans 01-08 designed the shared packages; this plan finishes their **adoption** (W3) and decides the unused ones.
- Plan 08 (update system) stays shelved (D5).
- Plan 09 (trunk, CalVer, per-product releases) stays; W9 adds the suite release on top of it.
- Plan 10 (Proxy adoption) is done in its C-steps; its D2 (JSON config) is reversed by D2 here.
- Plan 11 (CI and testing) provides W0 step 5.
- Plan 12 (shared TLS rollout) is the model for how the slices are run: one branch and PR per slice, each with its own test, and the open
  follow-ups listed in the plan.
