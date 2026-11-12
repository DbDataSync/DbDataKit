# DbDataKit.Runner.Packaging

Builds and tests an **integration project** into a runner package **without the .NET SDK** (plan 27a IX20; the whole picture is [`docs/integrations.md`](../../../docs/integrations.md)).

| Type | What it does |
| --- | --- |
| `IntegrationProject` | the layout (`package.yaml`, `src/*.csx`, `tests/*.csx`, `files/`, `package.lock`) and which way a folder builds: csx, or a compiled project |
| `CsxPackageBuilder.BuildAsync` | reads `package.yaml` with the runner's strict reader, restores the `#r "nuget:"` packages with `DbDataKit.Scripting` (exact versions, hashes pinned in `package.lock`, folder feeds for offline), compiles all of `src/**/*.csx` as one deterministic library with Roslyn against the runtime's reference pack when the machine has one and its implementation assemblies when it has not, checks that the manifest's tasks and the `[RunnerTask]` classes are one set, writes `package.yaml` and `files/` |
| `IntegrationTester.TestAsync` | builds into a scratch folder, then compiles and runs each `tests/**/*.csx` as a script against the built assembly, the kit and `RunSimulator`; `True`, `Equal`, `Fail` and `Project` are in scope (`IntegrationTestGlobals`); a script passes when it finishes |
| `DotnetSdk` | whether an SDK is on the machine, and the words to say when a compiled project needs one and there is none |
| `BuildDiagnostic` | one finding with its file (relative to the project) and line |

Only the .NET runtime is needed to build a csx project: no `dotnet`, no MSBuild. Limits (reference assemblies, native packages, analyzers, debugging) are in section 7 of the page above. The command line is `dbdatarunner package build` and `package test`; the Workspace server runs them as a child process.
