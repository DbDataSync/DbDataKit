# DbDataKit.Quality.Runner

Runs the checks of a dataset and records what they found (plan 23a FQ08). The model is `DbDataKit.Quality`, the evaluator `DbDataKit.Quality.Evaluation` (FQ07), the tables the state module `quality` (`DbDataKit.State`, FQ06). This library joins them:

- **`QualityRunService`** evaluates the checks of one dataset (or all the source has) and writes `check_result` and `check_status` through an `IQualityResults` (`DbDataKit.State`); the block rules are the store's (a `block` check blocks while its latest result is `fail`; an `error` blocks only with `on_error: block` and lifts nothing).
  A `row_count_change` check compares with the previous successful evaluation (`BaselineAsync`); the first evaluation only records.
- **Where the results go** is the `IQualityResults` the caller gives: a `QualityStore` over a state this process holds (the `quality` command, `dbdatabuild connection refresh --check-results <directory>`), or, in a Runner task, the kit's `EngineQuality`
  (`RunnerJob.Quality`), which writes through the runner protocol into the Runner's `quality` module under the run's own id (plan 35 I3). The Runner answers `/api/v1/quality` with them and the console's Quality page reads them there.
- **`IQualitySource`** is what a caller gives: its datasets with checks, how to open each read-only, the tables the checks name, the key columns for sample keys, the last load. `StaticQualitySource` is a fixed list; `ConnectionFilesQualitySource` reads the `checks` of
  connection definitions (format 2, the serving layer's data sources). Build brings its own (`ModelQuality` in `DbDataBuild.Cli`: the models a refresh loaded).
- **`QualityAlertWatcher`** and **`DeliveryQualityAlerts`** send an alert once per failure and once per recovery (FQ13); the console runs the watcher over the servers' overviews.

## Who evaluates what (plan 35 I3)

A dataset's checks are evaluated in the process of the Runner task that loads it, after its loads, with the read login that task already holds: a task never gets the files or the logins of another package to evaluate its checks. Today that is
Build's `refresh`. The `checks:` of a Sync replication, of a Store model and of a Gateway's data sources have no evaluator yet (plan 35, the I3 row).

## Opening the `quality` module

| Who | Opens | Why |
| --- | --- | --- |
| a Runner | `QualityModule.GroupModule` (scope group, in `RunnerNames.EngineModules`) | the results of the checks its group's tasks evaluated |
| a results directory of Build's command line | `QualityModule.Module` (scope install) | the results of `--check-results` |
| the console | `QualityModule.Module` | what its alert watcher has already told people (`check_alert`) |
| the serving layer | `QualityModule.GroupModule` | the instances of a group share one result |

## The shared command

`DbDataKit.Quality.Cli` has the noun `quality` (`run`, `show`); see its README.
