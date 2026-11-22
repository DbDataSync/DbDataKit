# AUTHORING-21 (work in progress)

Branch `backup/authoring-21`, started from `backup/authoring-23` (76e655868), not yet on main.

## Built so far

- Workspace: `GET /api/v1/projects/{project}` (viewer of the project): the project and `package` (the package it becomes on a Runner; null for an app
  or a Store project).
- Console: `ConsoleCatalogRead` and `CatalogReadController`:
  `GET api/workspace-catalog/{workspace}/{project}/channels?runner=&connection=`,
  `POST api/workspace-catalog/{workspace}/{project}/read`, `GET api/workspace-catalog/{workspace}/{project}/read/{run}?runner=&channel=&connection=`.

## Not built yet

- Tests (console unit tests with fakes, the real-servers test).
- Docs: `docs/authoring-metadata.md`, `docs/authoring-parity.md`.

## Defaults taken

- (not a default, a fact to keep) The slot read is the one the package makes of the connection's name: Sync's rule (`ConnectionSlots.NameOf`, moved
  from `ReplicationSetPackage.SlotNameOf` to `DbDataKit.Runner.Abstractions`); Build's connection names are already slot names.
- The follow call does not check that the run was started for this project (the Runner keeps no inputs on a run): any discover run of the channel can
  be followed by a person with access to some project, as the Runner's own steps route already lets a viewer of the Runner read it.
