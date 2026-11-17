# DbDataKit.Quality.Runner

Runs the checks of a dataset and records what they found (plan 23a FQ08). The model is `DbDataKit.Quality`, the evaluator `DbDataKit.Quality.Evaluation` (FQ07), the tables the state module `quality` (`DbDataKit.State`, FQ06). This library joins them:

- **`QualityRunService`** evaluates the checks of one dataset (or all the source has) and writes `check_result` and `check_status` through an `IQualityResults` (`DbDataKit.State`); the block rules are the store's (a `block` check blocks while its latest result is `fail`; an `error` blocks only with `on_error: block` and lifts nothing).
  A `row_count_change` check compares with the previous successful evaluation (`BaselineAsync`); the first evaluation only records.
- **Where the results go** is the `IQualityResults` the caller gives: a `QualityStore` over a state this process holds (the `quality` command, `dbdatabuild connection refresh --check-results <directory>`), or, in a Runner task, the kit's `EngineQuality`
  (`RunnerJob.Quality`), which writes through the runner protocol into the Runner's `quality` module under the run's own id (plan 35 I3). The Runner answers `/api/v1/quality` with them and the console's Quality page reads them there.
- **`IQualitySource`** is what a caller gives: its datasets with checks, how to open each read-only, the tables the checks name, the key columns for sample keys, the last load. `StaticQualitySource` is a fixed list; `ConnectionFilesQualitySource` reads the `checks` of
  connection definitions (format 2, the serving layer's data sources). Build brings its own (`ModelQuality` in `DbDataBuild.Cli`: the models a refresh loaded).
- **`QualityCheckTask` and `QualityCheckJob`** (plan 35 I3a): the `check` task a package declares (`QualityCheckTask.Declare(slots)`: by hand, the input `dataset`, the step `check-result`) and the job it runs (`QualityCheckJob`, a `RunnerJob` the package derives from with `[RunnerTask("check")]`, giving its `IQualitySource` in `SourceAsync`): the checks of the package's datasets are evaluated again, read only, with the package's own slots, and recorded through the kit's `Quality` exactly as after a load, so a block clears when the checks pass; nothing is loaded. Build's refresh package declares it (`CheckJob`); Sync and Store packages declare the same task once they have a source. The Runner's `POST /api/v1/quality/run` starts it for a dataset (the console's Quality page and the Workspace's Runs tab offer Re-check).
- **`QualityAlertWatcher`** and **`DeliveryQualityAlerts`** send an alert once per failure and once per recovery (FQ13); the console runs the watcher over the servers' overviews.

## Who evaluates what (plan 35 I3)

A dataset's checks are evaluated in the process of the Runner task that loads it, after its loads, with the read login that task already holds: a task never gets the files or the logins of another package to evaluate its checks. Today that is
Build's `refresh`, and again on request by the same package's `check` task (plan 35 I3a). The `checks:` of a Sync replication, of a Store model and of a Gateway's data sources have no evaluator yet (`planning/DATAQUALITY.md`).

### Re-check without a load

The `check` task re-evaluates the checks of a package's datasets with the package's own slots and read login, records the results exactly as after a load,
and loads nothing. Example: Build project `sales`, model `orders` has a blocking `not_null(customer_id)`; the 2:00 refresh records 40 failing rows, so
`orders` is blocked and `orders_summary` waits; at 10:00 someone fixes the rows and presses Re-check; the check passes, the block clears, and the next
refresh loads `orders_summary`. No other package's files or logins are used, which keeps each task to what its own package may touch.

### No quality system of its own

There is no separate quality package or service: the in-host quality task wrapper (the `quality-system` package) is gone, and evaluation lives in the
packages that own the data. The shared libraries (`DbDataKit.Quality`, `.Quality.Evaluation`, `.Quality.Runner`, `.Quality.Cli`) are what every package
composes. A general data quality system, separate from the packages, is a possible later product, not part of the suite today.

## Opening the `quality` module

| Who | Opens | Why |
| --- | --- | --- |
| a Runner | `QualityModule.GroupModule` (scope group, in `RunnerNames.EngineModules`) | the results of the checks its group's tasks evaluated |
| a results directory of Build's command line | `QualityModule.Module` (scope install) | the results of `--check-results` |
| the console | `QualityModule.Module` | what its alert watcher has already told people (`check_alert`) |
| the serving layer | `QualityModule.GroupModule` | the instances of a group share one result |

## The shared command

`DbDataKit.Quality.Cli` has the noun `quality` (`run`, `show`); see its README.
