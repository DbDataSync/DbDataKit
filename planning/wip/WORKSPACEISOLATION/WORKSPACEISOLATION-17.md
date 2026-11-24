# WORKSPACEISOLATION-17 (work in progress)

## Built so far

- Nothing yet. Plan: `ConsoleDeploys.DeployAsync` and `ConsoleDeployRuns.FindAsync` drop the console's own check of the approver (or viewer) role on
  the Workspace server and ask the Workspace server, as the person, for the project (`GET /api/v1/projects/{project}`); its refusal is passed on in
  its words. The target's roles decide the rest (the Runner asks for publisher, operator or administrator).

## Not built yet

- The test of the "Done when" (Ben, a viewer of the project and a publisher and operator on the Runner).
- `docs/publishing.md`: step 2 of a deploy and the role table of "Runs of a deployed build".

## Open questions

None yet.

## Defaults taken

None yet.
