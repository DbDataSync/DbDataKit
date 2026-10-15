## Secrets (Plan 02)

Implemented under `shared/`: `DbData.Secrets.Abstractions`, `DbData.Secrets`, `DbData.Secrets.Windows`,
`DbData.Secrets.Keyring` (stub), `DbData.Secrets.Cli`, each with tests in `shared/tests/`.
Stubbed: no concrete OS-keyring backend (Phase 0 wrap-vs-fold decision on `ClrKernel.Core.Secrets` pending),
`import-env` CLI command, cloud providers. Unverified: exact Sync env-var sanitisation for
`DBDATASYNC_SECRET_<REF>`; Unix branch of `AclHelper` (tests skip on Windows).
Run tests: `cd shared/tests/<Pkg>.Tests && dotnet test` (needs `shared/global.json` MTP runner).
