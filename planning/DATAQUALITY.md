# DATAQUALITY: the checks of every dataset are evaluated

## Goal

The checks stored with a Sync replication, a Store model, a Gateway data source and a semantic catalog are evaluated, recorded and shown like a Build
model's, and a blocking check keeps the last good data served. The shared quality libraries stay; a general data quality system is a future need, not
this subject (owner, 2026-11-16).

## What exists today

- `check/1` and the shared libraries `DbDataKit.Quality`, `.Quality.Evaluation`, `.Quality.Runner`, `.Quality.Cli`.
- A Build `refresh` task evaluates the checks of the models it loaded; the Runner keeps the results per group (`QualityModule.GroupModule`) and answers
  `GET /api/v1/quality`; the console's Quality page reads them. The `check` task (`QualityCheckTask`) re-evaluates without a load; Build's refresh package
  declares it.
- Stored and not evaluated: `checks:` of a replication's `task.yaml` (the Sync server that evaluated them was deleted), of a Store model (`model/2`), of
  a Gateway data source (`connection/2`, `ConnectionFilesQualitySource`) and of a semantic catalog.
- Build holds a model downstream of a blocked dataset (`LoadStatus.SkippedBlocked`); the publish API does not refuse a model that reads a blocked dataset.
- The Gateway does not report freshness and does not hold a new version back while its check blocks.
- No re-check runs on a schedule.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| DATAQUALITY-1 | Sync: the drain task evaluates the `checks:` of the targets it wrote after its load, and the replication set's package declares the `check` task with a Sync `QualityCheckSource` | none | M | a Sync conformance test (SQL Server and PostgreSQL): a drain blocks on a failing not-null check; the data is fixed; `check` clears the block; nothing is reloaded |
| DATAQUALITY-2 | Store: a model's package declares the `check` task with a Store source over the model's read slot | none | M | a Store conformance test: `check` records a failing check of an entity under its run and the console's Quality page shows it |
| DATAQUALITY-3 | Gateway data sources and semantic catalogs: their `checks` are evaluated where DATAQUALITY-P1 says | DATAQUALITY-P1 | M | a data source with a freshness check past its target shows `fail` on the console's Quality page |
| DATAQUALITY-4 | The Gateway reports freshness for what it serves: per served dataset the age of its newest data and of its published version, in the Flight SQL metadata and the management API | DATAQUALITY-3 | M | two instances share one result; a dataset with a freshness target reports `fail` once past it; the Flight SQL metadata carries the age |
| DATAQUALITY-5 | A dataset whose check blocks keeps serving its last good published version; the new version is held until the check passes or an administrator releases it (audited) | DATAQUALITY-4 | M | two instances: a version published while its check fails is staged and held; queries return the old rows and the provenance names the old version; on a pass it activates |
| DATAQUALITY-6 | A scheduled re-check, where DATAQUALITY-P2 says | DATAQUALITY-P2 | S | a re-check on a schedule records results without a load (fake clock) |

## Needs from other subjects

- PUBLISHING-5: the publish and promotion refusal `blocked_by_check` for a definition that reads a blocked dataset.

## Proposed decisions

### DATAQUALITY-P1: where a Gateway data source's checks run (awaiting the owner)

Option A: a Runner `check` task over a read slot bound to the same database (Work holds a read login too). Option B: the Gateway evaluates in-process on
a timer with its own read login. Scenario: data source `sales` has `freshness: 6h` on `orders`. Under A the Runner's task reads `orders` at its schedule
and the result is in the Runner's `quality` module; under B the Gateway reads it and keeps the result in its group's state. The isolation rule puts
scheduled work on Work. Recommendation: A.

### DATAQUALITY-P2: where a re-check schedule is written (awaiting the owner)

Option A: `checks: {every: 1h}` in the project file (Build, Sync, Store), published with the package. Option B: an operator sets a schedule on the
Runner for any manual task. Scenario: an engineer wants `orders` re-checked hourly. Under A the change is a commit reviewed with the model; under B it is
set on the Runner's screen and is not in git. Recommendation: A.

## Shared areas touched

- `DbDataKit.Quality*`, `DbDataKit.Runner.Kit` (`EngineQuality`), the Sync and Store runtime packages, `DbDataKit.Serving.*` (freshness, holds).
- `docs/formats.json` if a `checks:` schedule key is added, `docs/ui-editors.md` (check rows).
