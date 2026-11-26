# WORKSPACEISOLATION: the Workspace service calls no other service and never handles a secret
<!-- reviewed: 2026-11-20 -->

## Goal

The Workspace service is called only by the control service. It calls only package sources (NuGet, npm) and git remotes, never handles a secret, and
never executes code from a project. It builds and checks projects. The control service pulls the built package from it and deploys it to a Runner or
Gateway as the person. Runs, binding tests, catalog reads and development logins go through the control service (`docs/architecture.md`, "Who calls
whom" and "Planned architecture changes").

## What exists today

- The Workspace service publishes to Gateways and Runners itself (`WorkspacePublishing`, `WorkspacePublishingArtifacts`, `WorkspaceGatewayPublish` in
  `DbDataKit.Authoring.Server`), signed in with its own publisher key for each target, kept in its secret store and managed on its Publisher keys
  screen.
- It starts runs, reads run results, tests bindings and reads lineage on Runners (`RunStagedTaskAsync`, `RunServedTaskAsync`, `RunOfAsync`,
  `TestBindingAsync`, `GatewayLineageAsync`), and reads a data source's catalog through a Gateway (`WorkspaceCatalogRefresh`).
- It starts `dbdatabuild`, `dbdatasync` and `dbdatarunner` as child programs (COMMANDLINES).
- It runs a sync draft's script against generated input in a child process (`POST .../script-test`, `SyncDraftTools`), which executes the
  author's code on the Workspace service.

## What is kept

Code that does useful work in today's publish path is kept and moved into shared libraries (`Shared/src/DbDataKit.*`), where the control service uses
it. Only the parts that make the Workspace service call another service are removed. Known so far:

- **Building a package**: each kind's package output, the manifest and the files by SHA-256, and a kind's validators: a build fails on an error they
  find, and the build record says that the build passed them, with their findings. This stays on the Workspace service.
- **Checks and validation before a publish**: the package's manifest, the formats and versions, the kind's route to the target's product, the checks of
  slots and bindings, data source adoption, and `preflight` on the Runner. These run where the deploy is decided, in the control service, which never
  starts `dbdatabuild` or `dbdatasync`.
- **The shared publish API's client and signing**: `DefinitionsClient`, `PublishArtifacts` and `DefinitionSigning`.
- **The calls to a Runner or Gateway**: run a staged or served task, a run's result, a binding test, lineage, and the catalog read.
- **Target pinning** by certificate fingerprint.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| WORKSPACEISOLATION-1 | The list: every class and route of the Workspace's publish, run, check and catalog paths, each marked stays on the Workspace (building, checking a draft), moves to a shared library for the control service, or is removed (only what calls another service or holds a publisher key); it also says where the sync draft's `script-test` goes, since it runs the author's code | none | S | the list is in this file and the owner has read it |
| WORKSPACEISOLATION-8 | Remove what the list marks for removal: the Workspace's publisher keys, its pairing with targets, its clients of the Runner and Gateway, and the Publisher keys screen | none | M | no Workspace code opens a connection to a Runner or Gateway; the removed routes answer with the sentence naming the control service |
| WORKSPACEISOLATION-9 | The Workspace service's outbound list, through `DbDataKit.Outbound` (`OutboundGate`, the `outbound:` setting, `OutboundDestination.FromGitRemote`; built): package sources and the projects' git remotes; any other address is refused and audited (the Workspace gives the library an `IOutboundAudit` that writes to its audit). The rules the operating system applies come with TRUSTZONES' open design "Outbound rules applied by the operating system" | WORKSPACEISOLATION-8 | S | a test: a call to a Runner's address is refused and audited; a NuGet restore and a git fetch work |
| WORKSPACEISOLATION-10 | Docs: the Planned entries in `docs/architecture.md` become descriptions of what is built; `docs/apps.md`, `docs/authoring-parity.md`, `docs/authoring-metadata.md` and the Workspace README follow; the Workspace row of `docs/architecture.md` says that the code it never executes is code in a general-purpose language (C#, TypeScript): a draft's SQL runs in DuckDB with external access off, and a draft with a Bun macro is refused | WORKSPACEISOLATION-8, WORKSPACEISOLATION-9 | S | the owner approves the text |
| WORKSPACEISOLATION-14 | Sample and model tests on the Workspace service run the draft's SQL in DuckDB with external access and extension loading off (`SampleRun`, `ModelTestRunner` already set it); a test keeps it so | none | S | a model that reads a file (`read_csv('/etc/passwd')`) or an address (`read_parquet('https://...')`) fails its Sample and its model test with DuckDB's refusal, and so does one that turns external access back on |
| WORKSPACEISOLATION-15 | An app build refuses a draft that has a Bun macro (an import `with { type: "macro" }`) before `bun build` starts, with a diagnostic naming the file and line; the draft's check shows the same diagnostic | none | S | a draft whose source imports a macro fails its check and its build naming the file and line, and `bun build` is not started |

## Where each part goes (the list of WORKSPACEISOLATION-1)

Every route, class and member of the Workspace's publish, run, check and catalog paths, read from the code of `DbDataKit.Authoring`,
`DbDataKit.Authoring.Server` and `DbDataWorkspace`. Each one is marked:

- **Stays**: it builds or checks a draft, on the Workspace service.
- **Moves**: it is in the shared library `DbDataKit.Deploy` (`Shared/src`), which the control service can use; the Workspace reaches it through
  an adapter until WORKSPACEISOLATION-8.
- **Removed**: it calls another service or holds a publisher key. It goes with WORKSPACEISOLATION-8, once the task named has moved its work.

### Routes of the management API

`WorkspaceManagement.Map` (`Shared/src/DbDataKit.Authoring.Server/WorkspaceManagement.cs`). `{ws}` is `/api/v1/projects/{project}/workspaces/{id}`
and `{b}` a build. The role is the one the server and the console's relay table ask for.

| Route | Role | What it does today | Goes |
| --- | --- | --- | --- |
| `GET /api/v1/project-kinds` | viewer | the kinds, their templates and where each publishes (`publishes`: the product and what arrives there) | **Stays**. The control service can read `publishes` from it to offer the targets of a project |
| `GET {ws}/check` | viewer | the kind's validators, then the catalog cache's reader, on the draft (`DraftCheck`) | **Stays** |
| `POST {ws}/compile` | viewer | `dbdatabuild project compile` on a copy of the draft | **Stays** |
| `POST {ws}/lowered` | viewer | `dbdatabuild project compile <model> --content`, read only | **Stays** |
| `POST {ws}/lineage` | viewer | `dbdatabuild project show graph --columns`, read only | **Stays** |
| `POST {ws}/import-preview` | viewer | `dbdatabuild project import --from-cache --write` on a copy | **Stays** |
| `POST {ws}/sample` | viewer | `dbdatabuild project sample`: the draft's models run in DuckDB on generated rows | **Stays**, with DuckDB's external access off (WORKSPACEISOLATION-14) |
| `GET {ws}/scripts` | viewer | `dbdatasync script list` on a copy | **Stays** |
| `POST {ws}/provisioning-plan` | viewer | `dbdatasync provisioning plan <replication> --from-cache` on a copy | **Stays** |
| `POST {ws}/script-test` | author | `dbdatasync script test`: compiles the draft's C# script and runs it on generated rows, in a child process | **Removed** from the Workspace service, because it runs the author's code there; it is the Sync set's manual task `script-test` on a Runner, and the route answers 410 saying so (`docs/authoring-metadata.md`, "A script's test runs on a Runner") |
| `GET /api/v1/app-build` | viewer | whether this server can build an app, and what it lacks | **Stays** |
| `POST {ws}/builds` | author | a build: the check, then what the kind makes into `artifact/` (an app: Bun into `app/`) | **Stays** |
| `GET {ws}/builds` | viewer | the builds of a draft | **Stays** |
| `GET {ws}/builds/{b}` | viewer | a build's record and result, and `publishes`: the lines of the build's `publishes.jsonl` | **Stays**; nothing new is written to `publishes` once the Workspace stops publishing; the control service keeps the deploy record |
| `GET {ws}/builds/{b}/preview[/{path}]` | viewer | the built app, read only, in a sandbox | **Stays** |
| `POST {ws}/builds/{b}/publish` | approver | sends a build to a target with the target's publisher key: an app; a Runner package (bind, then stage or serve); Store models, or Build semantic models and views, to a Gateway | **Removed**: its work is the console's `POST /api/deploys` (`ConsoleDeploys`), which the Builds tab calls; the code it calls **Moves** (below) |
| `POST {ws}/builds/{b}/preview-data` | author | sends an app build to a Gateway's `preview` channel and points an instance's preview at it | **Removed**; it is the console's `POST /api/deploys` with `previewInstance`, a deploy to the `preview` channel |
| `GET /api/v1/publish-targets` | approver | the paired targets, whether the project's kind may go to each (`accepts`) and whether the pairing may publish | **Removed** (WORKSPACEISOLATION-8): the control service lists the Runners and Gateways it manages; the `accepts` test moves with `RouteFor` |
| `GET /api/v1/publisher-keys` | admin | each target's key reference and pairing | **Removed** (WORKSPACEISOLATION-8) |
| `PUT /api/v1/publisher-keys/{name}` | admin | records a reference to a key in the server's secret store | **Removed** (WORKSPACEISOLATION-8) |
| `POST /api/v1/publisher-keys/{name}/pair` | admin | makes a key and pairs it with a Gateway or a Runner | **Removed** (WORKSPACEISOLATION-8) |
| `DELETE /api/v1/publisher-keys/{name}` | admin | removes a key's reference | **Removed** (WORKSPACEISOLATION-8) |
| `POST {ws}/builds/{b}/run` | approver | runs a manual task of the version the build staged, or (`served`) a task of the version the channel serves, on the Runner, for the person | **Removed** (WORKSPACEISOLATION-8); the screens call the console's `/api/deploys/{id}/...` |
| `GET {ws}/builds/{b}/runs/{run}` | viewer | a run and the steps it recorded, read back from the Runner | **Removed** (WORKSPACEISOLATION-8); the screens call the console's `/api/deploys/{id}/...` |
| `POST {ws}/builds/{b}/binding-test` | approver | the Runner's test of the login bound to a slot | **Removed** (WORKSPACEISOLATION-8); the screens call the console's `/api/deploys/{id}/...` |
| `POST {ws}/builds/{b}/contract` | approver | a Store model's approved contract: reads the plan's run from the Runner, checks the approved hash, asks each paired Gateway for its lineage and refuses a dropped column that a consumer reads (`ContractGuard`), then runs `contract` on the Runner | **Removed** (WORKSPACEISOLATION-8); its checks are the console's `POST /api/deploys/{id}/contract`, which asks the Gateways the console manages |
| `POST {ws}/catalog/refresh` | author (the draft's owner) | asks a paired Gateway for a data source's tables and columns (`ReadSchemaAsync`) and writes `catalog-cache.yaml` | **Removed** (WORKSPACEISOLATION-8). The screens no longer call it: the Catalog tab's Gateway refresh asks the console (`POST api/workspace-catalog/{workspace}/{project}/gateway-read`, `ConsoleCatalogRead.GatewayAsync`) |

The other routes are outside these paths and stay: `GET /api/v1/info`, `GET|POST /api/v1/projects`, the workspace calls of `WorkspaceApi`
(workspaces, files, move, changes, history, file history, commit, push, merge) and `GET {ws}/events`.

### Classes and members of `DbDataKit.Authoring.Server` and `DbDataKit.Authoring`

| Part | What it does today | Goes |
| --- | --- | --- |
| `StandardProjectKinds`; `ProjectKinds`, `IProjectKind`, `ProjectDiagnostic`, `ProjectArtifact`, `ProjectPublishRoute`, `ProjectTemplate` (`DbDataKit.Authoring`) | the registry of kinds and what a kind answers | **Stays**; the moved `RouteFor` reads `ProjectPublishRoute` from `DbDataKit.Authoring` |
| `AppProjectKind` | an app: its template, and its check with `AppManifestLoader` | **Stays** |
| `BuildProjectKind` | a Build project: template (`project create starter`), check (`project show metadata`), package and models (`project publish --to runner\|gateway --out`), `CopyViews` | **Stays** |
| `BuildDraftCompiler` (`IDraftCompiler` on `BuildProjectKind`) | compile, lower, sample, lineage and import of a draft | **Stays** (`sample`: WORKSPACEISOLATION-14) |
| `StoreProjectKind` | Store models: template, check with `DataModelLoader` in process, the models and one deploy package per model (`ModelDeployPackage`) | **Stays** |
| `SyncProjectKind` | a Sync set: template, `SecretFindings`, check and package with `dbdatasync package publish` | **Stays** |
| `SyncDraftTools`: `ScriptsAsync`, `ProvisioningPlanAsync` | `script list` and `provisioning plan --from-cache` on a copy | **Stays** |
| `SyncDraftTools`: `ScriptTestAsync` | `script test`: runs the draft's C# | **Removed** from the Workspace service: the Sync set's task `script-test` on a Runner |
| `IntegrationProjectKind`: `CheckAsync`, the `package build` of `BuildCsxAsync`, `BuildCompiledAsync` | an integration's check and build | **Stays** until COMMANDLINES-8 replaces it; `dotnet publish` runs the project's MSBuild targets, and leaves with COMMANDLINES-8 |
| `IntegrationProjectKind`: the `package test` of `BuildCsxAsync` | runs the author's tests before the package is made | **Removed** from the Workspace service by COMMANDLINES-8, which makes Test a run on a Runner |
| `IntegrationTemplates` | the starting points of an integration | **Stays** |
| `ChildProgram`, `BuildTool`, `SyncTool`, `RunnerTool`, `DotnetTool` | start a program with a cleared environment and a time limit | **Stays** until COMMANDLINES replaces each program (below) |
| `PackageArtifacts` | the package's name, and what the program printed about it | **Stays** |
| `CatalogCacheCheck`, `DraftCheck` | the cache's reader, and the check of a draft | **Stays** |
| `WorkspaceBuilds`: `Request`, the app build, `RequestArtifact`, `RunKindAsync`, `Find`, `Readiness`, the folders | the builds of a draft | **Stays** |
| `WorkspaceBuilds`: `Publishes`, `RecordPublish` (`publishes.jsonl`) | what was published from a build; the run routes read it | **Removed** with the publish route; the control service keeps the deploy record |
| `WorkspacePreview`, `WorkspaceAppSettings`, `WorkspaceProjects` | the preview, the `apps:` settings, the projects | **Stays** |
| `WorkspaceOrigin` | the token `[workspace:<instance>/<project>/<workspace>]` at the end of a published version's note | **Moves**: the control service writes the note |
| `PublishTarget`, `Targets`, `Find`, `Save` (`publish-targets.json`) | the targets this server paired with: address, instance, pinned certificate fingerprint, roles, product | **Removed** (WORKSPACEISOLATION-8): the servers the control service manages replace them |
| `ReferenceFor`, `PairAsync`, `DetectProductAsync` | make a publisher key, keep it in the secret store and pair it with a target | **Removed** (WORKSPACEISOLATION-8) |
| `KeyAsync`, `ResolveTarget`, `RunnerClientAsync` | read a key from the secret store; find a target and the role its pairing allows | **Removed** (WORKSPACEISOLATION-8) |
| `GatewayLink`, `GatewayHttp` | a call to a Gateway as the key's client, over the pinned certificate | **Removed**: the control service calls as the person, as `ConsolePromotions` already does for the binding compare |
| `PublishProducts`, `RouteFor` | the products a target can be; whether a kind may go to a target's product | **Moves** |
| `WorkspacePublishException` | a refusal: its status and, when an adoption is asked, the data source the screen asks about | **Moves** |
| `ArtifactPublishOptions` | channel, serve, expected version, bind, data source, adopt, model, note | **Moves** |
| `PublishAsync`, `PreviewChannel` | an app to a Gateway: the Gateway's check, then the version, or the preview channel | **Moves**, without its key and target lookup |
| `PublishArtifactAsync` | picks the way to send by what the kind makes | **Moves**, without its key and target lookup |
| `ModelPackageFolder` | which model's deploy package of a Store build is sent | **Stays**: it names the packages a Store build serves, one per model |
| `PublishPackageAsync` | a Runner package: reads `package.yaml`, binds slots, publishes or stages through `RunnerPublishClient` | **Moves** |
| `PublishModelsAsync`, `PublishSemanticModelsAsync` | Store models, and Build semantic models with their views, to a Gateway | **Moves** |
| `WorkspaceGatewayPublish` (the whole class) | entity models, data source documents, semantic models and views over the shared publish API; binds slots where the Gateway lets the client; refusals in the Gateway's words | **Moves**; it signs, and sets `managedBy`, with the publisher key's identity; an administrator hands what the Workspace's keys published to the console first (the console's `POST /api/deploys/handover`, `docs/publishing.md`) |
| `RunStagedTaskAsync`, `RunServedTaskAsync`, `RunOfAsync`, `TestBindingAsync`, `Refused` | a run of a staged or served version; a run and its steps; a binding test; a Runner's refusal as a status | **Moves**, without its key and target lookup; the console's runs use it (`ConsoleDeployRuns`) |
| `GatewayLineageAsync` | the lineage graph of each paired Gateway, and the Gateways that did not answer | **Moves** |
| `ReadSchemaAsync` (`WorkspaceCatalogRefresh.cs`) | a data source's tables and columns from a Gateway (`POST /api/v1/data-sources/{name}/schema`) | **Moves**: `GatewayCalls.ReadSchemaAsync` (`DbDataKit.Deploy`), which the console's `gateway-read` calls over `ConsolePromotions.LinkFor` as the person; the Workspace's adapter keeps its key and target lookup until WORKSPACEISOLATION-8; the merge into the cache is `CatalogCache.Refreshed`, already in `DbDataKit.Catalog.Cache` |
| `StagedOf` (in `WorkspaceManagement`) | finds the target, package, channel and version a build was staged with, in `publishes.jsonl` | **Removed**: the control service's deploy record replaces it |
| the contract route's checks (in `WorkspaceManagement`) | the plan's run and hash, `ContractGuard` over the Gateways' lineage, the allowances | **Moves**; the console's contract uses it (`ConsoleDeployRuns`) |

`WorkspaceApi`, `WorkspaceFiles` and `Workspaces` (`DbDataKit.Authoring`) are outside these paths and stay.

### `DbDataWorkspace` and the state module

| Part | What it does today | Goes |
| --- | --- | --- |
| `WorkspaceServer.Build` | wires the engine | **Stays**; the `WorkspacePublishing` it makes with the secret store goes (WORKSPACEISOLATION-8) |
| `WorkspaceAdminCommand.Publisher` (`dbdataworkspace publisher pair\|list`) and its line in `WorkspaceCli` | pairs a key with a target from the command line, and lists the keys | **Removed** (WORKSPACEISOLATION-8) |
| `WorkspaceAdminCommand.Project`, `WorkspaceAdminCommand.Workspace`, `WorkspaceCli`, `WorkspaceService`, `WorkspaceState` | the server's command line, its service and its state | **Stays** |
| `WorkspaceSecrets` | the server's secret store: the state connection's login, and the publisher keys (`dbdatakit:workspace:publisher-key:<name>`) | **Stays** for the state login; WORKSPACEISOLATION-8 deletes the publisher keys in it |
| `WorkspaceSettings`: `BuildExecutable`, `SyncExecutable`, `SyncPlatform`, `RunnerExecutable`, `DotnetExecutable`, `IntegrationSources`, `IntegrationCache` | where the child programs and the integrations' packages are | **Stays** until COMMANDLINES replaces the programs |
| `WorkspaceStore`: `SetPublisherKey`, `PublisherKeys`, `RemovePublisherKey`; the table `publisher_key` of `WorkspaceModule` (`DbDataKit.State`) | the references to the keys | **Removed** (WORKSPACEISOLATION-8; the table goes by an appended migration) |

### Callers outside these projects

- The console's relay table, `ServerProducts.WorkspaceRoutes` (`DbDataKit/src/DbDataKit.Server/ServerProducts.cs`): each removed route's row leaves with
  the route.
- The Workspace screens (`Shared/web/workspace`): `KeysPage` (the Publisher keys screen) goes with WORKSPACEISOLATION-8; the publish of `BuildsPanel`,
  `PublishForm`, `PreviewDataForm` and `StoreDeployPanel` calls the control service's deploy; the runs of
  `ProjectRunsPanel`, `StoreDeployPanel` (deploy, contract) and `SyncProvisioningPanel` (provision) go through the console's `/api/deploys/{id}/...`
  (`docs/publishing.md`, "Runs of a deployed build"); the Gateway refresh of
  `CatalogPanel` asks the console (`gateway-read`); the Test of `SyncScriptsPanel` builds, stages through the console's deploy and runs `script-test` on a Runner.
- `StoreRowsPanel` takes its Gateways from the console (`GET /api/instances`) and asks each one's store through the relay which models it serves; it
  no longer calls `publish-targets`.

### The child programs

None of them calls another of our services: with `--out`, `project publish` and `package publish` write the package and do not send it. Their NuGet
restores (`package build`, `package publish --platform`) reach package sources, which the goal allows.

| Program | Started by | Commands the Workspace runs | Runs the author's code | What COMMANDLINES would change |
| --- | --- | --- | --- | --- |
| `dbdatabuild` | `BuildTool` (`BuildProjectKind`, `BuildDraftCompiler`) | `project create starter`; `project show metadata`; `project publish --to runner --out` and `--to gateway --out`; `project compile` (on a copy) and `project compile <model> --content`; `project show graph --columns`; `project import --from-cache --write` (on a copy); `project sample` | `project sample` runs the models' SQL in DuckDB on generated rows, with DuckDB's external access off (WORKSPACEISOLATION-14); the others read and write files | COMMANDLINES-2 moves create, check and package into `Shared/src` and replaces `BuildTool`. Compile, lower, lineage, import and sample also start `dbdatabuild`, and its text does not name them: its "Done when" (the build-kind tests pass with no `dbdatabuild`) holds only if they move too, which COMMANDLINES-1's list would say |
| `dbdatasync` | `SyncTool` (`SyncProjectKind`, `SyncDraftTools`) | `package publish --to runner --check --json`; `package publish --to runner --out --package [--platform]`, which restores the driver libraries; `script list`; `provisioning plan --from-cache` | none |  COMMANDLINES-3 replaces `SyncTool` for check and package. `script list` and `provisioning plan` are offline reads that could move the same way. `script test` runs on a Runner now (the set's task `script-test`), so the Workspace no longer starts it. AUTHORING-2 builds the package for the target Runner's platform, in place of the `SyncPlatform` setting |
| `dbdatarunner` | `RunnerTool` (`IntegrationProjectKind`) | `package build --out <scratch> --no-lock-write` (the check); `package build --out` (the build); `package test` | `package test` | COMMANDLINES-8 builds in process with the shared package build library, and makes Test a run on a Runner in a non-production channel |
| `dotnet` (the .NET SDK) | `DotnetTool` (`IntegrationProjectKind`) | `--list-sdks`; `publish <project>` of a compiled integration | `publish` runs the project's MSBuild targets; it leaves with COMMANDLINES-8 | COMMANDLINES-8 says the build runs none of the author's code; it does not say how a compiled project is built that way |
| `bun` | `BunAppBundler` (`DbDataKit.Apps.Bundling`, through `WorkspaceBuilds`) | `bun install --ignore-scripts` from the vendored store, then `bun build`, in a network namespace with a cleared environment | a Bun macro of the draft would run at bundle time, and the command line has no option about macros; WORKSPACEISOLATION-15 refuses a draft that has one | not in COMMANDLINES |

### Counts

- Routes on these paths: 27. **Stays** 14, **Removed** 13 (two of them, `publish` and `contract`, leave code that **Moves**; `script-test` left
  the Workspace service for a Runner task).
- Classes and members: 36 rows. **Stays** 15, **Moves** 13, **Removed** 8 (counting `ScriptTestAsync` and `package test`, which leave the
  Workspace service for a Runner).
- `DbDataWorkspace` and the state module: 6 rows. **Stays** 4, **Removed** 2.
- Child programs: 5 (`dbdatabuild`, `dbdatasync`, `dbdatarunner`, and the external `dotnet` and `bun`). All stay until COMMANDLINES replaces ours;
  `script test` left the Workspace service for a Runner, and `package test` leaves it.

### What the control service's deploy uses

- What moved is in `DbDataKit.Deploy` (`Shared/src`, tests in `Shared/tests/DbDataKit.Deploy.Tests`). It composes the libraries that were already
  shared: `DefinitionsClient` and `DefinitionSigning` (`DbDataKit.State.Api`), `RunnerPublishClient` (`DbDataKit.Runner.Client`, which takes a given
  link with `ForLink`), `PackageManifestReader` (`DbDataKit.Runner.Abstractions`), `ContractGuard` and `LineageJson` (`DbDataKit.Lineage`). It does
  not reference `DbDataKit.Authoring.Server`, the app bundler or a child program (a test says so), so the control service's host can reference it.
  Where each moved part is:
  - `AppDeploy`: an app to a Gateway (`Read`, then `SendAsync`; `PreviewChannel`).
  - `ArtifactDeploy`: what a kind made, by its route: a Runner package (`model-package` takes the one model's folder the caller chose), Store models,
    a Build project's semantic models and views.
  - `GatewayDefinitionsPublish`: the former `WorkspaceGatewayPublish`. It writes the id of the key it signs with as `managedBy`: the console's id when the
    control service deploys. The console takes over what the Workspace's keys made with `POST /api/deploys/handover` (the Gateway's
    `PUT /api/v1/settings/handovers/{client}`).
  - `RunnerCalls`: runs of a staged or served version, a run and its steps, a binding test.
  - `GatewayCalls`: a data source's catalog (`ReadSchemaAsync`) and the lineage of a list of Gateways (`LineageAsync`).
  - `ContractPlans`: the contract route's checks (the plan's run and hash, the consumers through `ContractGuard`, the inputs of the run).
  - `DeployRoutes.RouteFor` (it takes the kind's name and its routes, which the control service can read from `GET /api/v1/project-kinds`),
    `DeployTarget`, `DeployOptions`, `DeployException`, `DeployProducts` and `DeployOrigin`.
- Every moved call takes the link it calls through (`IDefinitionsLink`, paths relative to `/api/v1/`) and the key it signs with (`ConsoleIdentity`).
  The Workspace's adapter (`WorkspacePublishing`, `WorkspaceGatewayPublish`) passes a link that signs with its publisher key over the pinned
  certificate until WORKSPACEISOLATION-8; the control service passes a link as the person (`ConsolePromotions.LinkFor`, as `ConsoleBindingCompare`
  does) and its own key.
- A build has more than one thing to send. A Build build has a Runner package and Gateway models with their views; a Store build has Gateway models and
  one Runner package per model (`ModelPackageFolder`). The Workspace serves each of them (`.../builds/{b}/contents`), with the build record.
- A Runner or a Gateway checks a signed version against its trust store (`DefinitionSignatures`), where an administrator adds the publisher keys a group
  trusts. The control service's key has to be there for its signed deploys to verify (`ConsoleTrust` puts it there); INSTALLER-16 registers publisher keys through the console.
- The run, run-read, binding-test and contract routes find what a build staged in its `publishes.jsonl` (`StagedOf`). The record of what a build was
  deployed to is kept by the control service, by Workspace server, project, workspace and build; the Builds and Runs tabs read it there, and the
  Workspace stops writing `publishes.jsonl` (WORKSPACEISOLATION-8). The Builds, Runs, Provisioning and Deploy tabs read the console's record now.
- A preview with data is the console's deploy with `previewInstance`, which the Builds tab's form calls; the screens run `contract` through
  the console's `POST /api/deploys/{id}/contract`.
- What the Workspace's keys published on a Gateway is owned by the key's client until an administrator hands it to the console
  (`POST /api/deploys/handover`); -3's test of a publish over a model published before does that first.
- Not for the deploy: the child programs' NuGet restores leave from a child process, which the outbound list of WORKSPACEISOLATION-9 (`OutboundGate`, in
  the Workspace's process) does not see.

## Proposed decisions

### WORKSPACEISOLATION-P31: a script's Test stages the whole replication set (awaiting the owner)

Test on the Scripts tab builds the draft and stages the whole set's package, so it needs what any staged version needs. Two cases a person meets:

- Ana's new draft has a mapping whose source columns were never read (no cache). The build fails, and Test says "The draft did not build, so the script
  was not tested: ... has not been cached". Before, she could test the script with no replication at all.
- Ben's draft builds, but `dev` on `runners-eu` does not bind the slots `source_db` and `target_db`. The Runner refuses the stage naming them, although
  `script-test` opens no connection. He binds them once (**Set up for development**), and every later Test works.

Options:

- A, as built: Test stages the set's package; the draft must build and the channel must bind the set's slots.
- B: the Sync kind's build adds a second item, a package with only the scripts and the task `script-test` and no slot, which builds whenever the
  scripts are there; Test stages that one.

Recommendation: A for now. B is worth it only if authors write scripts before the first replication; it adds a build item and a second package name
on the Runner.

### WORKSPACEISOLATION-P32: where a Test is staged (default, awaiting the owner)

Beside the Test buttons the tab offers the console's Runners and a channel. It starts on the Runner and channel of the draft's newest deploy to a
Runner, whatever the channel, and on the first Runner the console lists, in `dev`, when the draft was never deployed. `prod` is a channel like any
other: picking it shows the production warning, and the Runner's roles decide. Example: Ana last staged the draft in `test` on `runners-eu`; Test
stages there again.

Each Test stages a new version in that channel (the same content makes no new version), so it replaces what was staged there before: a version Ben
staged in `dev` for a review is no longer the staged one after Ana's Test.

Options:

- A, as built: the preset above, staging in a shared channel such as `dev`.
- B: tests stage in a channel kept for them (`script-test`), which every Runner then has to serve, so a staged review version is never replaced.

Recommendation: A; B only if teams stage review versions in `dev`.

## Needs from other subjects

- PUBLISHING-3: the approver's signature for trusted code, added to what the control service signs.
- PUBLISHING-1: an app version as a manifest and files by hash on the shared publish API, so the control service signs it over its build record and the
  Gateway keeps the record, as it does for the other kinds.
- COMMANDLINES-2, COMMANDLINES-3, COMMANDLINES-8: the Workspace stops starting `dbdatabuild`, `dbdatasync` and `dbdatarunner`.

## Shared areas touched

`DbDataKit.Authoring.Server`, the control service's host, the Workspace screens (`Shared/web/workspace`), `DbDataKit.State.Api` (the publish client
and signing), `docs/ui-editors.md` (the Publisher keys screen goes).
