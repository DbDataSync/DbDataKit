# WORKSPACEISOLATION-3: the control service deploys a build

## Plan

1. The Workspace serves what a build holds to send (each item: product, route, files by SHA-256, the hash of its list of files) and its build
   record (server, project, workspace, build, kind, commit, the validators' result), and each file by hash.
2. The signed message can hold a build record (`dbdatakit-definition/2`); the shared publish API verifies it, stores the record with the version
   and returns it; promotion carries it; instances verify it again at preparation.
3. The control service pulls a build's item, checks it, deploys it as the person with its publisher key, and keeps the deploy record.
4. Tests: Sync to a real Runner (dev, slot bound) through the console; Store and Build cases; refusals.

## Built so far

Nothing yet.

## Open questions

## Defaults taken
