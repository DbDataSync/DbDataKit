# WORKSPACEISOLATION-3: the control service deploys a build

## Built so far

- The Workspace serves a build's items and record (`.../builds/{b}/contents`, `.../files/{sha256}`; `BuildContents`, `BuildRecord` in `DbDataKit.Deploy`).
- The signed message holds the build record (`dbdatakit-definition/2`); the shared publish API verifies it, stores it (`definition_version.build_record`),
  returns it, the gate verifies it again, a promotion carries it.
- The console deploys (`ConsoleDeploys`, `POST /api/deploys`, `GET /api/deploys`), signs with `ConsolePublisherKeyStore`, keeps the `deploys` module.
- Tests: `ConsoleDeployTests` (Sync, Build, Store to a runner in process; refusals), `WorkspaceBuildContentsTests`, Deploy, State, State.Api tests.

## Left

- Moved to the subject file: the -3 row now says what is left (Gateway deploys end to end, apps), the defaults are WORKSPACEISOLATION-P8 to P12.
- SQL Server and PostgreSQL runs of the State suites (`DeployStoreTests`, the definitions migration).
