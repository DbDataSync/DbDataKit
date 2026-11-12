# Integrations (DbDataIntegration)

An **integration** is ordinary code that moves data between the suite and a system that is not one of its databases (a web API, a file share, object storage), published to the Runner as a package and run by it on a schedule, by a trigger, or by hand. This page is the one place that says what an integration is, how one is written, built, tested and published, and **which parts exist today and which do not**. Each part is marked **built** or **not built**; the unbuilt parts name the slice of [plan 27a](plans/27a-knowledge-slices.md) (lane IX) that owns them. The decisions behind it are in [plan 27](plans/27-knowledge-agents-integrations-work.md) section 4 (owner, 2026-11-04) and [plan 28](plans/28-runner.md); the run-time side is documented in [`DbDataRunner/README.md`](../DbDataRunner/README.md) and [`Shared/src/DbDataKit.Runner.Kit/README.md`](../Shared/src/DbDataKit.Runner.Kit/README.md).

**The name.** DbDataIntegration is not a service or a program of its own. It is the *product shape*: authoring in the Workspace server (the `integration` project kind), running on the Runner, with the console as the place people sign in and see runs. The words that are fixed: **connection**, **integration** (a package), **task**, **action**, **script**. Notebooks (a .NET notebook run headless) stay **parked** (plan 28 open question 1): nothing here builds them.

## What is built and what is not

| Part | State |
| --- | --- |
| Run-time: the Runner, its task host, schedules, triggers (schedule, after another run, webhook into the inbox, local-folder file arrival, by hand), limits, retries, progress store, run history, lineage of packages and runs | **built** (plan 28a R01 to R20; [`DbDataRunner/README.md`](../DbDataRunner/README.md)) |
| The kit a .NET job writes against (typed progress, steps and units, connection slots, logs with secrets redacted) and `RunSimulator` to test it | **built** (`DbDataKit.Runner.Kit`, `DbDataKit.Runner.Kit.Testing`) |
| The package manifest `package/1` and publishing it to a Runner (`package publish`, the Workspace's publish with a Runner target) | **built** (`DbDataKit.Runner.Abstractions`, `DbDataKit.Runner.Client`) |
| Outside systems as connections: kinds `http` and `model-provider` in `connection/2`, sign-in by API key, basic, OAuth 2 client credentials and client certificate, failure classification | **built** (IX01, IX02; `DbDataKit.Connections.Http`) |
| **csx integrations built with no .NET SDK**: `package build` and `package test` (this page, section 6) | **built** (IX20, `DbDataKit.Runner.Packaging`) |
| The `integration` project kind in the Workspace server: template, check, build in a child process (tests run first), publish to a Runner | **built** (IX20, `IntegrationProjectKind`; 9 tests in `WorkspaceIntegrationKindTests`, one against the real `dbdatarunner`); compiled .NET projects build only where the SDK is installed (tested with a stand-in `dotnet`, not a real project build) |
| The integration editor in the Workspace's web screens (manifest as form and text, code files, run tests, publish) | **built, minimal** (IX24, `IntegrationPanel.tsx`: manifest as form and YAML, tasks, code and tests listed with their errors, new task, run the tests as a build, publish on the Builds tab; vitest 12 tests; the Playwright story `integration-editor.spec.ts` is written and not run) |
| Project templates "pull pages from a REST API" and "pick up CSV files from a share" | **not built** (IX21): skipped, see section 9; the kind's own starter template (one task, one test) is built |
| Kit services for outside systems: file formats (CSV, TSV, JSON, JSON Lines, XML, fixed width, Excel, Parquet, gzip, zip, OpenPGP) and the HTTP client with paging, rate limits and retries | **built** (IX08 to IX10; `DbDataKit.Runner.Kit.Formats`, `DbDataKit.Runner.Kit.Http`) |
| Kit services for outside systems: SFTP, FTPS, SMB, object storage | **not built** (IX04 to IX06) |
| Landing helpers: rows into a staging table through `DbDataKit.Transfer` and into DbDataStore's tables through the Store's write path, idempotent | **built** (IX11; `DbDataKit.Runner.Kit.Landing`; the Store route is in process, not through the Store's HTTP API; files into DbDataStore documents wait for plan 15's documents) |
| Change detection with no change feed (a row-hash snapshot kept as a checkpoint, compared with the next run) | **built** (IX13; `DbDataKit.Runner.Kit.Changes`) |
| Refused-record listing and replay (the kit's surface over the dead letters; the runner's API and screens are plan 28a R25) | **built** in the kit (IX14; `RefusedRecords`) |
| Lineage nodes for outside systems | **not built** (IX12) |
| Recorded HTTP responses and a fake file share for tests | **built** (IX15; `RecordedHttp` and `FakeFileShare` in `DbDataKit.Runner.Kit.Testing`; the recording format `http-recording/1` is in `docs/formats.json`) |
| Dry run (`--dry-run`, the kit refuses writes) and the sandbox run before promotion | **not built** (IX16) |
| Outbound steps in a non-production channel use the channel's connection or are refused | **not built** (IX17) |
| OAuth 2 authorization code, the serving layer's end of webhooks, the DbDataStore change trigger | **not built** (IX03, IX18, IX19) |
| Notebooks | **parked**; no row is built |
| Agents in integrations and agents that write them | **not built** (AG27; plan 27 section 4.9) |
| Approval of a trusted integration version by an administrator before it serves | **not built** (plan 27 section 4.7; the Runner stages a version and an administrator activates it, which is the approval point today) |

## 1. What an integration is

A package of the kind `integration` (every package the Runner takes is published as that kind): a **manifest** (`package.yaml`, format `package/1`) listing **tasks**, each with its schedule, triggers, inputs, connection slots, progress declaration and limits, and the **code** the tasks run. The Runner runs each task in a process of its own, with the limits the manifest sets, under the service account of the identity home. Nothing in it is a generic engine: Sync replicates database to database and Build models inside a database; an integration covers what is outside them (plan 27 section 4.8).

Directions it covers: **inbound pull** (read an API or share on a schedule, incrementally), **inbound push** (a webhook calls in), **outbound push** and **outbound file** (send suite data out), **requested** (an action enqueues a task), **chained** (a task starts when another run ends).

## 2. Package kinds and languages

The Runner starts two things (`Shared/src/DbDataKit.Runner/README.md`): a .NET **assembly** through the task host, or an **executable** that speaks the runner protocol. What an author writes maps onto them:

| Kind | What the author writes | What is published | Needs to build | State |
| --- | --- | --- | --- | --- |
| **csx** | C# files in `src/*.csx` with `#r "nuget: Id, 1.2.3"` lines: the job classes | one assembly in `bin/`, the packages' run-time assemblies beside it, `package.yaml` | the .NET **runtime** only (Roslyn and the NuGet client run inside `dbdatarunner`) | **built** (IX20) |
| **Compiled .NET project** | a `.csproj` (root or `src/`) of job classes, NuGet dependencies by the usual means | the output of `dotnet publish`, `package.yaml` | the **.NET SDK** | **built** for the build and publish route; the Workspace says so when the SDK is missing |
| **Any executable** | a program in any language that speaks the runner protocol (`DbDataKit.Runner.Protocol`, gRPC over the runner's socket) with a hand-written manifest; `launch: { kind: executable, command: ..., args: [...] }` | the files as they are | whatever the author's toolchain needs | **built** at the Runner (it starts executables); **no** Workspace build step: it is published as files, not authored here |
| **Notebook** | a .NET notebook over a project's APIs, run headless | | | **parked** |

The csx and the compiled kind end the same way: the Runner never compiles (plan 28 section 4), it receives built assemblies. A `.csx` file here is **ordinary C# source** (types, namespaces, `using`s; no top-level statements) with NuGet directives at the top; all files of `src/` compile together into one library. It is not Roslyn's interactive "script" with `Submission#0` types: a job class is a normal public class the task host finds by `[RunnerTask("name")]`, and a test can name it.

## 3. The manifest

`package.yaml` is the `package/1` manifest and is hand-written; for a .NET job the C# types (the input record, the `Progress` and `Slots` classes, the steps, units and counters) are the source of truth and the Runner's staging check compares them with the manifest. The format and every key are in [`DbDataKit.Runner.Abstractions`'s README](../Shared/src/DbDataKit.Runner.Abstractions/README.md); the shape of one:

```yaml
schema: package/1
name: orders-pull
launch: { kind: assembly, assembly: bin/orders-pull.dll }     # a csx project: bin/<name>.dll, the file the build writes
built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
tasks:
  - name: pull
    entry: { type: Orders.Pull }
    schedule: { cron: "0 2 * * *", time_zone: Europe/Berlin }
    triggers: [ { webhook: { source: shop } }, { manual: true } ]    # also: after another task, file arrival, store change (not built)
    inputs:      [ { name: since, type: date, default: "2024-01-01" } ]
    connections: [ { name: shop_api, kind: http }, { name: staging, kind: database } ]   # the slots; bound per channel on the Runner
    limits: { timeout: 30m, memory_mb: 512, concurrency: 1, attempts: 3, backoff: 30s }
```

**Slots** are the connections a task names. The code asks `Slots.ShopApi.OpenAsync()`; the Runner binds the slot to a connection of the channel the version runs in, so the job never holds a secret and a preview never reaches production by accident. A secret is always a reference named `dbdatakit:<service>:<kind>:<name>[:<part>]`, never a value in a file.

## 4. The kit

What the job writes against is `DbDataKit.Runner.Kit` (**built**): one base class `RunnerJob<TInput, TProgress, TSlots>`, steps that the Runner remembers across retries (with a stated recovery), units for fan-out, typed checkpoints saved with the run's outcome, counters, the file ledger and inbox, dead letters, logs with secrets redacted. Test it with `RunSimulator<TJob>` (**built**), which runs a job over the real stores on SQLite and injects crashes, cancellations and failed units.

What plan 27 section 4.3 adds for outside systems (clients and formats, landing, change detection, lineage, dry runs) is **not built** and is the work of IX08 to IX16. Until then an integration brings its own packages for HTTP and file formats through `#r "nuget:"` (any package the allow-list and the lock file admit).

## 5. Connections to outside systems and channel bindings

An outside system is a **connection** like a database (plan 27 section 4.6): `connection/2` with `kind: http` (endpoint, `sign_in` with secret *references*, TLS options) or `model-provider`. **Built** (IX01, IX02): the format and its refusals, the HTTP client that signs requests in (API key, basic, OAuth 2 client credentials with a cached and once-refreshed token, client certificate) and the shared failure classification (`ConnectionFailure`: temporary or permanent, so retries know the difference). **Not built:** OAuth 2 authorization code (IX03), SFTP, FTPS, SMB, object storage (IX04 to IX06), the console form per kind with a Test button and the per-channel binding screen (IX23). The bindings agent's page `docs/channel-bindings.md` is not in this repository yet; until it is, see [plan 27a IX23](plans/27a-knowledge-slices.md) and the Runner bindings screen in [`docs/ui-editors.md`](ui-editors.md).

A task's slot is bound **per channel**; the sandbox rule (plan 27 section 4.7; IX17, **not built**) is: an outbound step in a non-production channel uses the connection bound for that channel (a sandbox endpoint) or is refused when there is none, and an administrator may bind a production endpoint to a non-production channel as an explicit, recorded, confirmed choice.

## 6. Authoring, build, test and publish

### The project

An `integration` project in the Workspace server is a git repository of this layout (the same for the command line):

```
package.yaml        the package/1 manifest (hand-written)
src/*.csx           the C# files of the job classes (csx kind); or src/<name>.csproj (compiled kind)
tests/*.csx         test scripts: each passes when it finishes and fails when it throws
files/              anything else the package carries (copied as it is into the package's files)
package.lock        the pins of the NuGet packages (scripting-lock/1; written by the first build, commit it)
README.md
```

No new file format is introduced: the manifest is `package/1` (registered, frozen), the lock is `scripting-lock/1` (`DbDataKit.Scripting`), the tests are scripts.

### Commands (built)

`dbdatarunner package build <project> --out <folder> [--source <feed>]... [--cache <dir>] [--offline] [--locked] [--no-lock-write]` reads the manifest with the Runner's own strict reader, restores the packages, compiles, checks that the manifest's tasks and the `[RunnerTask]` job classes are the same set, and writes `<folder>/package.yaml` and `<folder>/files/` (`bin/<name>.dll`, the packages' assemblies beside it, the project's `files/`), which is what `package publish` takes. `dbdatarunner package test <project>` builds into a scratch folder and runs each script of `tests/` against the built assembly, the kit and `RunSimulator`, in a load context of its own; `True`, `Equal`, `Fail` and `Project` (the project's folder, for fixtures) are in scope. Both print findings with file and line (`--format json` for the Workspace); both refuse a compiled project on a machine with no SDK with the message `DotnetSdk.Missing` says.

```csharp
// tests/pull.csx
var sim = new RunSimulator<Orders.Pull>(input: new PullInput(new DateOnly(2026, 1, 1)));
await sim.RunAsync();
True(sim.Outcome == DbDataKit.Monitoring.RunOutcome.Succeeded, "the run did not succeed");
Equal(120L, sim.Counted(Orders.PullCounters.Rows));
sim.Dispose();
```

A test script is a *script* (so `using var` at top level is not allowed: dispose by hand), runs in the tool's process (trusted code, not a sandbox) and has 60 seconds.

### Through the Workspace (built, IX20)

The `integration` kind of the Workspace server (plan 33's project-kind seam, `IProjectKind`; `DbDataKit.Authoring.Server`) follows the pattern of the Build and Sync kinds:

| Step | What happens | Where |
| --- | --- | --- |
| New project | template: a manifest, one job class, one test | the kind's `Template` |
| Check | the offline build into a scratch folder, nothing kept; the findings are the check's | child process `dbdatarunner package build` (csx), or a file check plus the SDK test (compiled) |
| Build | the kind's artifact: `package.yaml` and `files/` in `builds/<project>/<id>/artifact/` | the same verb with `--out`; a compiled project: `dotnet publish` in a child process |
| Test | the scripts of `tests/` | child process `dbdatarunner package test` |
| Version | the Workspace's own: a commit in the project's git workspace is the version; the Runner numbers what it receives | |
| Publish | `POST .../builds/{build}/publish` with a Runner target and channel: the package is staged on the channel, files the Runner has are not sent; `bind` fills the channel's slots | the existing publish path (SC20d is the model), `RunnerPublishClient` |

The child process runs with a cleared environment, the draft as its folder and a time limit, as the other kinds do. **Not built:** the administrator's approval of the exact trusted version (today: the Runner stages, an administrator activates), and prompting per step for an agent that writes the integration (plan 27 section 3.3, AG lane).

### What the Runner does with it (built)

It validates the manifest, stages the version, and when every instance is ready and the progress migration loses nothing, activates it; connection slots need a binding in the channel before the version serves. See [`DbDataRunner/README.md`](../DbDataRunner/README.md).

## 7. csx without the .NET SDK: the answer

**Yes, for the csx kind, with the limits below.** A csx integration is compiled and packaged by `dbdatarunner` using only the .NET **runtime**, Roslyn (`Microsoft.CodeAnalysis.CSharp`, which `DbDataKit.Scripting` already uses) and the NuGet client libraries (`NuGet.Protocol`, `NuGet.Packaging`, which `DbDataKit.Scripting` already uses for `#r "nuget:"`). It runs no `dotnet`, no MSBuild and no restore.

**Evidence.**

- The pieces already existed: `DbDataKit.Scripting` resolves `#r "nuget: Id, 1.2.3"` into a closure on disk with the runtime only (exact versions, NuGet's version rules, hash pins in a lock file, a folder as an offline feed, source mapping), and `CsxCompiler` compiles against the shared framework found in the running process. Its README says "run with only the .NET runtime installed, no SDK and no MSBuild".
- What was missing was the **package** step. `DbDataKit.Runner.Packaging` (`CsxPackageBuilder`) adds it: manifest, restore, compile (a regular library compilation of all `src/**/*.csx`, deterministic, embedded PDB), the check of manifest against job classes, and the package folder. 17 tests (`DbDataKit.Runner.Packaging.Tests`) pass: a package from a real compiled NuGet package in an offline folder feed, byte-identical output on a second build, compile errors with the file and line of the source, a changed package refused under its pin, a range or two versions refused, files of the project carried, the test scripts run in the simulator (pass, fail with the message, compile error with the line).
- **Without the SDK, by hand (2026-11-11):** the built `dbdatarunner` apphost was started with `env -i PATH=/nonexistent DOTNET_ROOT=<folder holding only host/ and shared/ of the install, no sdk/ and no packs/>`; `package build` and `package test` both exited 0 on a project that restores `Humanizer.Core 2.14.1` from an offline feed (the `~/.nuget` cache copied as a folder feed) and counts words in a job. The resulting package was then `package install`ed into a runner, `server run` started, `tasks run proof/dev/hello` queued, and the run **succeeded** through the real task host (`runs list`: `succeeded proof/dev/hello v1 attempt 1`). That run is the proof that a csx-built package is a package the Runner takes without knowing how it was made.

**Limits, and what to do about them.**

| Limit | Detail |
| --- | --- |
| **Reference assemblies** | Reference assemblies (`Microsoft.NETCore.App.Ref`) ship with the SDK. With no SDK the code is compiled against the runtime's *implementation* assemblies (`System.Private.CoreLib`), found in the running process or `DOTNET_ROOT`; the assembly then binds to that implementation and is guaranteed only on the same major version of .NET (a build says so with the finding `DBD-INT-REFS`). With the reference pack on the machine (an SDK install) the build uses it and binds to `System.Runtime`. A package carries `built_against.dotnet`; the Runner checks it. |
| **NuGet restore** | Exact versions, `lib/` and `ref/` assets of the nearest target framework, the runtime's `runtimes/<rid>/` assemblies. No `build/` props or targets, no content files, no analyzers or source generators run (a package that needs MSBuild to work does not work here). Packages with **native libraries** are refused at build (`DBD-INT-NATIVE`): the task host finds native files only through a `deps.json`; use the compiled kind. Sources are NuGet v3 addresses or folders, with source mapping; the first resolution trusts and pins, later ones must match `package.lock`. |
| **Analyzers** | The kit's analyzer (`DBKR001`, `DBKR002`: step names) is **not run** by the csx build (it would need an analyzer loader); the simulator and the Runner's staging catch the same mistakes at run time. Nullable annotations are on and warnings are reported, not errors. |
| **Debugging** | An embedded portable PDB is written, so stack traces and logs name the file and line of `src/*.csx`. Attaching a debugger to a csx project in an editor is not supported (no project file); run the tests. |
| **Language** | Types and namespaces only; no top-level statements; `#load` is not supported (all files of `src/` are one compilation). Not Roslyn's interactive script with its `Submission#0`. |
| **Trust** | No syntax guard: integration code is trusted code (plan 27 section 4.7). The build and the test run it (the test in the tool's process); the Workspace runs the tool as a child process with a cleared environment, which is the boundary. |
| **Footprint** | `dbdatarunner` now carries Roslyn (about 10 MB more in a package). |
| **Self-contained hosts** | A single-file, self-contained `dbdatarunner` has no runtime folder on disk; the builder then looks for an install in `DOTNET_ROOT`, on `PATH` and in the usual places, and says so if there is none (`DBD-INT-RUNTIME`). The task host is framework-dependent anyway, so a machine that runs tasks has the runtime. |

**Compiled .NET projects still need the SDK**, and say so. The Workspace's kind reports `DBD-WS-INTEGRATION-SDK` (the words of `DotnetSdk.Missing`) from the check and the build on a server with no SDK, instead of a failing build program; so does `package build` on the command line. A compiled project is the choice for native libraries, analyzers, a project file an IDE opens, and code that wants the full MSBuild.

## 8. Dry run and sandbox rules

**Not built** (IX16, IX17), and written here as decided so that code written now does not have to change:

- A **dry run** (`tasks run --dry-run`) runs the task so that it reads the outside system and the kit refuses writes; it lists what it would have sent. A job that writes through its own HTTP client bypasses the kit's guard: the guarantee holds for the kit's clients and landing helpers only.
- A **sandbox run** goes against the channel's sandbox connection before a version is promoted; an administrator may require it (a recorded setting).
- In a **non-production channel** an outbound step uses the connection bound for that channel or is refused naming the slot.
- Until then the safeguard is the channel binding itself: a slot of a preview channel is bound to a sandbox connection by the person who binds it.

## 9. Templates

Templates generate ordinary code an author then owns (plan 27 section 4.1: "templates, not an engine"). The `integration` kind's own starter (**built**) is a minimal task with a test that builds and passes with no edits (the real-program test of `WorkspaceIntegrationKindTests` proves it). The two named templates, "pull pages from a REST API into a staging table" and "pick up CSV files from a share" (IX21), are **not built**, for two reasons found while looking:

- They stand on kit pieces that were missing when the row was looked at and now exist: a CSV reader (IX08), the HTTP helpers with paging and per-connection rate limits (IX10) and landing into a staging table (IX11). The file share half still lacks its client (SFTP, FTPS, SMB: IX04 to IX06).
- An `http` connection slot cannot yet be opened with its sign-in: `ConnectionGrant` (what `Slot<T>.OpenAsync` returns for the default slot type) carries host, port, database, a login and a token, and has no endpoint or sign-in method; `DbDataKit.Connections.Http` (IX02) signs requests in from a `connection/2` file, but nothing hands it to a task through the Runner's `ResolveConnection`. IX10 is the row that joins them.

They can be built now for the REST half, as csx templates (`IntegrationProjectKind.Template` takes a template name then) with the recorded-response tests of IX15.

## 10. Where this differs from the designs

- Plan 27 section 4.1 names a `.csx` script kind next to a compiled project. Roslyn's script model (top-level statements, `Submission#0`) cannot name its own types from a test or the manifest, so a csx *project* is a set of C# files compiled as a library (section 2). Scripts remain what tests are written as.
- Plan 28 says "a `.csx` script published compiled": that is what this does, in a build step that is not the Runner's (the Runner still never compiles).
- A task cannot open an `http` slot with its sign-in today (section 9): `ConnectionGrant` is database-shaped. The `connection/2` `http` kind and its client exist; the join through the Runner is IX10.
- `DbDataRunner/README.md` listed the outside-system connection kinds as not built; IX01 and IX02 built `http`, so that table is corrected with this page.
- Plan 27a IX20 says "build with the .NET SDK in a sandboxed child process"; the owner's direction of 2026-11-11 (csx without the SDK) changes that to "SDK only for the compiled kind".

## 11. Decisions open

- Who approves a trusted integration version (the administrator at activation today) and whether the approval is recorded as its own audit row: plan 27 section 4.7, not built.
- Whether the csx build should also run the kit's analyzer (needs an analyzer loader in the tool).
- Which runtime a build targets when the Runner that will run it is another machine (the platform belongs to the Runner chosen after the build; the same open question as Sync's `--platform`, plan 33 SC22c).
- Native libraries in a csx project (a `runtimes/` folder beside the assembly and a native-name fallback in the task host's load context).
