# SYNCAUTHORING-11 (work in progress)

The set's own `discover` and `describe`, for a connection whose driver is Sync's own, started through the console's catalog read.

## Built so far

- nothing yet

## Plan

- Sync TaskRunner: tasks `discover` and `describe` in the set package (Sync's `MetadataService` behind `discover`, a zero-row probe behind
  `describe`), answering in steps of the same names and shapes as `dbdatakit-metadata`.
- Console: `POST .../read` and `.../describe` take `"reader": "package"` and run the set's task of the newest version the channel holds.
- Designer: Refresh and Describe columns ask with `reader: package` for a connection whose engine the built-in package does not open, or that
  binds a `metadataProvider` script; a channel holding no build offers **Build and stage for dev** (a build, then the console's deploy).

## Open questions

## Defaults taken
