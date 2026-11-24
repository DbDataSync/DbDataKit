# WORKSPACEISOLATION-17 (work in progress)

## Built so far

- `ConsoleDeploys.DeployAsync` and `ConsoleDeployRuns.FindAsync` ask the Workspace server for the project as the person
  (`ConsoleDeploys.ProjectRefusalAsync`, `GET /api/v1/projects/{project}`); the approver check is gone. Tests in `ConsoleDeployTests` (Ben; Pat).
- `docs/publishing.md` (step 2, step 6, an example, the role table), `docs/apps.md` (Publish), a release note.

## Not built yet

- The subject file: remove the row; the open question below as a proposal.

## Open questions

- Who may list the deploys (`GET /api/deploys`)? Example: Pat may not read `orders`, yet the list answers Pat with each deploy of `orders` and its
  build record. Becomes WORKSPACEISOLATION-P30.

## Defaults taken

None.
