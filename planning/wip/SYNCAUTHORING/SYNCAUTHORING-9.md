# SYNCAUTHORING-9 (work in progress)

## Built

- `SetPreflight` and `SetPreflightJob` in `DbDataSync.TaskRunner/Runner`: the set's manual read-only task `preflight`, declared by
  `ReplicationSetPackage` with `Preflight.Declare` over every slot of the set; `RunnerEntry` runs it; a replication named `preflight` is refused.
- Tests: `SetPreflightConformanceTests` (SQL Server, PostgreSQL), `DrainOnTheRunnerTests.ThePreflightTask_...` (the real runner), manifest tests.

## Not built yet

- the console deploy to `prod` with Sync's real preflight (Servers.Tests);
- docs (`docs/authoring-metadata.md`, `docs/publishing.md` table row "A Sync set").

## Defaults taken

(written out as proposals when the branch is ready)
