# AUTHORING-18 (work in progress)

## Built so far

- Nothing yet. Plan: a console route `POST api/workspace-catalog/{workspace}/{project}/binding-test` with `{"runner", "channel", "connection", "role"?}`
  beside the reads of the same project: the console asks the Workspace server for the project as the person (may they read it, which package it
  becomes), then asks the Runner's binding test (`POST /api/v1/bindings/{channel}/integration/{package}/test`) as the person. No version is read.

## Open questions

- none yet

## Defaults taken

- none yet
