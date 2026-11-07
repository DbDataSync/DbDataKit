# DbDataKit.Runner

The runner's engine (plan 28, slices R05 onward of plan 28a). So far: the **artifact cache**, which materializes a published version ("what runs is what was approved", decision DR13).

A version is a list of files (`ArtifactFile`: path, SHA-256, size, media type, payload id). An `IArtifactSource` lists them and opens one as a stream; `PayloadArtifactSource` is the implementation over `PayloadStore` (plan 29 section 3.6), and plan 30's `version_artifact` table can implement the same interface later.

```csharp
var cache = new ArtifactCache("/var/lib/dbdatarunner/cache", source);
using var view = await cache.MaterializeAsync("orders-sync@7", files, ct);   // or without files: the source lists them
// view.Directory is this run's read-only copy; view.PathOf("bin/Orders.dll") a file in it
await cache.EvictAsync(hashesOfEveryServedVersion, ct);                        // removes the rest, never what a live view holds
```

- **Objects by hash.** `<dir>/objects/<first two hex>/<sha256>`. A file is streamed to `<dir>/tmp/<guid>.part` while it is hashed (one 128 KiB buffer, whatever the size), checked against the descriptor, made read-only and renamed into place. A wrong hash is refused with the path and both hashes and leaves neither an object nor a partial file. Two materializations of one hash, in one process or two, leave one object.
- **A view per run.** `<dir>/views/<version>-<id>/`, laid out by the artifact paths. On Linux and macOS its files are symbolic links to the objects (copies with `ArtifactLinkMode.Copy`, and wherever linking is not allowed), the objects are mode 0444 and the view's directories 0555, so a task running as the same user can neither write a file nor add, delete or replace one. On Windows files get the read-only attribute (directories too, which stops less there). Disposing the view removes it.
- **Paths are checked before anything is fetched.** `..`, `.`, empty segments, absolute paths, drive letters, backslashes, reserved device names (`CON`, `NUL`, `LPT1`...), characters Windows refuses, a duplicate (also one that differs only in case), and a file that is also a directory are refused with a message naming the path.
- **Eviction** takes the set of hashes still needed. A hash held by a live view of this process stays (the result counts them); a second process using the same directory is not protected.

## The scheduler loop and retries (R08, R09)

`RunnerScheduler` (namespace `DbDataKit.Runner.Scheduler`) reads the `task_projection` rows of the channels it serves, queues what is due on the shared work queue, claims items with a lease and runs each through an `ITaskRunner` (`TaskExecutor`) from a version materialized by the artifact cache. `RunAsync(ct)` is the loop; `TickAsync` is one pass (tests drive it with the state database's clock); `RunNow`, `Pause`, `Resume` and `Status()` are the by-hand surface.

Settings (`RunnerSchedulerOptions`), with their defaults: `Tick` 5 s, `Heartbeat` 30 s, `Lease` 90 s (plan 28a QR3; the lease has to be longer than the heartbeat), `MaxConcurrentRuns` 4, `CancellationGrace` 30 s, `RetryDefaults` 30 s back-off up to 10 min with 20 % jitter, `StartFailureAttempts` 3. A package's `concurrency` is a pooled resource on the queue and applies across loops.

Retries (`RetryPolicy`, `RetryPlanner`, `WorkItemRetries`): a retryable failure puts the same work item back with a new run id, a not-before time and the next attempt number, on the same chain and, once an attempt had started, the same version. A projection row's `schedule` and `limits` are the small JSON documents `ProjectionDocuments` reads and writes. `IVersionSource` is where versions come from: `DefinitionVersionSource` (kind `integration`) or `InMemoryVersionSource`.

## Staging and activation (R17, R18)

`StagingFollower` (namespace `DbDataKit.Runner.Staging`, one per instance) prepares every STAGED version of an `integration` and reports `preparing`, then `ready` or `failed`, in `InstanceStatusStore`: the files through the artifact cache (every hash verified), the contracts the package was built against (`StagingOptions.Contracts`), the manifest again (R01), `RoutingCheck.CanActivate` for each task's required labels, and the **dry run** of the progress migration (`ProgressMigration.DryRun`) against the progress stored now. The plan and its losses are the status's `note` (`has_losses` marks it); a conversion that fails is the status's `error`. `PackageActivator` then activates:

- `ActivationMode.Automatic` (what the follower asks for): only when every live instance is `ready` and no dry run lists losses; atomic and once (`DefinitionPublisher.ActivateIfAllReady`), so the runner that sees it first does it and the others find it done.
- `Administrator`: the same conditions; losses need `ConfirmLosses`. `Anyway`: a recorded choice that goes ahead although instances failed or have not reported; the event names who and what stood in the way.
- The activation **holds** new runs of the package (`ActivationHolds`, a leased row every runner reads before it claims, and again after it has claimed), waits per package until no task has a claimed or running item (a run finishes on the version it started with), at most `ActivationOptions.MaxWait` (10 minutes; `DBDATARUNNER_ACTIVATION_MAX_WAIT_SECONDS` on the server). After that it applies nothing, releases the hold, names the busy tasks and is tried again at the next look.
- Then, in ONE transaction (`DefinitionChange` handed to `DefinitionPublisher`): the pointer moves (an update that finds it moved changes nothing, which is how two activations at once migrate once), each task's progress is migrated (`ProgressStore.Within`, history kept in `checkpoint_history` and, for state values, `state_history`, under the actor `migration:<package>@<from>-><to>#<id>`) and the task projection is rebuilt.
- `RollbackAsync` restores what that migration changed from the history, in one transaction with the pointer and the projection; `SwitchAsync` migrates by the plan, like an activation.
