# DbDataKit.Runner.Kit.Testing

Test a runner job without a runner (the kit's design is in the `DbDataKit.Runner.Kit` README). `RunSimulator<TJob>` runs a job in memory over the **real stores** (progress, steps, units, payloads, runs) on a temporary SQLite database, so the
semantics a test sees are the kit's own: steps answer from memory, finished units are skipped, a checkpoint moves only with the run's outcome, and an in-doubt step is recovered the way the job said.

The simulator keeps one **attempt chain**: every `RunAsync()` is the next attempt, so a retry finds what the earlier attempts left. A crash is an attempt's outcome, not an exception: its run is recorded
`Interrupted` and its started steps are in doubt, as the runner would leave them.

## Usage: the daily drop

A file that disappears from the server when it is downloaded, a crash after the copy, and the retry:

```csharp
var sim = new RunSimulator<DailyDrop>(input: new DailyDropInput(new DateOnly(2026, 11, 7)), slots: world.Opener);
sim.FailAfterBody(DailyDropSteps.Download, times: 1);          // the in-doubt case; typed, so a renamed step breaks the test at compile time
await sim.RunAsync();                                          // the first attempt crashes after the body, before its result is stored
await sim.RunAsync();                                          // the retry finds the complete payload and adopts it (see the Kit README, "A streaming step in doubt adopts a complete payload first")

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

## The outside world in memory (plan 27a IX15)

A job for an integration talks to an API or a file share. Test it with neither.

**`RecordedHttp`** replays a recording (`HttpRecording`, format `http-recording/1`, a JSON file) as an `HttpMessageHandler`:

```csharp
var recording = new HttpRecording()
    .Add("GET", "https://api.example.test/v1/orders", json: """{"items":[{"id":1}],"next_cursor":"b"}""")
    .Add("GET", "https://api.example.test/v1/orders?cursor=b", json: """{"items":[{"id":2}],"next_cursor":null}""");   // or HttpRecording.Load("orders.json")
var recorded = new RecordedHttp(recording);
using var http = new OutsideHttp(recorded.Client("https://api.example.test/v1/"));      // the job's client, no network
// ... run the job under RunSimulator ...
recorded.Verify();                                   // every request had an answer, every entry was asked for
```

* A request is matched on method, address (scheme, host, path and the query as a set, so the order of parameters does not matter) and, if the entry says so, body (`body` as text, `json` compared as JSON). Each entry answers once, in file order, so `[503, 200]` for one address tests the client's retry; `"repeat": true` answers every match.
* **A request with no entry is not sent anywhere.** The handler throws `RecordingException` naming it (`No recording for GET https://api.example.test/v1/orders?cursor=b`) and keeps it in `Misses`; `Verify()` fails the test with every such request and every entry nobody asked for, so a job that swallows the failure still fails the test. The value of a parameter that names a credential (`api_key`, `token`, ...) is shown as `***`.
* **A recording holds no secret.** A response header that is a credential or a cookie, a user name in an address and a credential-named query parameter make the file unreadable, naming the file and the entry (never the value). There is no capture from a live API: write the file from the API's documentation, or from an answer you have read and cleaned.
* The file: `{ "schema": "http-recording/1", "entries": [ { "request": { "method", "url", "body"?, "json"? }, "response": { "status", "headers"?, "body"? | "json"? | "body_base64"? }, "repeat"? } ] }`. The format is registered in `docs/formats.json` (open) and has no editor screen (`docs/ui-editors.md`: a fixture edited as text).

**`FakeFileShare`** is a folder an outside system fills, in memory: `Put` a finished file (with its modification time), `Begin` a file a writer has not finished (it is listed with `Complete` false and cannot be read, and `Finish` makes it whole), `ListAsync`, `ReadAsync`, `WriteAsync` (whole in one step), `MoveAsync`, `DeleteAsync`; `FailNext("read", new IOException(...), times, path)` makes the next calls fail (an `IOException` is temporary for the runner's retry, a missing file is a `FileNotFoundException`, permanent); `Log` and `Calls("read")` say what the job did; time is a `TimeProvider`. A listing gives the SHA-256 the job hands to `Files.ClaimAsync`. It is the common small shape of SFTP, FTPS, SMB and object storage, not their interface: those clients (plan 27a IX04 to IX06) are not built, and when they are, a job takes its share as the slot's handle and this class stands in for it in the test.

Tests (`OutsideWorldTests`): a cursor-paged job reads its pages from a recording, a missing recording fails naming the request even when the job fails first, an unused entry fails the check, a secret in the query is not printed, a 503 then a 200 tests the retry, body and repeat matching, a recording file round-trips and a credential in one is refused naming the entry, a file being written is not picked up and a file is taken once whatever it is called, a failing read is temporary and the retry goes through.
