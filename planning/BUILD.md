# BUILD: Build after its server

## Goal

Build's remaining work as a Workspace project and a Runner package: what its tasks and screens still lack once the Build server is gone. Authoring a
build project in the Workspace is AUTHORING's; the Runner itself is RUNNER's.

## What exists today

- Build's tracking copy on the Runner, its divergences and their resolve, and the console's Tracking page (`docs/ui-editors.md`, the row of Build's
  tracking tables).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| BUILD-1 | Build tracking, the rest, and the effective tracking mode shown per connection and replication; switch-primary as an action | SYNC-5 | M | Playwright: a restored target shows diverged; switch-primary moves the primary and is audited; it refuses while a side is down |

## Shared areas touched

- `DbDataBuild`, the Runner's tracking routes, the console's Tracking page.
