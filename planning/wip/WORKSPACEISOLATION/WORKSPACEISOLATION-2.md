# WORKSPACEISOLATION-2 (work in progress)

## Built so far

- Nothing yet: reading the code the list marks **Moves**.

## Plan

- New library `Shared/src/DbDataKit.Deploy` (tests `Shared/tests/DbDataKit.Deploy.Tests`): the moved code takes an `IDefinitionsLink` (the link it
  calls through) and a `ConsoleIdentity` (the key it signs definitions with, and the id written as `managedBy`).
- `DbDataKit.Runner.Client` gains `RunnerPublishClient.ForLink(IDefinitionsLink)` so the Runner calls can go through a given link.
- `DbDataKit.Authoring.Server` keeps its public surface as an adapter: it finds the target and the key, makes a pinned link, calls the library and
  turns its `DeployException` into a `WorkspacePublishException`.

## Open questions

## Defaults taken
