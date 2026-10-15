# Plan status

## Update (Plan 08)

Implemented in `shared/`: `DbData.Update.Abstractions`, `DbData.Update`, `DbData.Update.Service`, `DbData.Update.Detect`, `DbData.Update.Tools` (dotnet tool `dbdata-update`), `DbData.Update.Updater` (console `dbdata-updater`). Tests: `shared/tests/DbData.Update*.Tests` (all offline).

Done: release/1 manifest, semver + channels, UpdateChecker (min-safe, revocation, downgrade), sources (GitHub/NuGet/Folder/Url), downloader (trusted prefix, resume, size+sha256), ed25519 verification with key rotation, zip/tar-safe extraction, UpdateMode/window, side-by-side layout, update-plan journal with recovery, helper state machine with rollback, install-method detection and appliers, manifest create/sign/verify tool.

Stubbed / not done: real service control and Event Log (Plan 03; `dbdata-updater` has a throwing stub), Authenticode, helper trigger (scheduled task/systemd), cross-process helper mutex, `install.ps1` replacement (phase 6), Scoop/winget generation from manifest, porting Sync `Updates` onto the library (phase 2), reusable-release.yml (sketch only in Tools README), key custody decision.
