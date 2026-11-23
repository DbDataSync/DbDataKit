# WORKSPACEISOLATION-2 (work in progress)

## Built so far

- `Shared/src/DbDataKit.Deploy` with its tests (`Shared/tests/DbDataKit.Deploy.Tests`, 37 + the dependency test).
- `RunnerPublishClient.ForLink` in `DbDataKit.Runner.Client`.
- The adapter in `DbDataKit.Authoring.Server` (`WorkspacePublishing`, `WorkspaceGatewayPublish`, `WorkspaceOrigin`, `PublishProducts`, the
  contract route's checks through `ContractPlans`).
- `DbDataWorkspace.Tests`: 197, 0 failed (22 skipped: engines, child programs, the app store); with an app store, `WorkspacePublishTests` and
  `WorkspaceBuildTests` 16, 0 failed.
- The subject file: the -2 row is gone, -3 and -6 depend on nothing, "What bears on WORKSPACEISOLATION-3" names where each part is.

## Not yet

- Gateway's `ManagementWorkspacePublishTests`, the console's `DbDataKit.Server.Servers.Tests`, the Runner's client tests, `DbDataKit.Authoring.Server.Tests`;
  builds of `DbDataSync.Cli` and `DbDataBuild.Cli` (they reference `DbDataKit.Runner.Client`).
- The repository checks.

## Open questions

## Defaults taken
