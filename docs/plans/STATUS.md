## Secrets (Plan 02)

Implemented under `shared/`: `DbData.Secrets.Abstractions`, `DbData.Secrets`, `DbData.Secrets.Windows`,
`DbData.Secrets.Keyring` (stub), `DbData.Secrets.Cli`, each with tests in `shared/tests/`.
Stubbed: no concrete OS-keyring backend (Phase 0 wrap-vs-fold decision on `ClrKernel.Core.Secrets` pending),
`import-env` CLI command, cloud providers. Unverified: exact Sync env-var sanitisation for
`DBDATASYNC_SECRET_<REF>`; Unix branch of `AclHelper` (tests skip on Windows).
Run tests: `cd shared/tests/<Pkg>.Tests && dotnet test` (needs `shared/global.json` MTP runner).

## Odbc (Plan 04 Part A)
Branch worktree-agent-afbf86949a65d1ed2. shared/src/DbData.Odbc.Metadata + tests: copied from DbDataProxy c08355f (no history split), hardened (options, row cap, timeouts, policy, Build/Quote, session, SQLWCHAR probe, LibraryImport), added TypeInfo/Procedures/driver inventory/diagnostics, PublicAPI baseline. 129 tests green. Not done: Proxy switch to the package, Linux/macOS live verification, Part B.
# Plan status

## Update (Plan 08)

Implemented in `shared/`: `DbData.Update.Abstractions`, `DbData.Update`, `DbData.Update.Service`, `DbData.Update.Detect`, `DbData.Update.Tools` (dotnet tool `dbdata-update`), `DbData.Update.Updater` (console `dbdata-updater`). Tests: `shared/tests/DbData.Update*.Tests` (all offline).

Done: release/1 manifest, semver + channels, UpdateChecker (min-safe, revocation, downgrade), sources (GitHub/NuGet/Folder/Url), downloader (trusted prefix, resume, size+sha256), ed25519 verification with key rotation, zip/tar-safe extraction, UpdateMode/window, side-by-side layout, update-plan journal with recovery, helper state machine with rollback, install-method detection and appliers, manifest create/sign/verify tool.

Stubbed / not done: real service control and Event Log (Plan 03; `dbdata-updater` has a throwing stub), Authenticode, helper trigger (scheduled task/systemd), cross-process helper mutex, `install.ps1` replacement (phase 6), Scoop/winget generation from manifest, porting Sync `Updates` onto the library (phase 2), reusable-release.yml (sketch only in Tools README), key custody decision.
# Plan status

## Config (Plan 01)

- Done (shared/src, tests green via `dotnet test --project shared/tests/<Pkg>.Tests`): DbData.Config.Yaml (reader, merge, editor, diff, fuzz), DbData.Config (attributes, binder, layering, env/CLI, migrations, JSON Schema), DbData.Config.Hosting (MEC provider, options validation, ShowOrigin).
- Not done: DbData.Config.Testing; products do not consume the packages yet (phases 1, 5, 6); JSON input format behind the node tree; git-commit backup mode; `schema: kind/int` form; CLI commands (`config show/migrate/schema`) are library APIs only.
