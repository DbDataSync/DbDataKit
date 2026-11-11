# DbDataKit.Quality.Cli

The noun `quality` of the shared command surface (plan 23a FQ08), over a host's datasets with checks. A host adds it to its command line with `QualityNoun.Create(source, store)`, where `source` is its `IQualitySource` and `store` opens the `quality` state module as a `QualityStore`.

```
<program> quality run [--dataset <identity or name>]
<program> quality show [--dataset <identity or name>] [--history <n>] [--blocking]
```

- `quality run` evaluates the checks of a dataset (or of every dataset the host has checks for) over a read-only connection and records each result and the check's status and block, exactly as the runner task `quality` does (`DbDataKit.Quality.Runner`). Exit code 0: every check passed;
  1: a check failed or could not run (or the dataset is unknown); 2: used wrongly. With `--format json` the document's `data` is `{ datasets: [{ dataset, checks: [{ id, kind, severity, status, measured, threshold, observed, sample_keys, message, blocking }] }], evaluated, passed, failed, errors, blocking }`.
- `quality show` is the latest result of each check (status, since when, measured against allowed) and what blocks now, with `--history <n>` the last n evaluations of each, with `--blocking` only the checks that block dependents. It reads state only (nothing is evaluated).

The JSON schema of each verb's `data` is in the router's output schema (`NounRouter.OutputSchema()`), as for every shared noun.
