# WORKSPACEISOLATION-9: the Workspace service's outbound list (work in progress)

## Built so far

- Nothing yet: audit of the code done. The Workspace's own code connects out in two places today: the push of a workspace
  (libgit2, in process; reachable only for a project with a git remote, which the Workspace does not have yet) and the
  join and announcement to the console (`DbDataKit.Web`). Every NuGet restore runs in a child program (`dbdatarunner`,
  `dbdatasync`, `dotnet`), which is SANDBOX's.

## Open questions

## Defaults taken
