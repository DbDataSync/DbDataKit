# DbDataKit.Quality.Evaluation

The evaluator of the `check/1` checks (plan 23a FQ07). One SELECT per check, built with `DbDataKit.Dialect` and sent over a read-only connection; the answer is the measure and a few sample keys.
The model is `DbDataKit.Quality`; the results are written by the host (FQ08, `DbDataKit.Quality.Runner`) through the state module `quality`. This library touches no state.

```csharp
var evaluator = new CheckEvaluator(dialect);
var results = await evaluator.EvaluateAsync(checks, ReadOnlyConnections.Opener(provider, definition, login), check => new EvaluationContext { ResolveTable = name => tables.GetValueOrDefault(name) });
```

## The seven kinds, one statement each

See the summary of `CheckEvaluator`. SQL Server, PostgreSQL and DuckDB run the same checks over the same rows with the same result (`DbDataKit.Quality.Evaluation.Tests`); MySQL, MariaDB and Oracle follow the dialect's own conformance.

## What a result is

- `Pass`, `Fail` or `Error`. An error is a check that could not run: a connection that is down, a missing table or column, a login that may not read, a statement that ran past `EvaluationLimits.StatementTimeout`, an accepted-values list longer than `MaxAcceptedValues`. It is never a pass and never an exception.
- `Measured` and `Threshold` in the unit of the kind: failing rows, seconds of age, percent of change. `Observed` is the raw reading (the row count, the baseline of the next `row_count_change`).
- **Sample keys** (at most 10): one failing row, or one duplicated value, as `column=value, column=value`, **only the columns that name it**: the dataset's key columns when the host gives them (`EvaluationContext.KeyColumns`), otherwise the check's own columns where those are what failed
  (`unique`, `accepted_values`, `range`, `referential`), and none for `not_null`. No other column is selected, so nothing else can leave the engine. Values are cut at 80 characters.
- The message of an error never repeats a data value: only a missing table or column keeps the engine's words (they are identifiers); everything else is the class of the failure and its code.

## Limits

A statement has a timeout (default 60 s, as the command's timeout and a cancellation), reads at most as many rows as sample keys (the row limit is part of the statement), and an `accepted_values` list is limited to 500 values. A limit reached is an `Error` whose message says `limit reached`.
