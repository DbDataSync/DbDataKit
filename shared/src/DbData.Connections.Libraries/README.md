# DbData.Connections.Libraries

The validation and planning layer of Sync's NuGet library installer, hardened for finding H1 (`docs/review/SECURITY.md`). Nothing here reaches the network unless an executor is run; the restore sits behind `ILibraryRestoreExecutor`.

- `LibraryValidation`: package id (NuGet grammar, 100 characters), version (exact SemVer 2.0, optional fourth part; no ranges, wildcards, `$(...)`, whitespace or trailing newline), library/driver ids and file names (lower case, no path parts, no `..`, no Windows device names), factory type names, runtime identifiers. Regexes use `[0-9]` and `\z` so Unicode digits and a trailing newline do not slip through.
- `NuGetSourcePolicy`: https only, no credentials, exact match against the configured feeds, nothing that looks like an option. Default feed is the first configured one.
- `ProjectFile.Build`: the restore project is an `XElement` tree, so a value cannot add elements even if validation were bypassed (tested with both). The feed goes on the command line, never into the project.
- `LibraryPaths`: ids resolved and checked as direct children of `libraries/`; `DeleteLibraryDir` removes a link instead of following it; `AssertInside` rejects the root itself and same-prefix siblings.
- `LibraryInstallPlanner.Plan` validates everything and returns a `LibraryInstallPlan` (project XML, argument list, paths) without touching the disk. `LibraryInstaller.InstallAsync(plan, executor)` restores into a temp directory, replaces `lib/` (a failed restore leaves the old one), copies with a per-file prefix check and writes a validated manifest. `DotnetPublishExecutor` runs `dotnet publish` with an argument list (not exercised in tests; no network).
- `LibraryManifest.Parse` validates a planted `library.json` as it would validate input.

Not ported: `KnownLibraries` catalog, the surface/compatibility checkers, factory reflection, the runtime-only image cache, `DbProviderFactories` registration (the loader side).
