# DbDataKit.Quality.Runner

Runs the checks of a dataset and records what they found (plan 23a FQ08). The model is `DbDataKit.Quality`, the evaluator `DbDataKit.Quality.Evaluation` (FQ07), the tables the state module `quality` (`DbDataKit.State`, FQ06). This library joins them:

- **`QualityRunService`** evaluates the checks of one dataset (or all the source has) and writes `check_result` and `check_status` through `QualityStore`; the block rules are the store's (a `block` check blocks while its latest result is `fail`; an `error` blocks only with `on_error: block` and lifts nothing).
  A `row_count_change` check compares with the previous successful evaluation (`QualityStore.Baseline`); the first evaluation only records.
- **`IQualitySource`** is what a host gives: its datasets with checks, how to open each read-only, the tables the checks name, the key columns for sample keys, the last load. `StaticQualitySource` is a fixed list; `ConnectionFilesQualitySource` reads the `checks` of
  connection definitions (format 2, the serving layer's data sources). Build, Sync, DbDataStore and the serving layer bring their own (FQ09 to FQ12).
- **`QualitySystemTasks`** is the system package `quality-system` with the task `quality`: it runs in the host's process (like the console's `console-system`), on request, on a schedule (`QualityTaskSettings.Every`) and after another task's run (`AfterRuns`, each `package/task`: the manifest's `after-run` trigger).
  Publish `QualitySystemTasks.Manifest(settings)` on the host's system channel and wrap the executor in `QualityTaskRunner`. The input `dataset` names the dataset a scheduled or triggered run evaluates; empty is every dataset of the host.
  A run ends `succeeded` when every check passed, `completed with failures` when a check failed or could not run, `failed` when the task itself could not (an unknown dataset).

## Opening the `quality` module

| Who | Opens | Why |
| --- | --- | --- |
| the console, Build, Sync, DbDataKit Store, a runner | `QualityModule.Module` (scope install) with the modules it has | one copy per install: the results of what that install evaluates |
| the serving layer | `QualityModule.GroupModule` (scope group) | the instances of a group share one result; two groups on one database each have their own |

A host that runs the task also opens `runs` (the run record) and the runner's modules. Opening the module is not done by any host yet (plan 23a FQ09 to FQ12 do it, each for its own product).

## The shared command

`DbDataKit.Quality.Cli` has the noun `quality` (`run`, `show`); see its README.
