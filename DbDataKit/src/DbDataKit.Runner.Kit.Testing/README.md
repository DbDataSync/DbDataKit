# DbDataKit.Runner.Kit.Testing

Test a runner job without a runner (plan 28b section 10). `RunSimulator<TJob>` runs a job in memory over the **real stores** (progress, steps, units, payloads, runs) on a temporary SQLite database, so the
semantics a test sees are the kit's own: steps answer from memory, finished units are skipped, a checkpoint moves only with the run's outcome, and an in-doubt step is recovered the way the job said.

The simulator keeps one **attempt chain**: every `RunAsync()` is the next attempt, so a retry finds what the earlier attempts left. A crash is an attempt's outcome, not an exception: its run is recorded
`Interrupted` and its started steps are in doubt, as the runner would leave them.

## Usage: the daily drop

A file that disappears from the server when it is downloaded, a crash after the copy, and the retry:

```csharp
var sim = new RunSimulator<DailyDrop>(input: new DailyDropInput(new DateOnly(2026, 11, 7)), slots: world.Opener);
sim.FailAfterBody(DailyDropSteps.Download, times: 1);          // the in-doubt case; typed, so a renamed step breaks the test at compile time
await sim.RunAsync();                                          // the first attempt crashes after the body, before its result is stored
await sim.RunAsync();                                          // the retry finds the complete payload and adopts it (28b K14)

Assert.Equal(1, sim.Executions(DailyDropSteps.Download));      // the body ran once
Assert.Equal(StepStatus.Recovered, sim.StatusOf(DailyDropSteps.Download));
Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
Assert.Equal(1_240, sim.Counted(DailyDropCounters.Rows));
```

This is a test of the package (`RunSimulatorTests.The_plan_example_...`).

## Injecting failures

Every injector takes the step, unit or counter **value** the job declares, never a name string, and fires `times` times in all (across attempts), then stays quiet, so "fail once and let the retry run" is the default.

| Call | What happens |
| --- | --- |
| `FailAfterBody(step, times, key?)` | the process dies after the body returned and before its result is stored (in doubt on the retry; a streaming step's payload is already complete, so it is adopted) |
| `FailBeforeBody(step, times, key?)` | the process dies as the body is about to start (in doubt, the body never ran) |
| `FailMidStream(streamStep, times, key?)` | the process dies at the end of a streaming write, before the payload commits (an incomplete payload, nothing to adopt) |
| `CancelAt(step, ...)` / `CancelAt(unit, key?, ...)` | the run is cancelled there: `Stopped`, the step or unit stays started |
| `ConflictOnSave(n)` | the nth `SaveProgressAsync` of an attempt finds a checkpoint moved: nothing written, `Stopped` |
| `FailUnit(unit, key)` / `FailUnit(unit, item)` | that unit's body throws: it is recorded failed, the others go on, `CompletedWithFailures` |

## Reporting

`Outcome` and `Failure` (latest attempt), `Attempts` (each with its steps, units and counters), `Executions(step)` (bodies started, all attempts), `StatusOf(step)` (`Ran`, `Reused`, `Recovered`, `Failed`; null
when the attempt did not finish the step), `Counted(counter)` (all attempts) and `CountedIn(counter, attempt)`, `Progress<T>()` (the stored progress after the latest attempt; `Progress<T>(attempt)` after
an earlier one), `Units(unit)` (how many completed, failed, skipped or left started, and which keys), `Payloads(streamStep)` (the stored results, readable, with their hash) and `Log` (the run log of every attempt).
`StoredSteps()` and `Database` give the rows for anything else.

Notes: `maxParallelism: 1` makes a fan-out deterministic. A job that opens a slot needs `slots:` (what a runner would bind to a connection). `Progress<T>(attempt)` keeps the flat members as they were after that attempt; a
`Keyed<T>` member reads the store when first used. Dispose the simulator to remove its temporary database.
