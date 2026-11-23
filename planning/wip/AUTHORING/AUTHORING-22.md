# AUTHORING-22 (work in progress)

## Built so far

Nothing yet: the code was read (`ConsoleDeploys`, `ConsoleDeployRuns`, `DbDataKit.Deploy`, Build's `ViewJobs.cs`).

## Plan

- A shared declaration of the `preflight` task and its findings step, read by the console.
- Build: `preflight` runs `connection status` for each connection the plans use; Store: the `deploy` dry run and the `contract` plan.
- The console's deploy to `prod` with serve: stage, run `preflight` on the staged version, activate when no error; an error refuses activation until confirmed.
- Re-check on the Runs tab through `/api/deploys/{id}/runs`.

## Open questions and defaults

None yet.
