# DbDataKit.Runner.Abstractions

What the runner and the people who publish to it share (plan 28, slice R01 of plan 28a). So far: the **`package/1` manifest**.

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
    connections: [ { name: inbox, kind: folder } ]         # every slot a trigger uses is declared here
    progress:                                              # plan 29 section 4.3
      checkpoints: [ { name: orders, per: [customer], parts: [ { name: modified, type: time }, { name: id, type: int } ] } ]
      state:       [ { name: last_file, type: text } ]
    limits: { timeout: 30m, memory_mb: 512, cpu: 1.5, concurrency: 2, attempts: 3, backoff: 30s, max_backoff: 10m }
    requires: [network:outside]
    capabilities: [network]
```

* `PackageManifestReader.Load(text, file)` reads strictly: a key that is not part of the format is an error with the nearest key that is (`Did you mean limits?`), and every problem is a `ConfigDiagnostic` with the file, line, column and key path (`tasks[0].limits.timeout`). `Read` throws a `PackageManifestException` with one line per problem; `LoadFile` reads from disk.
* `PackageManifestValidator.Problems(manifest)` says what is wrong with a manifest that is well formed (it works on the model, so one built in code is checked the same way): names, the schedule (through `Schedule.Validate()` of `DbDataKit.Scheduling`), a connection slot a trigger uses and the task does not declare, a value declared twice in the progress, a limit of zero, a task an after-run trigger follows that does not exist, a trigger loop (`TriggerGraph.FindCycle`), defaults that do not parse in their type.
* `PackageManifestWriter.Write(manifest)` writes the text back; reading it gives an equal manifest.
* A task's `progress` is read straight into `ProgressDeclaration` of `DbDataKit.State`, the model `ProgressMigration.Plan` takes. Nothing is duplicated.
* Durations are a number and a unit (`500ms`, `30s`, `5m`, `2h`, `1d`).
