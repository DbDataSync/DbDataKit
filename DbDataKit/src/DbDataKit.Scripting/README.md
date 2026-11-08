# DbDataKit.Scripting

Our own scripting (plan 14; decision D9): `.csx` scripts and libraries that ask for NuGet packages with `#r "nuget: Id, 1.2.3"` and run with only the .NET runtime installed, no SDK and no MSBuild. The resolver (S02), the loader (S03) and the compiler (S04) are here; Sync, Proxy and Build adopt them slice by slice (S05-S07).

| Type | What it does |
| --- | --- |
| `ScriptDirectives` | finds `#r "nuget:"` directives by parsing the script with Roslyn (a directive in a comment or a string is not one) and blanks them so line numbers survive. Stored scripts name exact versions; a range, a wildcard or a bare id is for interactive use |
| `PackageResolver` | requests to a `Closure` on disk: the nearest target framework's dependencies, NuGet's version rules, a package the script names itself wins over a transitive request (a warning says so), only what the final versions reach is taken, framework-owned packages are skipped. Run-time and compile assemblies and the native files of the best runtime identifier; `build/` props, analyzers and tools are never evaluated |
| `PackageSource` | a v3 index URL or a folder laid out as NuGet's global-packages folder is (`id/version/id.version.nupkg` with the `.nuspec` and `.nupkg.sha512` beside it), so an offline feed is a folder, with source mapping: when any source has id patterns, a package comes only from the sources mapped to it |
| `PackageStore` | the cache, `root/<id>/<version>/`: extracted package, the `.nupkg` and its SHA-512; written by an atomic rename, hash checked on every use, an entry that would leave its own folder refuses the package |
| `LockFile` | the hash of every package one script or library uses, in the configuration repository beside it (`libraries/<id>/library.lock`, `scripts/<name>.lock`; `scripting-lock/1` JSON, strict reader): the first resolution writes it, every later download or stored package must match, and a mismatch is refused naming the package, the version and the file. `RequirePinned` refuses a package with no pin |
| `ClosureLoadContext` | a collectible load context for one closure: managed assemblies from the closure, native libraries by the file names a DllImport can mean, a name the host shares answered by the default context so the types are the same on both sides |
| `ClosureLoader` | what a host holds per library, driver or script: load under a key (loading again replaces and unloads the earlier one), unload, and `UnloadedAsync` to ask whether the managed side was collected. Unloading is best effort: it needs every reference to the context's types dropped, and a native library stays loaded for the life of the process, so a changed native package needs a restart |
| `CsxCompiler` | a `.csx` source to a cached assembly with Roslyn scripting, against the shared framework, the host's contract assemblies and the script's own closure. `#load` only through `RestrictedSourceResolver` (`.csx` files inside one root; absolute paths, `..` and links out are refused), a file `#r` is refused, diagnostics carry the script's own file and line (directive lines are blanked, never removed), and `ScriptSyntaxGuard` runs before anything is emitted. The cache key covers the text, the loaded files, the compile references and Roslyn's version. `RunAsync` loads the image into the caller's load context and calls Roslyn's `Submission#0.<Factory>` (an undocumented convention, pinned by a test) so no package assembly lands in the default context |
| `ScriptSyntaxGuard` | a lint that refuses banned namespaces (default `System.IO`, `System.Net`, `System.Diagnostics`, `System.Reflection`, `System.Runtime.InteropServices`) with the reason; not a security boundary |
| `LibraryValidation` | the checks of the hostile-input corpus (H1) for package ids, exact versions, names and type names, moved here from `Connections.Libraries` |

Nothing here runs package code: no install scripts exist, no MSBuild runs, analyzers are not loaded. Loading a package into a process is code execution in that process; the controls are the allow-list, the pinned lock file and who may edit the configuration.

The cache belongs under the product's data directory (`PackageStore.DefaultRoot`), never `%TEMP%` or a user profile, which a service account may not have. **Not yet verified on Windows** (long paths, the service account): the plan asks for that pass before S02 is relied on there.

## The lock file (`scripting-lock/1`)

Every script, library and driver that asks for packages is pinned by a lock file beside it in the configuration repository, committed with it: `libraries/<id>/library.lock`, `drivers/<id>/driver.lock`, `scripts/<name>.lock`. The first resolution writes it; every later download or stored package must match.

```json
{ "schema": "scripting-lock/1", "packages": { "Npgsql/9.0.3": "<base64 SHA-512 of the .nupkg>", ... } }
```

The key is `<id>/<normalized version>`; the file holds exactly the closure's packages and is rewritten only when it changes (a version change replaces the old pin; a script with no packages writes none). The reader is strict: a key other than `schema` and `packages`, a `packages` that is not an object or a hash that is not text is an error naming the file and the key's path (`$.packges`). A package whose bytes differ from its pin is refused with `PackageIntegrityException` naming the package, the version and the lock file, and nothing of it is stored (a script reports it as a compile diagnostic). An offline load works when the stored hashes match the pins; a repository with no lock file keeps working and the next resolution creates it.

Limits: the key is `schema`, not `schemaVersion` (this is a JSON file, not one of the YAML config files); a compiler built with no scripts folder has nowhere to keep a lock file and trusts the first download (the API, the CLI and the TaskRunner all pass the folder); a script that is validated and never saved leaves an untracked `<name>.lock` in the working tree; only a script's own `#r` lines are pinned (a `#load`ed script's are not resolved by the compiler).

## Running loaded code

Loading a package into a process is code execution in that process, and unloading is best effort (see `ClosureLoader`): a native library stays loaded for the life of the process, and a managed context is collected only when every reference to its types is gone. So a custom driver that Sync or the Proxy adopts runs **isolated**: in a child process, or in a collectible load context where its limits are accepted. The TaskRunner already runs each run in a process of its own, so unloading never matters there. What the API loads and runs itself today (library, driver and script loading, connection tests, schema discovery, previews, scripted queries) is not isolated and nothing is unloaded; the owner chose not to retrofit isolation or unloading into the existing paths, only to add it as those paths are refactored or when a real problem shows up. The Proxy has no loader and gets none without isolation.
