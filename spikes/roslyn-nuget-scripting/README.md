# Roslyn + NuGet libraries scripting spike

Runs a `.csx` script whose first lines are `#r "nuget: Id, Version"`: the packages are resolved and downloaded **in process** with
NuGet.Protocol, loaded into a collectible `AssemblyLoadContext`, and the script is compiled and run with Roslyn scripting. No `dotnet
restore`, no MSBuild, no `dotnet nuget`, no `dotnet-script`, no SDK at run time. The plan is
[docs/plans/14-roslyn-nuget-scripting-spike.md](../../docs/plans/14-roslyn-nuget-scripting-spike.md).

This is a standalone solution, not part of `DbDataKit.slnx`. The project file is `RoslynNuGetScripting.proj` (not `.csproj`) so that
`scripts/check_solution.py` does not demand a slnx entry, and `Directory.Packages.props` here switches the root's central package
management off. Both root checks pass with the spike in place.

| File | What it does |
| --- | --- |
| `src/ScriptDirectives.cs` | finds `#r "nuget: ..."` with Roslyn's parser, validates id and version range, blanks the directive |
| `src/PackageResolver.cs` | dependency closure over NuGet.Protocol sources (URL or folder), nearest target framework, asset selection, host-provided filter, allow-list hook |
| `src/PackageStore.cs` | global-packages-style cache, SHA-512 recorded and re-checked on every use, optional pins, zip-slip check, atomic extract |
| `src/ScriptLoadContext.cs` | collectible context: `Load` from the closure, `LoadUnmanagedDll` from `runtimes/<rid>/native` |
| `src/ScriptHost.cs` | compile (cached by content hash), load, run, unload check |
| `src/Platform.cs` | RID fallback chain (glibc, musl, win, osx), native name candidates, shared-framework assembly set |
| `scripts/*.csx` | the demonstrations |

## Build

```
cd spikes/roslyn-nuget-scripting
dotnet build RoslynNuGetScripting.proj
dotnet publish RoslynNuGetScripting.proj -c Release -o publish      # framework-dependent: needs only the runtime
```

Usage: `spike <script.csx> [--cache DIR] [--source URL_OR_FOLDER]... [--allow ID]... [--pin id/version=SHA512]... [--unload-check]`.
Default source is nuget.org. A folder source must have the `id/version/id.version.nupkg` layout (`~/.nuget/packages` has it).

## What was run, and what it printed

Machine: Linux x64, .NET SDK 10.0.113 and runtime 10.0.13 installed, nuget.org reachable. `$S` below is a scratch folder.
Output lines about packages are on stderr, `result:` on stdout; timings vary.

**A. Online, fresh cache, apphost, empty environment (`PATH=/nonexistent`, only `DOTNET_ROOT` set) - DuckDB.NET.Data.Full:**

```
env -i PATH=/nonexistent DOTNET_ROOT=/usr/lib/dotnet ./publish/spike scripts/duckdb.csx --cache $S/c2 --unload-check
downloading DuckDB.NET.Data.Full.1.5.6 / DuckDB.NET.Bindings.Full.1.5.6 / Apache.Arrow.23.0.0 / Apache.Arrow.Scalars.23.0.0 from https://api.nuget.org/v3/index.json
result: answer=42 duckdb=v1.5.6 ALC=duckdb
compiled from cache: False; native libraries loaded: .../c2/packages/duckdb.net.bindings.full/1.5.6/runtimes/linux-x64/native/libduckdb.so; 8259 ms
managed side unloaded: True
```

**B. Offline re-run: same cache, the only source is unreachable (`http://127.0.0.1:9/v3/index.json`):** `result: answer=42 duckdb=v1.5.6`,
`compiled from cache: True`, 356 ms, exit 0. No source was contacted because the cache satisfied the fixed version.

**C. Fresh cache, only a local folder feed (`--source ~/.nuget/packages`), no nuget.org:** the four DuckDB packages were "downloaded"
from the folder, `result: answer=42 duckdb=v1.5.6`.

**D. No child process:** `strace -f -e trace=execve,clone3` of a sqlite run showed exactly two `execve` calls, `env` and `./publish/spike`.
Nothing named `dotnet` was started.

**E. Pure managed (Newtonsoft.Json 13.0.3 + Humanizer.Core 2.14.1), `--unload-check`:**
`result: {"n":3} / 3 minutes / scripting with nuget` and `managed side unloaded: True` (WeakReference dead after `Unload()` and GC).
The host itself ships a Newtonsoft.Json; the script got its own copy through its context.

**F. Microsoft.Data.Sqlite 10.0.13:** `result: sqlite=3.53.3 sum=42`, native `libe_sqlite3.so` from `sqlitepclraw.lib.e_sqlite3/2.1.13/runtimes/linux-x64/native`.
**G. Npgsql 10.0.3 (no server contacted):** `result: npgsql 10.0.3.0 data source for localhost/x`.
Unload check for F and G printed `managed side unloaded: False` (see limits).

**H. Safety checks:**
- appended one byte to a cached `.nupkg`: `error: Newtonsoft.Json 13.0.3: the cached package does not match its recorded SHA-512`
- `--allow Newtonsoft.Json` on the script that also uses Humanizer: `error: Humanizer.Core 2.14.1 is not on the allow-list`
- `--pin newtonsoft.json/13.0.3=AAAA`: `error: ... SHA-512 mbJSvHf... is not the pinned AAAA`
- ranges and floating: `[12.0,13.0.2)` chose Newtonsoft.Json 12.0.1 (lowest applicable, as NuGet does), `2.14.*` chose 2.14.1
- `#r "nuget: System.Text.Json, 8.0.5"`: the package copy is ignored ("provided by the host") and the script ran on the shared framework's 10.0.0.0.

**I. Runtime-only container** (docker is available; `mcr.microsoft.com/dotnet/runtime:10.0`, the published output mounted read-only;
`dotnet --list-sdks` printed nothing and `/usr/share/dotnet/sdk` does not exist there):
```
docker run --rm -v $PWD/publish:/app:ro -v $PWD/scripts:/scripts:ro -v $S/c4:/cache mcr.microsoft.com/dotnet/runtime:10.0 \
  dotnet /app/spike.dll /scripts/duckdb.csx --cache /cache --unload-check
result: answer=42 duckdb=v1.5.6 ALC=duckdb   (7092 ms, downloaded from nuget.org inside the container)
managed side unloaded: True
```
Then with `--network none` and the cache from that run: same result, `compiled from cache: True`, 324 ms. With `--network none`, an empty cache and
`--source /feed` (the host's `~/.nuget/packages` mounted read-only): Npgsql and the Newtonsoft/Humanizer script ran. The sqlite script
failed there only because that folder has no Microsoft.Data.Sqlite 10.0.13 (`No version ... satisfies`); with network it printed
`sqlite=3.53.3 sum=42` in the container.

**J. Alpine (musl) runtime image, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`:** sqlite ran and picked `runtimes/linux-musl-x64/native/libe_sqlite3.so`
(the RID chain works). DuckDB.NET.Data.Full **failed** with "Unable to load shared library 'duckdb'": that package ships no musl native, only
`linux-x64` (glibc); this is a package limit, not a loader bug.

## What this proves and does not prove

- The built program never starts `dotnet` (strace D) and resolved, downloaded and loaded everything with library calls only.
- It does **not** prove the SDK is unnecessary by itself: this machine has the SDK, and the test with `PATH` stripped only shows the program does not look for it.
  The runtime-only container (I) is the real evidence: there, no SDK exists, and DuckDB, Npgsql, Newtonsoft and Humanizer all ran.
- Not run: Windows, macOS, arm64, a Windows service account, package signature verification, `#load`, the real suite's scripts.

## Limits seen

- Unload: only the managed side. Native libraries loaded by the context stay mapped until the process exits (`NativeLibrary.Free` is not called;
  engines such as DuckDB keep threads, so freeing is not safe in general). Unload was collected for pure managed, Newtonsoft (primitives), Humanizer and DuckDB
  scripts, but **not** when Newtonsoft serialized a type declared in the script (cause not investigated; some cache outside the context holds the type),
  nor for Microsoft.Data.Sqlite and Npgsql (not investigated). Treat unload as best effort and use a process per run where it must be strict.
- A script must be run from a non-async frame so the context is not kept by an async state machine (see the remarks in `ScriptHost.Execute`).
- A top-level `using var` is not valid in a Roslyn script; the demo scripts use blocks.
- The first cold run of a script spends its time on download and compile (7-8 s in A and I; not profiled); a warm run with the compiled script cached takes about 0.3 s.
