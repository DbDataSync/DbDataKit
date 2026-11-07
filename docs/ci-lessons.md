# CI lessons

Every CI problem that has cost time more than once, how to recognise it, and the fix. Read this before you push; add a problem and its fix **in the same commit that fixes it**, so the next person does not find it again.
The CI runners are small (two cores, Windows and Linux), run many jobs at once, and the test engines are containers, so most of these are about time, files and order.

## Before you push (the short list)

1. Build **every project that references what you changed, including the test projects of other products** (Build's `DbDataBuild.Tests.Conformance`, Sync's `TaskRunner.Tests`, the console's server tests). A merge from `main` that moves or renames a type breaks them silently: a green build of your own project proves nothing about them.
2. Run the new and the affected tests on **SQLite, SQL Server and PostgreSQL** (`eval "$(testenv/testenv.sh env dbdatakit)"` in the same shell command as the test run; each command starts a fresh shell). A test that is "skipped" on an engine you did not set up has not run.
3. Think Windows: does the test delete a directory that holds read-only files (cache, artifact views)? does it read a file a running server still has open? does it use `/` or `:` in a path? See the Windows section.
4. If you touched `Directory.Packages.props`, a `.csproj`, a publish target or the Dockerfile: `dotnet publish` the console and start it (`dotnet dbdatakit.dll server run --listen https://localhost:<port>` must print "The console is on").
5. If you added a Playwright spec: the specs run **one at a time in the order of their file names**; see the Playwright section.
6. `python3 -I -m unittest discover -s scripts/tests` and the five `scripts/check_*.py` (agent worktrees under `.claude/worktrees` make two of those fail on a machine that has them; that is not a CI problem).
7. Delete your `bin`/`obj` (and any `node_modules`) when you finish: the disk of a session is small and a full disk fails builds in confusing ways.

## Recognise it, fix it

### Windows

| Signature | Cause | Fix |
| --- | --- | --- |
| `UnauthorizedAccessException: Access to the path '<hash>' is denied` in a test's `Dispose`/cleanup, `Directory.Delete(recursive)` | the artifact cache stores objects read-only; Windows will not delete a read-only file | delete with `ArtifactTestSupport.DeleteTree(path)` (DbDataKit.Runner.Tests), never `Directory.Delete` on a tree that held cache objects |
| `IOException: The process cannot access the file '...state.db' because it is being used by another process` when a test reads files under the data directory | a plain `File.ReadAllText` needs exclusive-enough access; the running server holds the SQLite file | open with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite \| FileShare.Delete)` |
| a memory-limit test sees no kill / a freshly started child is killed at once | job-object kills are silent; the memory watchdog sampled at once | the watchdog's first sample is after one interval (`MemoryWatchdog`); the limit test forces the watchdog |
| a read-only-directory test fails | Windows has no read-only directory semantics | skip it on Windows (`OperatingSystem.IsWindows()`), say why in the test |
| noise: "SQL Server '.\SQLEXPRESS' is not reachable" lines in the Windows job | tests that need a local SQL Server are skipped there | not a failure; look for `failed` lines |

### Timing (two-core runners)

| Signature | Cause | Fix |
| --- | --- | --- |
| a test needing "the next thing within N seconds" fails only in CI; log says "This runner was silent for 00:00:5x (the lease is 00:00:30)" | the whole process stalled (thread pool starvation, many jobs on the machine) longer than a 30 s lease | the DbDataRunner tests raise the thread pool minimum (`TestProcess.cs`) and run one at a time; the scheduler renews its own running items before it releases expired leases (`RenewOwnLeases`). A second record of one run (`Sequence contains more than one element` on a runs query) means this area again |
| `Two_runners_activating_at_once_migrate_once` fails with `NotReady` instead of `AlreadyDone` | the second runner read readiness after the first had activated (activation resets the instance states) | `PackageActivator.ActivateAsync` looks at the head again before answering `NotReady` |
| `Assert.False(activation.IsCompleted)` or `Waited 60 seconds for the hold on new runs` in `A_run_in_flight_finishes_on_the_old_version…` | the in-flight `lingers` task was a fixed 12 s of wall clock; when staging the new version is slow (a stalled runner) it ended before the activation started its hold, so the activation finished at once and the hold was never there to see | `lingers` takes the input `release_file` and runs until that file exists (five minutes at most); the test creates the file after it has seen the hold and the 1.5 s of waiting. Never wait for "a task that is long enough": give the task a signal the test controls. The task process has a cleared environment, so the channel is an input of the run request (`{"inputs": {"release_file": "..."}}`), not an environment variable |
| `IncompatibleServerException: 127.0.0.1:<port> did not report a management API version` in a publish/pair test (`PublishApiTests.A_publisher_uploads_only_a_changed_file…`), after a very slow test (2+ minutes) | Kestrel's own `408 Request Timeout` (`RequestHeadersTimeout` 15 s, `DbDataKit.Hosting`): a process that stalls longer than that gets the answer from Kestrel itself, which is written before any middleware runs, so `UseManagementApiVersion` cannot put the version on it. The server never started the request. Shown by `KestrelsOwnRequestTimeoutAnswerIsWrittenBeforeTheMiddlewareAndHasNoVersion`; that a stall of this length produces it in the full test is inferred (a reproduced stall made pairing fail with a closed connection instead, the same family) | `ManagementVersionHandler` sends a request once more when the answer is a 408 without the version header (the request was never started, so it is safe to send again); a second such answer, or a 408 that carries the version, is passed on as before. Do not make the version header optional |
| `Cancel` or "body started" tests flaky | the test cancelled before the body ran | cancel when the body signals it started (a `TaskCompletionSource`), never after a fixed delay |
| two tests pick the same free port | `FreePort` handed the same number twice | `FreePort` never repeats a port in a process |
| a deadlock victim (SQL Server 1205, PostgreSQL 40P01/40001) in a state store test | concurrent takers of one inbox | the dialects' `ShouldRetry` covers them; a new store operation that takes rows concurrently needs the same retry |

### Playwright (console)

| Signature | Cause | Fix |
| --- | --- | --- |
| `ENOENT ... FIRST-RUN.txt` in `first-run.spec.ts` or `identity-home.spec.ts` | a spec that ran earlier invited the first administrator (`user invite`), which uses the first-run invitation up | the specs run in the order of their file names with one worker; `first-run` and `identity-home` need a console with no user, so a spec that invites an administrator must sort **after** `identity-home` (`monitoring`, `inventory-connections`...). Say so in a comment at the top of the spec |
| a password is "found on the page" in `state-move.spec.ts` | a very short password (`e2e`) occurs inside a database name | `expectPasswordNotShown` skips passwords shorter than 8 characters; use a long password |
| `ENOENT ... FIRST-RUN.txt` in `identity-home.spec.ts` although its Proxy is "up" (the failure is immediate) | a fixture server added to `playwright.config.ts` took a port a spec starts its own server on (8497 and 8498 were the identity-home spec's home and console): the spec's health check was answered by the OTHER server, so it believed its own Proxy was running | before choosing a port, `grep -n "<port>" playwright.config.ts tests/*.ts`; ports 8481 to 8499 and 8511 to 8515 are in use, 8501 and 8502 are the inventory story's |
| a new fixture server never starts | every server in `playwright.config.ts` needs its own port and its own directory under the temp folder; the `webServer` entry needs `url`, `reuseExistingServer: false`, a timeout | copy an existing entry; ports in use are listed in the config |

### Console image / publish

| Signature | Cause | Fix |
| --- | --- | --- |
| the container is "unhealthy"; log: `Could not load file or assembly 'Microsoft.Data.Sqlite'` | publish items for another program (the task host) were added **before** the SDK's file-conflict resolution, which saw two files of one name and dropped the console's | add such items with `AfterTargets="ComputeResolvedFilesToPublishList"` (`TaskHostIntoPublish`); check by publishing and listing the output |

### Merging `main` into a branch

| Signature | Cause | Fix |
| --- | --- | --- |
| `error CS0117: 'RunnerPackages' does not contain a definition for 'Install'` in a project you did not touch | `main` moved or renamed the type (R20 moved the runner hosting into `DbDataKit.Runner` and `DbDataKit.Runner.Api`; the installer is `RunnerPackageInstaller`) | after every merge from `main`, build the test projects of every product that references the runner/state libraries (see "Before you push" item 1) |
| conflict in `docs/plans/*.md` slice rows | two slices edit neighbouring rows | keep every row's status text; never resolve by taking one side wholesale |

### Infrastructure (not your change; re-run once, then look closer)

| Signature | What it is |
| --- | --- |
| `testenv.sh up ...` ends with `container dbkit-test-mssql exited (1)` before any test | the SQL Server container failed to start on that runner; re-run the job |
| tests all pass (`failed: 0`) but the job fails with `rm: cannot remove '/tmp/tmp.XXXX': Directory not empty` | cleanup race of the e2e job; re-run |
| `429 Too Many Requests` pulling an image | Docker Hub rate limit; pull through `mirror.gcr.io` and re-tag (CLAUDE.md) |
| a job sits "queued" for a long time | many pull requests run at once; one batch pull request instead of eight is the cure |

## Working in a session (not CI, but it costs the same time)

- `dotnet test --project ...` can fail with `Handshake failures` here: run the built executable instead. Microsoft.Testing.Platform projects take `--filter-class "*Name"` and `--filter-method "*Name*"`; the xunit v3 in-process runner takes `-class` and `-method`.
- Never chain `sleep` in a command; wait with a `Monitor` until-loop.
- A job that reports "failed with 1 error(s)" has an error line named `failed` above it; search for the test name, not for "error".
- When one failure shows up in two pull requests, it is not those pull requests' failure: fix it once, in the batch, and add it here.
