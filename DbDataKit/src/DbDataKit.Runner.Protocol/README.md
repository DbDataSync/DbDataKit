# DbDataKit.Runner.Protocol

How a task's process talks to the runner, the same for every kind of package. The .NET kit (`DbDataKit.Runner.Kit`) wraps it, so .NET code never sees it; a task in another language speaks it through its own thin client and writes its `package/1` manifest by hand. The server side is `DbDataKit.Runner.Protocol.Server` (`RunnerProtocolService`, `RunnerProtocolHost`).

## Transport and identity

* **gRPC over a local socket, never TCP:** a Unix domain socket on Linux, a Kestrel named pipe on Windows. Standard input and output would compete with the task's own output and cannot carry payload streams. `RunnerEndpoint` is `unix:/path` or `npipe:name`.
* **One server per runner, not per task** (one Kestrel listener). The socket lives in a directory only the runner's account can use (mode 0700), the socket itself 0600.
* **A per-run token** authenticates a task. `RunnerProtocolHost.Register(RunGrant)` returns a random token; the runner gives the task the endpoint and the token in its environment, never in argv (`DBDATAKIT_RUNNER_ENDPOINT`, `DBDATAKIT_RUNNER_TOKEN`, names in `RunnerProtocolNames`), and an interceptor binds every call of every kind to the token's run, so a task can touch only its own run's progress, steps, logs and inbox. A run that ended, or a token that was never issued, is refused.
* **The task never holds a database connection to the runner's state** (state credentials stay with the runner): progress is read once at start and written with the run's outcome through `Advance`.

## Version

The package is `dbdatakit.runner.v1`, a `.proto` in this project, versioned in its package name. The client sends its version in `Start` and the runner refuses a task it cannot serve, naming both. An incompatible change is a `v2` served beside `v1` until no published package uses `v1`; a package records the protocol it was built against (`built_against.protocol`) and staging refuses one the runner does not serve with a message that asks for a republish.

## The calls

| Group | Calls |
| --- | --- |
| Run | `Start` (inputs as strings, connection handles, run id, attempt, chain, the whole progress snapshot, cancellation grace), `End` (outcome, counts, first failed item, outputs), `Heartbeat`, `WatchCancel` (a server stream; the runner's cancel with the grace period) |
| Progress | `Advance` (the changed values with the expected version, saved with the outcome in one transaction) |
| Steps | `BeginStep(name, key)` answers `reuse(result)`, `run` or `in_doubt`; `RetakeStep`, `AdoptStepPayload`, `CompleteStep`, `FailStep`; `StepChunk` (a client stream into the step's payload), `StatPayload`, `ReadPayload` (a server stream) |
| Units | `BeginUnit(name, key)` answers `run` or `skip`; `UnitProgress`, `EndUnit(outcome)` |
| Files, inbox, dead letters | `ClaimFile`, `CompleteFile`; `TakeInbox`, `CompleteInbox`, `ReleaseInbox`; `Reject` |
| Reporting | `Report` (status, counts, logs and lineage, one-way and batched), `ResolveConnection` (a connection handle resolved by the runner, secrets never written to disk) |
| Work | `EnqueueWork`, `ClaimWork`, `StartWork`, `FinishWork`, `ReleaseWork`, `IsCancelRequested`, `WorkState`: for an engine running as a task that takes work items itself (Sync's drain, Build's refresh; plan 28c) |

The messages are the kit's calls with names and keys as strings on the wire; the typed definitions are the .NET kit's convenience. What each call means (steps, units, recovery) is in the `DbDataKit.Runner.Kit` README, and the sample jobs run unchanged through a real socket (`GrpcRunBackend` against the same stores as the in-process backend).

## What the end message carries

`TaskEnd`: state (`Succeeded`, `CompletedWithFailures` for partly succeeded, `Failed`, `Stopped`), counts, the first failed item, a classified failure and the outputs. A process that exits without one is classified by the runner (`DbDataKit.Runner.Abstractions`, `TaskEndMapping`).
