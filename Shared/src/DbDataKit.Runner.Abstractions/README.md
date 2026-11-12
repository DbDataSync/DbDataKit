# DbDataKit.Runner.Abstractions

What the runner and the people who publish to it share (the runner's package model). It holds the **`package/1` manifest** and the **task and run model** (below).

A package is described by `package.yaml`. The first key is the format id, `schema: package/1`; a file in another format, or another version of this one, is refused with a message naming the file.

```yaml
schema: package/1
name: orders-sync
launch: { kind: assembly, assembly: bin/Orders.dll }        # or { kind: executable, command: bin/orders, args: [--quiet] }
built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
tasks:
  - name: extract
    entry: { type: Orders.Tasks.Extract, method: Run }      # assembly packages; method defaults to Run
    schedule: { cron: "0 2 * * *", time_zone: Europe/Berlin, misfire: skip, misfire_grace: 5m }   # or every: 15m
    triggers:
      - after: { task: other, on: [succeeded, failed] }    # outcomes: succeeded, completed_with_failures, failed, stopped, interrupted
      - webhook: { source: shop }
      - file: { connection: inbox, path: "in/*.csv", poll: 1m }
      - store_change: { model: orders, entity: order }
      - manual: true
    inputs:      [ { name: since, type: date, default: "2024-01-01" } ]
    connections: [ { name: inbox, kind: folder }, { name: erp, kind: sqlserver, access: read } ]   # every slot a trigger uses is declared here; access: read|write (default write)
    progress:                                              # see the DbDataKit.State README, progress across versions
      checkpoints: [ { name: orders, per: [customer], parts: [ { name: modified, type: time }, { name: id, type: int } ] } ]
      state:       [ { name: last_file, type: text } ]
    limits: { timeout: 30m, memory_mb: 512, cpu: 1.5, concurrency: 2, attempts: 3, backoff: 30s, max_backoff: 10m }
    requires: [network:outside]
    capabilities: [network]
    steps:    [ { name: download, kind: stream, input: DateOnly }, { name: archive, input: DateOnly, result: ArchiveReceipt } ]   # for the console; kind is value (default) or stream
    units:    [ { name: customer, item: Customer } ]
    counters: [rows]
```

**Connection access** (plan 21 CH03, an optional key of a connection slot, added to `package/1` in place by the owner's decision of 2026-11-12). `access: read` says the task only reads the connection: in a release channel other than `prod` that binds nothing for the slot, the runner serves production's binding (the resolution is `DefinitionPublisher.ResolveSlots`; publish, stage, activate and switch all use it). A slot that says nothing, or `write`, must be bound in every channel and is refused naming slot and channel otherwise. A name two tasks declare is read only when every task says read. Any other value is an error naming the file and `connections[i].access`. `ConnectionSlot.Access` carries it; the package's `slots` in `GET /api/v1/definitions/integration/{name}` say `access` and the Runner's bindings card shows a read slot and the binding it inherits.

**Runtime packages** (plan 28c E04, a newer feature of `package/1`). An engine is published once as a *runtime package* and many *definition packages* name it:

```yaml
# the runtime package: no tasks of its own, an executable launch, a version
schema: package/1
name: dbdatabuild-runtime
runtime: true
version: 2.1.0
platform: linux-x64          # win-x64 or linux-x64: one package per platform, stored as dbdatabuild-runtime-linux-x64 (PackageManifest.PackageName)
launch: { kind: executable, command: bin/dbdatabuild }

# a definition package: `launch.runtime` instead of an assembly or command, and the versions of the runtime it accepts
schema: package/1
name: orders-project
launch: { runtime: { name: dbdatabuild-runtime, range: "2.x" } }       # 2.x, 2.1.x, 2.1.4, ">=2.1 <3"
tasks:
  - name: refresh
```

The runner starts the runtime's executable with the definition's directory as the working directory and `DBDATAKIT_PACKAGE_DIR` pointing at it (`DBDATAKIT_RUNTIME_DIR` is the runtime's own directory). A runner that does not know `launch.runtime` or `runtime` refuses the manifest naming the key. `VersionRange` is the range grammar.

* `PackageManifestReader.Load(text, file)` reads strictly: a key that is not part of the format is an error with the nearest key that is (`Did you mean limits?`), and every problem is a `ConfigDiagnostic` with the file, line, column and key path (`tasks[0].limits.timeout`). `Read` throws a `PackageManifestException` with one line per problem; `LoadFile` reads from disk.
* `PackageManifestValidator.Problems(manifest)` says what is wrong with a manifest that is well formed (it works on the model, so one built in code is checked the same way): names, the schedule (through `Schedule.Validate()` of `DbDataKit.Scheduling`), a connection slot a trigger uses and the task does not declare, a value declared twice in the progress, a limit of zero, a task an after-run trigger follows that does not exist, a trigger loop (`TriggerGraph.FindCycle`), defaults that do not parse in their type.
* `PackageManifestWriter.Write(manifest)` writes the text back; reading it gives an equal manifest.
* A task's `progress` is read straight into `ProgressDeclaration` of `DbDataKit.State`, the model `ProgressMigration.Plan` takes. Nothing is duplicated.
* `steps`, `units` and `counters` list what the task declares so the console can show them before a run; none is required. For a .NET job they are generated from the C# declarations by `DbDataKit.Runner.Kit`'s `ManifestGenerator`.
* Durations are a number and a unit (`500ms`, `30s`, `5m`, `2h`, `1d`).

## The task and run model

Plain records and pure mappings, no I/O; the protocol (`DbDataKit.Runner.Protocol`) and the executor (`DbDataKit.Runner`) share them.

* `TaskRef(package, channel, task)`: names follow the manifest's name rule; string form `orders-sync/stable/extract`, `TaskRef.Parse` / `TryParse` read it back.
* `TaskStart`: run id, attempt (first is 1), package version, inputs, connection handles (names only, never a secret), progress snapshot reference, cancellation grace, protocol version (`dbdatakit.runner.v1`).
* `TaskFailure(kind, message, item)` with `TaskFailureKind` (Temporary, Permanent, Cancelled, TimedOut, ProcessExited, ProtocolViolation, LimitExceeded). `Retryable` is Temporary, TimedOut and ProcessExited. `TaskFailure.From(ConnectionFailure)` maps Network and Timeout to Temporary, every other connection kind to Permanent.
* `TaskEnd(state, processed, failedItems, firstFailedItem, failure, outputs)` with `TaskEndState` (Succeeded, CompletedWithFailures, Failed, Stopped). Each state has one `RunOutcome` (partly succeeded is `CompletedWithFailures`, QR4) and one final `WorkItemStatus` (Done, Done, Failed, Cancelled).
* `TaskEndMapping`: `OutcomeOf`, `WorkItemStatusOf`, `ToRunFinish` (the arguments of `RunStore.Finish`), `ShouldRetry(end, attempt, maxAttempts)` (DR8), and the ends the runner makes when a process gave none: `ProcessExited`, `TimedOut`, `Cancelled`, `LimitExceeded`, `ProtocolViolation`.
