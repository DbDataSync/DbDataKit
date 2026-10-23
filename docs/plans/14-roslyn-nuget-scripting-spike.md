# 14. Roslyn scripting with NuGet libraries (spike result and plan)

Status: **adopted** (2026-10-23) as the plan for workstream W2 in place of dotnet-script: D9 is decided (Roslyn scripting plus the NuGet libraries, embedded) and slices S02-S04 follow section 5. The spike code is in `spikes/roslyn-nuget-scripting/` (its README has the commands and the real output). The Windows pass and section 7's follow-up are still to do, and the S-series is low priority until the owner picks it up.

Why: dotnet-script resolves `#r "nuget:"` with `dotnet restore` on a generated project, which needs the SDK and MSBuild at run time. The
owner wants `#r "nuget: Some.Package, 1.2.3"` with only the .NET runtime installed. This spike builds that from `Microsoft.CodeAnalysis.CSharp.Scripting`
and the NuGet libraries (`NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Frameworks`, `NuGet.Versioning`) only.

Legend: **[V]** verified by running it in the spike; **[R]** reasoned or read from documentation, not run.

## 1. The sample files (`scripting-and-nuget-examples/`)

| File | What it does | Reuse | Do not copy |
| --- | --- | --- | --- |
| `roslyn-extract-package-reference.cs` | parses a script and lists `#r` directives through `ReferenceDirectiveTrivia` | the whole idea: parse with Roslyn, never a regex (`ScriptDirectives.cs`). We parse with `SourceCodeKind.Script` and blank the directive so line numbers survive | it only prints; the directive must also be removed before compiling, or Roslyn tries to resolve `nuget: ...` as a file |
| `dotnet-load-nuget-package-livereloadserver.cs` (West Wind) | recursive download of a package and its dependencies from several sources, then load into the default context | multiple sources through `Repository.Factory.GetCoreV3(url-or-folder)` (one call handles both), `FindPackageByIdResource.CopyNupkgToStreamAsync`, one folder per id/version, recursion over dependencies | the framework pick (`OrderByDescending(ToString())` sorts "net9.0" above "net10.0"; exact-string match of dependency groups instead of nearest), `Replace("/", "\\")` (Windows only), passing `VersionRange.OriginalString` as a version, loading into `AssemblyLoadContext.Default` (no unload, no isolation), swallowed errors, only files under one TFM folder (loses `runtimes/`), no hash, no native assets |
| `dotnet-load-nuget-package-example.cs` | download one package to a temp folder, copy the `lib` files of the first "net*" group flat, load the main dll in a collectible context | collectible context per package, `PackageReader` use, the idea of a download folder keyed by id/version | takes the first group, not the nearest; no `Load` override, so the package's own dependencies never resolve; flattened folder; "main dll" guess |
| `dotnet-nativelibrary-loader.cs` | `NativeLibrary.SetDllImportResolver` for one assembly, looking in `runtimes/<rid>/native` under the app folder | the path convention `runtimes/<rid>/native/<name>` and `NativeLibrary.Load` | a resolver can only be set once per assembly and only for assemblies you own, so it cannot serve third-party package assemblies; a hard-coded RID and extension. We use `AssemblyLoadContext.LoadUnmanagedDll` in the script's context instead |

## 2. Design

Pipeline: `.csx` text -> directives -> resolver (closure on disk) -> compiler (Roslyn, cached) -> loader (collectible context) -> run.

### 2.1 Directives [V]
`#r "nuget: Id"`, `#r "nuget: Id, 1.2.3"`, `#r "nuget: Id, [1.0,2.0)"`, `#r "nuget: Id, 1.2.*"`. Decision for the suite: **config and
library scripts use exact versions only** (as W2 already says); the spike also accepts NuGet ranges and floating versions to show
they work, with NuGet's rule "the lowest version that satisfies the range" for fixed ranges and the highest for floating ones [V]. A bare
id means the newest stable version; allowed only for interactive `script test`, refused in stored configs. Ids are checked against
`[A-Za-z0-9._-]{1,100}` before they become folder names (the H1 corpus of S02 replaces this check). Other `#r "Name"` directives are left to Roslyn.
`#load` is not in the spike; it needs a `SourceReferenceResolver` limited to the config repository [R].

### 2.2 Resolver [V unless noted]
- Sources: any mix of v3 index URLs and folders (`Repository.Factory.GetCoreV3`). A folder in global-packages layout works as an offline feed.
  A source that fails is logged and skipped; an error is raised only if nothing satisfies the request. nuget.config is not read (a service account has none);
  sources come from our config. Package source mapping (id patterns per source) is not implemented: add it in S02 [R].
- Closure: breadth first from the script's own references. A package named by the script wins over a transitive request (NuGet's "direct dependency wins");
  among transitive requests the first resolved version stays unless it does not satisfy a later one, in which case the later range is resolved and
  its dependencies are queued. Stale dependencies of a replaced version stay in the closure (harmless extra files; a real graph walk with
  conflict warnings is S02 work).
- Cache: `<root>/<id lower>/<version>/` with the extracted package, the `.nupkg` and a `.nupkg.sha512` (base64, same name as NuGet's global packages folder).
  A download goes to a temp file, is hashed, extracted to `<dir>.tmp-<guid>` and moved into place, so the folder exists only when complete; two processes
  racing lose gracefully. A fixed version already in the cache is never fetched again, which is why the offline run needs no network [V].
- Hash checking: on every use the stored `.nupkg` is re-hashed and compared with the recorded SHA-512 (tamper test [V]). On first download the hash is
  recorded (trust on first use) unless a **pin** `id/version -> sha512` is supplied, in which case a mismatch aborts [V]. nuget.org's flat container does not
  publish package hashes, so the only strong source of truth is a lock file kept with the config (S02 should add `library.lock`, written by `library add`).
- Package signatures: not verified in the spike. `NuGet.Packaging.Signing` can read and verify author and repository signatures, but trust roots on Linux and
  offline revocation checks need work [R]. Recommendation: hash pins from the lock file plus the allow-list; signatures later.
- Target framework: `NuGetFramework` of the running runtime (`net10.0`); `NuGetFrameworkUtility.GetNearest` over the groups of `lib/`, `ref/` and the nuspec dependencies [V].
  A package with only `netstandard2.0` works [V: Npgsql dependencies, Humanizer].
- Assets taken (never `build/`, `buildTransitive/`, `contentFiles/`, analyzers, or tools; no props or targets are evaluated and no install scripts exist in modern NuGet):
  1. managed run-time assemblies: `runtimes/<rid>/lib/<tfm>` for the first RID of the chain that has any, else `lib/<tfm>`;
  2. compile references: `ref/<tfm>` if present, else `lib/<tfm>`;
  3. native files: `runtimes/<rid>/native/*` per file name, first RID of the chain that ships it.
  RID chain (hand-written, `Platform.cs`): `linux-<arch>`, `linux`, `unix`, `any`; musl first tries `linux-musl-<arch>`; `win-<arch>`, `win`; `osx-<arch>`, `osx`, `unix`.
  Verified on glibc [V] and on Alpine for sqlite (`linux-musl-x64` chosen) [V]. Windows and macOS are [R]. NuGet's real `runtime.json` graph is richer
  (for example distro RIDs, `win10-x64`); packages using those rids would be missed. Satellite resource assemblies are ignored.
- Already provided by the host: any assembly whose simple name is in the shared framework (Microsoft.NETCore.App, and Microsoft.AspNetCore.App when the host is
  ASP.NET) is taken from the host, and the package's copy is ignored, for both compile and run [V with System.Text.Json 8.0.5 against the 10.0 runtime].
  This is the SDK's default conflict rule; the consequence is that a package that needs a newer framework-owned assembly than the host has fails at run time
  with a missing-member error (reasoned [R]; the answer is to run on a newer runtime, not to load a second copy). Packages that exist only to carry framework
  references (`NETStandard.Library`, `Microsoft.NETCore.Platforms`) are still downloaded but contribute no assets; S02 should skip a short list of them.
  Non-framework assemblies the host also has (Newtonsoft.Json, NuGet.*) are **not** shared by default: the script gets its own copy in its context [V], so the host's
  version never conflicts. A host that wants a type to cross the boundary (an `IDriver` interface) puts that assembly in the shared set explicitly [R: one-line change in `HostProvides`].
- Allow-list hook: `Func<PackageIdentity,bool>` is called on every chosen id and version before any download or cache use [V].

### 2.3 Loader [V]
- One `ScriptLoadContext : AssemblyLoadContext(isCollectible: true)` per script run (or per library package set; reuse is a policy of S03).
  `Load(AssemblyName)` returns the closure's file for that simple name, otherwise `null`, which falls through to the default context (framework and shared host assemblies).
- `LoadUnmanagedDll(name)` tries `name`, `name.so|dll|dylib`, `lib<name>...` against the closure's native file names and calls `NativeLibrary.TryLoad` on the full path.
  DuckDB.NET.Data.Full (`libduckdb.so`, from the Bindings.Full package) and SQLitePCLRaw (`libe_sqlite3.so`) both loaded this way with no per-assembly resolver [V].
  A native library that depends on another native library by name needs the dependency in the same folder with a suitable rpath, or an explicit pre-load [R].
- Unload: `Unload()` then GC. The managed side was collected for plain scripts, Newtonsoft with primitives, Humanizer and DuckDB [V]; it was **not** collected
  after Newtonsoft serialized a script-declared type, nor for Microsoft.Data.Sqlite or Npgsql [V as an observation, cause not investigated].
  Native libraries are loaded for the life of the process; the spike never frees them and says so in its output. Where strict unload matters (a driver being replaced), the
  answer is a per-run process (the TaskRunner already is one), not the load context.
- The context must be created in a non-async, non-inlined frame; an async method keeps its locals in a state machine that the returned task keeps alive (found and fixed in the spike [V]).
- Roslyn's `InteractiveAssemblyLoader` is bypassed on purpose: the script is compiled to a PE image, loaded with `LoadFromStream` into our context and its
  `Submission#0.<Factory>` method is invoked. Otherwise the loader may place referenced assemblies in the default context [R: not tested, bypassed by design].
  This relies on an internal naming convention of Roslyn's scripting output (stable for years, but undocumented) [V on Roslyn 5.0.0].

### 2.4 Compiler [V unless noted]
- References: the closure's compile references plus the **shared framework implementation assemblies** (`TRUSTED_PLATFORM_ASSEMBLIES` entries under `shared/Microsoft.*`).
  The host application's own assemblies are not visible unless the host adds them (a script cannot reach Sync's internals by accident). `ScriptOptions.Default` with
  default imports `System`, `System.IO`, `System.Linq`, `System.Collections.Generic`, `System.Threading.Tasks`; no debug info.
- Cache: the compiled dll is stored as `<cache>/compiled/<sha256 of source + Roslyn version + compile-reference paths>.dll`. A hit skips Roslyn: 0.3 s against 1-4 s [V].
  Paths contain the version, so a changed package version changes the key.
- Policy: the allow-listed reference set and `ScriptSyntaxGuard` of today's `ScriptCompiler` plug in before `Emit` (S04) as syntax and symbol checks. They remain lints, not a sandbox.
  Scripts run in-process with the host's privileges. See 2.5.
- Language version: Roslyn 5.0.0 here; C# 14 support was not specifically exercised [R].

### 2.5 Security
- No restore-time code execution: NuGet packages' `build/*.props|targets`, `tools/*.ps1` and MSBuild tasks are never run; no MSBuild at all. Analyzers are not loaded.
- Extraction rejects entries that escape the package folder (`..`, absolute) [R: code path checked, no hostile package was run]; ids and versions are validated before use.
- Hash recording and re-check on every use [V], pins [V], allow-list hook [V]. Signature verification: later (2.2).
- What a loaded package can do is everything the process can. `#r` of an arbitrary package is code execution, as with dotnet-script. The control is the allow-list plus a pinned lock file
  plus who may edit the config repository. Say so in the docs.
- Windows service accounts: `Environment.SpecialFolder.LocalApplicationData` for LocalSystem is `C:\Windows\system32\config\systemprofile\AppData\Local`, and a virtual or
  managed service account may have no profile at all. The cache folder is therefore a **setting** (`Scripting:CacheDirectory`) that defaults to a folder under the product's data
  directory (`ProgramData\DbDataKit\...`), writable by the service account only, never `%TEMP%` or the user's profile [R]. The cache is not shared between service accounts.
  Long paths: package folders nest deeply (`Microsoft.CodeAnalysis...`); enable long paths or keep the root short [R].
- Offline mode: a fixed version in the cache needs no network [V]; fixed versions in a local feed folder need no network [V in Docker `--network none`]. Floating ranges need a source;
  with all sources down they fall back to the cache [R, code path only]. A `--offline` switch that skips sources entirely is trivial to add.

## 3. Compared with dotnet-script

| Point | dotnet-script (as W2 describes it; [R] unless stated) | This design |
| --- | --- | --- |
| Needs SDK at run time | yes: `dotnet restore` on a generated project, MSBuild | no [V in `runtime:10.0` container] |
| Offline | local feed through NuGet.config and restore | cache or folder feed, no process [V] |
| Native assets | via the dependency model's runtime assets | `runtimes/<rid>/native` through `LoadUnmanagedDll` [V DuckDB, SQLite] |
| Unload | collectible context in its loader | same mechanism; same limits [V] |
| Conflict rules | full NuGet restore (central graph, lock, prune) | hand-written, simpler, can be wrong in odd graphs |
| `build/` props, analyzers | evaluated by restore/MSBuild (`PackageReference` semantics) | ignored, by design |
| Maturity, maintenance | small open-source project, Roslyn lags .NET releases | ours to maintain: about 550 lines now, more with hardening |
| Features we would otherwise write | `#load`, REPL, debugging, `#!` handling, cache | `#load` and the cache are ours; REPL and debugging not needed |
| Package signature, source mapping | by restore | not done yet |

## 4. Risks and unknowns

Verified: everything marked [V] above, on Linux x64 (glibc) with .NET 10.0.13, the SDK-less container, and an Alpine container for SQLite.

Not verified: Windows (paths, long paths, service account, `.dll` natives, file locking of loaded natives when cleaning the cache), macOS and arm64 RIDs, signature verification,
a hostile package corpus (H1), `#load`, Oracle or other drivers with native dependencies that need sibling libraries, thousands of scripts (memory of un-collected contexts),
concurrent processes writing one cache (the atomic move handles it in theory only), nuget.org authentication or proxies, behaviour on the ASP.NET Core host (shared set includes AspNetCore: [R]).

Biggest risks:
1. **Unload is best effort.** Several real libraries kept the context alive in the spike. Keep the context for the process lifetime for libraries and drivers (as today), and use a process per run for scripts that must be strict.
2. **We own the dependency-resolution rules.** A package graph unusual enough (conflicting majors, RID-specific managed assemblies from distro RIDs, `Microsoft.NETCore.Platforms` runtime graphs) will behave differently from restore. Mitigation: a lock file written by one resolution and read by every later load; a compatibility test over the packages the suite actually uses (Npgsql, MySql, Oracle, DuckDB, SQLite, Arrow).
3. **Package choice limits platforms, not our loader**: DuckDB.NET.Data.Full has no `linux-musl` native [V: failed on Alpine]. The Docker image must be glibc, or use another DuckDB package.
4. **Roslyn scripting internals**: `Submission#0.<Factory>` is not a public API. Mitigation: a single test pins it; the alternative is compiling a normal class library with `CSharpCompilation` and a `Main`-like entry convention.
5. **Native dependency chains** (a native library that links another by name) need the directory on the loader path; not exercised.
6. **Security expectations**: without signatures, the first download of an unpinned package is trust on first use.
7. NuGet.* 7.9.0 and System.Security.Cryptography.Pkcs: NuGet.Packaging 7.x pulls Pkcs 8.0.1, which carries advisories (NU1902/NU1903 under warnings-as-errors);
   the spike pins Pkcs 10.0.13. Versions in the spike may be newer than two weeks; check them before they go into the root `Directory.Packages.props`.

## 5. Work breakdown (maps onto 13a)

S01 (the dotnet-script spike) is replaced by this spike; its "done when" becomes: this report accepted, D9 decided.

| Slice | Content | Size | Notes |
| --- | --- | --- | --- |
| S02 resolver | `DbDataKit.Scripting.Resolver`: directives, sources and source mapping, closure with conflict warnings, cache with lock file and pins, hash and zip-slip checks, the H1 corpus moved in from `Connections.Libraries.Validation`, framework-owned package skip list, RID chain tests, local feed fixture tests | M (about 5-7 days) | the spike code is the starting point (about 330 lines) |
| S03 loader | collectible context, native resolution, shared-assembly set, policy for reuse and unload, tests that load Npgsql and DuckDB from a closure, run a query, unload (managed) and assert the native library stays | S (about 2-3 days) | context lifetime policy is the real decision |
| S04 compiler | `.csx` to a cached assembly, `#load` with a restricted resolver, the allow-list and `ScriptSyntaxGuard` ported, diagnostics with original line numbers, Sync's `ScriptCompiler` tests ported | M (about 5-6 days) | Roslyn/C# 14 check; entry-point convention test |
| extra | Windows pass of the spike (cache path, long paths, service account) before S02 starts | S (1-2 days) | do first; it is the largest unverified area |
| extra | optional: signature verification of `.nupkg` files | S-M | after S02, only if wanted |

The Docker image for Sync then needs only the `runtime` or `aspnet` base image, with ICU or invariant globalization chosen on purpose: Humanizer failed under invariant globalization in the spike ("en-US is an invalid culture").

## 6. Recommendation for D9

**Roslyn scripting + the NuGet libraries (this design), not dotnet-script.** Reasons: it removes the SDK from run time, which was the blocker; DuckDB, SQLite and Npgsql
(native assets, collectible loading, offline operation) all worked in an SDK-less container in under 550 lines; we keep one resolver and one loader for libraries, drivers
and scripts (W2's goal) without depending on a small upstream project. Keep the `DbDataKit.Scripting` interfaces from W2 unchanged, as 13 already
proposes, so a different engine can still be swapped in. Conditions: do the Windows pass of the spike first; exact versions plus a lock file in stored configs; accept that unload is best effort and native libraries are never unloaded.

## 7. Where code is loaded today: does "unload is best effort" matter?

The owner's expectation: custom packages and drivers are mostly loaded in child processes, not in the long-running service, so a leaking unload costs little. Checked against the source on 2026-10-23 (read, not run):

| Product | Loads code at run time? | Where |
| --- | --- | --- |
| DbDataSync | yes | the API process **and** the TaskRunner child; short CLI commands too |
| DbDataProxy | no (no plugin, library or script loading in `src/`; ODBC drivers are native and chosen by the operating system's driver manager) | - |
| DbDataBuild | no (hooks are SQL scripts that are parsed and checked, not compiled code) | - |
| DbDataKit | no (`Connections.Libraries` installs and validates; it does not load) | - |

For Sync the assumption **does not hold today**:

- `DbDataSyncHost` (the API) builds `new LibraryRegistry(repoRoot).LoadAll()`, registers the built-in drivers with the script host, then runs `DriverLoader.LoadDescriptorDrivers` and `LoadCompiledDrivers` at start-up. The library resolver is one `AssemblyLoadContext.Default.Resolving` handler for the life of the process. So every installed library, descriptor driver, compiled driver and script is loaded into the long-running API process.
- The TaskRunner child repeats the same composition (`TaskRunner/Program.cs`) for each run, so runs are isolated by the process: unloading never matters there.
- Library *validation* already runs in a child (`LibraryValidationLauncher`), which is the pattern the owner describes.
- Compiled driver plugins load into a `DriverPluginLoadContext` that is deliberately **not collectible** ("a driver lives for the process; hot-reload is a non-goal"), and libraries load into the default context. So nothing is unloaded today: installing or changing a library, driver or script takes effect in the API after a restart. Best-effort unload in the new loader is therefore not a regression; it is more than Sync does now.

What the API does with loaded code (connection test, schema discovery, previews, provisioning, scripted queries) is the part to decide. Still to do, as slice **S11** in 13a:

1. List every API code path that runs loaded code, and say for each whether it must run in the API or can run in a child (a short-lived probe process like validation, or the TaskRunner).
2. Choose the rule: either "user-supplied code runs only in children, the API loads only built-in drivers and never a custom package" (then unload hardly matters and the API stays clean), or "the API keeps loading it and a change needs a restart". The first is a larger change to Sync's API and the web pages; the second is what exists.
3. If custom drivers are ever allowed in Proxy (plan W2 step 6), apply the same rule: Proxy has no loader today, so the first version should run them in a child process or not at all.
4. Re-run the spike's unload measurements on the context policy chosen (it was not collected for Microsoft.Data.Sqlite or Npgsql, or when Newtonsoft serialized a script-declared type, and the cause is not yet known).
