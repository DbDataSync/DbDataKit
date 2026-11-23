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
| WORKSPACEISOLATION-3 | The control service's deploy of a build is built: the Workspace serves a build's items and record (`.../builds/{b}/contents`, `.../files/{sha256}`), the console deploys an item as the person (`POST /api/deploys`, `ConsoleDeploys`), signs each version with its publisher key over the build record, and keeps the deploy record (`GET /api/deploys`; `docs/publishing.md`, "Deploying a build from the control service"). Left: the deploy to a Gateway tested end to end through the console (an app build, which needs Bun and the vendored store; a Build project's semantic models; a Store project's models), so far tested against a stand-in Gateway in `DbDataKit.Deploy.Tests`; an app version is sent over the Gateway's app calls, with no signature and no build record on the Gateway, until PUBLISHING-1 puts apps on the shared publish API; for trusted code, the approver's signature once PUBLISHING-3 is built | none | M | a test through the console deploys an app build, a Build project's semantic models and a Store project's models to a Gateway; the Gateway records the person, and for the models the signature and the build record; the deploy record lists each |
| WORKSPACEISOLATION-4 | The publish panel on the Workspace screens calls the control service's deploy (the Designer's **Build and publish…** opens this panel; SYNCAUTHORING-17 waits for this task) | WORKSPACEISOLATION-3 | M | Playwright: publish from the panel; the target shows the version |
| WORKSPACEISOLATION-5 | Runs, run results, binding tests and lineage reads go through the control service | WORKSPACEISOLATION-3 | M | the Runs tab's tests pass with the Workspace service unable to reach the Runner |
| WORKSPACEISOLATION-6 | Catalog refresh goes through the control service: the Gateway refresh (`WorkspaceCatalogRefresh`) moves there, onto the console route AUTHORING-21 builds first for the Runner read | none | S | the catalog refresh test passes with the Workspace service unable to reach a Gateway or Runner |
| WORKSPACEISOLATION-7 | Development logins (AUTHORING-19): the screen sends the login through the relay to the Runner; the Workspace service only learns which binding exists | none | M | a test sets up a login for `dev`; the value is in no Workspace log, state or audit, and the Runner holds it |
| WORKSPACEISOLATION-8 | Remove what the list marks for removal: the Workspace's publisher keys, its pairing with targets, its clients of the Runner and Gateway, and the Publisher keys screen | WORKSPACEISOLATION-4, WORKSPACEISOLATION-5, WORKSPACEISOLATION-6, WORKSPACEISOLATION-7, WORKSPACEISOLATION-11 | M | no Workspace code opens a connection to a Runner or Gateway; the removed routes answer with the sentence naming the control service |
| WORKSPACEISOLATION-9 | The Workspace service's outbound list, through `DbDataKit.Outbound` (`OutboundGate`, the `outbound:` setting, `OutboundDestination.FromGitRemote`; built): package sources and the projects' git remotes; any other address is refused and audited (the Workspace gives the library an `IOutboundAudit` that writes to its audit). The rules the operating system applies come with TRUSTZONES' open design "Outbound rules applied by the operating system" | WORKSPACEISOLATION-8 | S | a test: a call to a Runner's address is refused and audited; a NuGet restore and a git fetch work |
| WORKSPACEISOLATION-10 | Docs: the Planned entries in `docs/architecture.md` become descriptions of what is built; `docs/apps.md`, `docs/authoring-parity.md`, `docs/authoring-metadata.md` and the Workspace README follow | WORKSPACEISOLATION-8, WORKSPACEISOLATION-9 | S | the owner approves the text |
| WORKSPACEISOLATION-11 | The Rows tab of a Store project (`StoreRowsPanel`) offers the Gateways the control service manages, read from the console as the signed-in person, instead of the Workspace's paired targets (`GET /api/v1/publish-targets`, which WORKSPACEISOLATION-8 removes); a Gateway is offered when it serves the model, and rows are still read and written through the console's relay as today | none | S | vitest: the panel lists the Gateways from the console's answer and calls no Workspace target route; the Rows story (Playwright) edits a row with `publish-targets` answering 404 |

## Where each part goes (the list of WORKSPACEISOLATION-1)

Every route, class and member of the Workspace's publish, run, check and catalog paths, read from the code of `DbDataKit.Authoring`,
`DbDataKit.Authoring.Server` and `DbDataWorkspace`. Each one is marked:

- **Stays**: it builds or checks a draft, on the Workspace service.
- **Moves**: it is in the shared library `DbDataKit.Deploy` (`Shared/src`), which the control service can use; the Workspace reaches it through
  an adapter until WORKSPACEISOLATION-8.
- **Removed**: it calls another service or holds a publisher key. It goes with WORKSPACEISOLATION-8, once the task named has moved its work.

Where a part's place is open, the row names its proposal (below, under "Proposed decisions").

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
| `POST {ws}/sample` | viewer | `dbdatabuild project sample`: the draft's models run in DuckDB on generated rows | **Stays**; whether this runs project code is WORKSPACEISOLATION-P3 |
| `GET {ws}/scripts` | viewer | `dbdatasync script list` on a copy | **Stays** |
| `POST {ws}/provisioning-plan` | viewer | `dbdatasync provisioning plan <replication> --from-cache` on a copy | **Stays** |
| `POST {ws}/script-test` | author | `dbdatasync script test`: compiles the draft's C# script and runs it on generated rows, in a child process | **Removed** from the Workspace service, because it runs the author's code there; where it goes is WORKSPACEISOLATION-P2 |
| `GET /api/v1/app-build` | viewer | whether this server can build an app, and what it lacks | **Stays** |
| `POST {ws}/builds` | author | a build: the check, then what the kind makes into `artifact/` (an app: Bun into `app/`) | **Stays** |
| `GET {ws}/builds` | viewer | the builds of a draft | **Stays** |
| `GET {ws}/builds/{b}` | viewer | a build's record and result, and `publishes`: the lines of the build's `publishes.jsonl` | **Stays**; nothing new is written to `publishes` once the Workspace stops publishing; the control service keeps the deploy record |
| `GET {ws}/builds/{b}/preview[/{path}]` | viewer | the built app, read only, in a sandbox | **Stays** |
| `POST {ws}/builds/{b}/publish` | approver | sends a build to a target with the target's publisher key: an app; a Runner package (bind, then stage or serve); Store models, or Build semantic models and views, to a Gateway | **Removed**: WORKSPACEISOLATION-3 moves its work to the control service, WORKSPACEISOLATION-4 the screen; the code it calls **Moves** (below) |
| `POST {ws}/builds/{b}/preview-data` | author | sends an app build to a Gateway's `preview` channel and points an instance's preview at it | **Removed**; where it goes is open, since the tasks do not name it (WORKSPACEISOLATION-P5) |
| `GET /api/v1/publish-targets` | approver | the paired targets, whether the project's kind may go to each (`accepts`) and whether the pairing may publish | **Removed** (WORKSPACEISOLATION-8): the control service lists the Runners and Gateways it manages; the `accepts` test moves with `RouteFor` |
| `GET /api/v1/publisher-keys` | admin | each target's key reference and pairing | **Removed** (WORKSPACEISOLATION-8) |
| `PUT /api/v1/publisher-keys/{name}` | admin | records a reference to a key in the server's secret store | **Removed** (WORKSPACEISOLATION-8) |
| `POST /api/v1/publisher-keys/{name}/pair` | admin | makes a key and pairs it with a Gateway or a Runner | **Removed** (WORKSPACEISOLATION-8) |
| `DELETE /api/v1/publisher-keys/{name}` | admin | removes a key's reference | **Removed** (WORKSPACEISOLATION-8) |
| `POST {ws}/builds/{b}/run` | approver | runs a manual task of the version the build staged, or (`served`) a task of the version the channel serves, on the Runner, for the person | **Removed** (WORKSPACEISOLATION-5) |
| `GET {ws}/builds/{b}/runs/{run}` | viewer | a run and the steps it recorded, read back from the Runner | **Removed** (WORKSPACEISOLATION-5) |
| `POST {ws}/builds/{b}/binding-test` | approver | the Runner's test of the login bound to a slot | **Removed** (WORKSPACEISOLATION-5) |
| `POST {ws}/builds/{b}/contract` | approver | a Store model's approved contract: reads the plan's run from the Runner, checks the approved hash, asks each paired Gateway for its lineage and refuses a dropped column that a consumer reads (`ContractGuard`), then runs `contract` on the Runner | **Removed**; the checks it makes **Move**; the tasks do not name it (WORKSPACEISOLATION-P5) |
| `POST {ws}/catalog/refresh` | author (the draft's owner) | asks a paired Gateway for a data source's tables and columns (`ReadSchemaAsync`) and writes `catalog-cache.yaml` | **Removed** (WORKSPACEISOLATION-6). The console's relay table does not list this route, so the console refuses the screen's Gateway refresh today |

The other routes are outside these paths and stay: `GET /api/v1/info`, `GET|POST /api/v1/projects`, the workspace calls of `WorkspaceApi`
(workspaces, files, move, changes, history, file history, commit, push, merge) and `GET {ws}/events`.

### Classes and members of `DbDataKit.Authoring.Server` and `DbDataKit.Authoring`

| Part | What it does today | Goes |
| --- | --- | --- |
| `StandardProjectKinds`; `ProjectKinds`, `IProjectKind`, `ProjectDiagnostic`, `ProjectArtifact`, `ProjectPublishRoute`, `ProjectTemplate` (`DbDataKit.Authoring`) | the registry of kinds and what a kind answers | **Stays**; the moved `RouteFor` reads `ProjectPublishRoute` from `DbDataKit.Authoring` |
| `AppProjectKind` | an app: its template, and its check with `AppManifestLoader` | **Stays** |
| `BuildProjectKind` | a Build project: template (`project create starter`), check (`project show metadata`), package and models (`project publish --to runner\|gateway --out`), `CopyViews` | **Stays** |
| `BuildDraftCompiler` (`IDraftCompiler` on `BuildProjectKind`) | compile, lower, sample, lineage and import of a draft | **Stays** (`sample`: WORKSPACEISOLATION-P3) |
| `StoreProjectKind` | Store models: template, check with `DataModelLoader` in process, the models and one deploy package per model (`ModelDeployPackage`) | **Stays** |
| `SyncProjectKind` | a Sync set: template, `SecretFindings`, check and package with `dbdatasync package publish` | **Stays** |
| `SyncDraftTools`: `ScriptsAsync`, `ProvisioningPlanAsync` | `script list` and `provisioning plan --from-cache` on a copy | **Stays** |
| `SyncDraftTools`: `ScriptTestAsync` | `script test`: runs the draft's C# | **Removed** from the Workspace service (WORKSPACEISOLATION-P2) |
| `IntegrationProjectKind`: `CheckAsync`, the `package build` of `BuildCsxAsync`, `BuildCompiledAsync` | an integration's check and build | **Stays** until COMMANDLINES-8 replaces it; `dotnet publish` runs the project's MSBuild targets (WORKSPACEISOLATION-P3) |
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
| `WorkspaceGatewayPublish` (the whole class) | entity models, data source documents, semantic models and views over the shared publish API; binds slots where the Gateway lets the client; refusals in the Gateway's words | **Moves**; it signs, and sets `managedBy`, with the publisher key's identity (WORKSPACEISOLATION-P6) |
| `RunStagedTaskAsync`, `RunServedTaskAsync`, `RunOfAsync`, `TestBindingAsync`, `Refused` | a run of a staged or served version; a run and its steps; a binding test; a Runner's refusal as a status | **Moves** (WORKSPACEISOLATION-5 uses it), without its key and target lookup |
| `GatewayLineageAsync` | the lineage graph of each paired Gateway, and the Gateways that did not answer | **Moves** |
| `ReadSchemaAsync` (`WorkspaceCatalogRefresh.cs`) | a data source's tables and columns from a Gateway (`POST /api/v1/data-sources/{name}/schema`) | **Moves** (WORKSPACEISOLATION-6 uses it), without its key and target lookup; the merge into the cache is `CatalogCache.Refreshed`, already in `DbDataKit.Catalog.Cache` |
| `StagedOf` (in `WorkspaceManagement`) | finds the target, package, channel and version a build was staged with, in `publishes.jsonl` | **Removed**: the control service's deploy record replaces it |
| the contract route's checks (in `WorkspaceManagement`) | the plan's run and hash, `ContractGuard` over the Gateways' lineage, the allowances | **Moves** (WORKSPACEISOLATION-P5) |

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
  `PublishForm`, `PreviewDataForm` and `StoreDeployPanel` moves to the control service's deploy with WORKSPACEISOLATION-4; the runs of
  `ProjectRunsPanel`, `StoreDeployPanel` (deploy, contract) and `SyncProvisioningPanel` (provision) with WORKSPACEISOLATION-5; the Gateway refresh of
  `CatalogPanel` with WORKSPACEISOLATION-6; the Test of `SyncScriptsPanel` with WORKSPACEISOLATION-P2.
- `StoreRowsPanel` picks its Gateway from `publish-targets`, then edits rows through the console's relay. WORKSPACEISOLATION-11 gives it the
  control service's list of Gateways before WORKSPACEISOLATION-8 removes that route.

### The child programs

None of them calls another of our services: with `--out`, `project publish` and `package publish` write the package and do not send it. Their NuGet
restores (`package build`, `package publish --platform`) reach package sources, which the goal allows.

| Program | Started by | Commands the Workspace runs | Runs the author's code | What COMMANDLINES would change |
| --- | --- | --- | --- | --- |
| `dbdatabuild` | `BuildTool` (`BuildProjectKind`, `BuildDraftCompiler`) | `project create starter`; `project show metadata`; `project publish --to runner --out` and `--to gateway --out`; `project compile` (on a copy) and `project compile <model> --content`; `project show graph --columns`; `project import --from-cache --write` (on a copy); `project sample` | `project sample` runs the models' SQL in DuckDB on generated rows (WORKSPACEISOLATION-P3); the others read and write files | COMMANDLINES-2 moves create, check and package into `Shared/src` and replaces `BuildTool`. Compile, lower, lineage, import and sample also start `dbdatabuild`, and its text does not name them: its "Done when" (the build-kind tests pass with no `dbdatabuild`) holds only if they move too, which COMMANDLINES-1's list would say |
| `dbdatasync` | `SyncTool` (`SyncProjectKind`, `SyncDraftTools`) | `package publish --to runner --check --json`; `package publish --to runner --out --package [--platform]`, which restores the driver libraries; `script list`; `provisioning plan --from-cache`; `script test` | `script test` | COMMANDLINES-3 replaces `SyncTool` for check and package. `script list` and `provisioning plan` are offline reads that could move the same way. `script test` cannot meet COMMANDLINES-3's "Done when" (no `dbdatasync`) while it runs on the Workspace service (WORKSPACEISOLATION-P2). AUTHORING-2 builds the package for the target Runner's platform, in place of the `SyncPlatform` setting |
| `dbdatarunner` | `RunnerTool` (`IntegrationProjectKind`) | `package build --out <scratch> --no-lock-write` (the check); `package build --out` (the build); `package test` | `package test` | COMMANDLINES-8 builds in process with the shared package build library, and makes Test a run on a Runner in a non-production channel |
| `dotnet` (the .NET SDK) | `DotnetTool` (`IntegrationProjectKind`) | `--list-sdks`; `publish <project>` of a compiled integration | `publish` runs the project's MSBuild targets (WORKSPACEISOLATION-P3) | COMMANDLINES-8 says the build runs none of the author's code; it does not say how a compiled project is built that way |
| `bun` | `BunAppBundler` (`DbDataKit.Apps.Bundling`, through `WorkspaceBuilds`) | `bun install --ignore-scripts` from the vendored store, then `bun build`, in a network namespace with a cleared environment | not known: a Bun macro of the draft runs at bundle time, and the command line does not pass an option about macros (WORKSPACEISOLATION-P3) | not in COMMANDLINES |

### Counts

- Routes on these paths: 27. **Stays** 14, **Removed** 13 (two of them, `publish` and `contract`, leave code that **Moves**; `script-test` leaves
  the Workspace service for a place WORKSPACEISOLATION-P2 names).
- Classes and members: 36 rows. **Stays** 15, **Moves** 13, **Removed** 8 (counting `ScriptTestAsync` and `package test`, which leave the
  Workspace service for a Runner).
- `DbDataWorkspace` and the state module: 6 rows. **Stays** 4, **Removed** 2.
- Child programs: 5 (`dbdatabuild`, `dbdatasync`, `dbdatarunner`, and the external `dotnet` and `bun`). All stay until COMMANDLINES replaces ours;
  `script test` and `package test` leave the Workspace service for a Runner.

### What bears on WORKSPACEISOLATION-3

- What moved is in `DbDataKit.Deploy` (`Shared/src`, tests in `Shared/tests/DbDataKit.Deploy.Tests`). It composes the libraries that were already
  shared: `DefinitionsClient` and `DefinitionSigning` (`DbDataKit.State.Api`), `RunnerPublishClient` (`DbDataKit.Runner.Client`, which takes a given
  link with `ForLink`), `PackageManifestReader` (`DbDataKit.Runner.Abstractions`), `ContractGuard` and `LineageJson` (`DbDataKit.Lineage`). It does
  not reference `DbDataKit.Authoring.Server`, the app bundler or a child program (a test says so), so the control service's host can reference it.
  Where each moved part is:
  - `AppDeploy`: an app to a Gateway (`Read`, then `SendAsync`; `PreviewChannel`).
  - `ArtifactDeploy`: what a kind made, by its route: a Runner package (`model-package` takes the one model's folder the caller chose), Store models,
    a Build project's semantic models and views.
  - `GatewayDefinitionsPublish`: the former `WorkspaceGatewayPublish`. It writes the key's id as `managedBy` (WORKSPACEISOLATION-P6).
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
  one Runner package per model (`ModelPackageFolder`). WORKSPACEISOLATION-3 serves each of them, with the build record.
- A Runner or a Gateway checks a signed version against its trust store (`DefinitionSignatures`), where an administrator adds the publisher keys a group
  trusts. The control service's key has to be there for -3's signed deploys to verify; INSTALLER-16 registers publisher keys through the console.
- The run, run-read, binding-test and contract routes find what a build staged in its `publishes.jsonl` (`StagedOf`). The record of what a build was
  deployed to is kept by the control service, by Workspace server, project, workspace and build; the Builds and Runs tabs read it there, and the
  Workspace stops writing `publishes.jsonl` (WORKSPACEISOLATION-8). So WORKSPACEISOLATION-5 follows -3.
- The tasks do not name two routes: `preview-data` and `contract` (WORKSPACEISOLATION-P5).
- What the Workspace's keys published on a Gateway is owned by the key's client (WORKSPACEISOLATION-P6). -3's test of a publish over a model published
  before needs that answer.
- Not for -2 or -3: the console's relay table does not list `catalog/refresh`, so WORKSPACEISOLATION-6 moves a refresh that the screens cannot
  reach today; and the child programs' NuGet restores leave from a child process, which the outbound list of WORKSPACEISOLATION-9 (`OutboundGate`, in
  the Workspace's process) does not see.

## Needs from other subjects

- PUBLISHING-3: the approver's signature for trusted code, added to what the control service signs.
- PUBLISHING-1: an app version as a manifest and files by hash on the shared publish API, so the control service signs it over its build record and the
  Gateway keeps the record, as it does for the other kinds.
- COMMANDLINES-2, COMMANDLINES-3, COMMANDLINES-8: the Workspace stops starting `dbdatabuild`, `dbdatasync` and `dbdatarunner`.
- AUTHORING-21: the console's catalog read from a Runner channel; WORKSPACEISOLATION-6 puts the Gateway refresh on the same route (AUTHORING-21 is built
  first).

## Proposed decisions

### WORKSPACEISOLATION-P2: where a sync draft's script test runs (awaiting the owner)

Scenario: Ben writes `config/scripts/upper-name.csx` and presses Test on the Scripts tab with 5 rows. Today the Workspace starts `dbdatasync script
test upper-name --rows 5` on a copy of the draft: the script is compiled and run in a child process with a cleared environment and a time limit, and
the network is open to it. `docs/architecture.md` says that a project's scripts and tests run on a Runner.

- Option A: a Runner task. The sync runtime package gets a manual task `script-test` that does not use a connection. Test builds the draft, stages it in a
  non-production channel through the control service and runs the task there as the person, as COMMANDLINES-8 does for an integration's Test. The
  Scripts tab shows the result, with a link to the run.
- Option B: a Runner task that takes the script's text as an input, so Test does not need a build or a stage. The Runner then compiles and runs code
  that was never built or staged.
- Option C: the test stays on the Workspace service, in the sandbox an app build has (a network namespace, a cleared environment, no secret store), and
  `docs/architecture.md` names this exception.

Recommendation: A: the Runner runs only code that was built and staged, like every other run. Each Test costs a build and a stage. A needs a task
(in SYNCAUTHORING or here) before WORKSPACEISOLATION-8 removes the route.

### WORKSPACEISOLATION-P3: what else the Workspace service runs from a draft (awaiting the owner)

Scenarios found while making the list:

- Build's Sample (`dbdatabuild project sample`) runs the draft's model SQL in DuckDB on generated rows. DuckDB SQL can read a file or an address
  (`read_csv('/etc/passwd')`, `read_parquet('https://...')`) unless DuckDB's external access is turned off; the list did not check whether Sample turns
  it off.
- An integration's build runs its tests (`dbdatarunner package test`), and a compiled integration's `dotnet publish` runs the project's MSBuild
  targets. COMMANDLINES-8 takes both off the Workspace service.
- An app build runs `bun build` in a network namespace. A Bun macro (an import `with { type: "macro" }`) runs at bundle time, and the bundler's
  command line does not pass an option about macros.

- Option A: each of these leaves the Workspace service; Sample becomes a Runner task as Test does.
- Option B: the rule covers code in a general-purpose language (C#, TypeScript). Sample stays on the Workspace with DuckDB's external access off, and a
  test shows that a model that reads a file or an address is refused. App builds turn Bun macros off, or refuse a draft that has one, and a test shows
  it. The integration's tests and `dotnet publish` leave with COMMANDLINES-8.
- Option C: all of them stay, inside the sandbox an app build has.

Recommendation: B.

### WORKSPACEISOLATION-P5: where `preview-data` and `contract` go, which the tasks do not name (awaiting the owner)

Scenario: Ana sends build 3 of the app `orders-ui` to the preview of the instance `sales` (`preview-data`). Ben approves a Store model's contract that
drops `customer.fax` (`contract`); the Workspace first asks every paired Gateway whether a published consumer reads that column.

- Option A: `preview-data` goes with WORKSPACEISOLATION-3 (it is a deploy to the `preview` channel) and its form with WORKSPACEISOLATION-4.
  `contract` goes with WORKSPACEISOLATION-5 (a run and a lineage read, which -5 covers), and asks the Gateways the control service manages. The two
  rows are rewritten to name them.
- Option B: a new task for each.
- Option C: `preview-data` goes: an app is seen with data by deploying it to a test channel. `contract` as in A.

Recommendation: A.

### WORKSPACEISOLATION-P6: who owns what the Workspace's keys published on a Gateway (awaiting the owner)

Scenario: the Workspace's key `gw-main` published the data source `erp` and the semantic model `sales` to the Gateway `gw1`. The Gateway records the
key's client (`workspace-3fa2...`) as their owner (`managedBy`), and lets that client update `erp` and bind its slots. After WORKSPACEISOLATION-3 the
control service publishes `sales` again. The data sources the caller owns (`OwnedAsync`) no longer include `erp`, so the publish asks for an adoption
or for an administrator of the Gateway to bind the slot.

- Option A: a person adopts each data source once, with the confirmation the publish already asks for (`adopt`), which the Gateway records.
- Option B: before WORKSPACEISOLATION-8 removes the keys, the control service, as an administrator of each Gateway, makes its own client the owner of
  what each Workspace key made, one recorded step per Gateway. The list did not find a Gateway call that changes an owner, so B needs one.
- Option C: the owner of what the control service publishes is the person who deployed it, and ownership by a client ends.

Recommendation: B, with the control service's client as the owner of what it publishes from then on; the person is recorded as the publisher of each
version.

### WORKSPACEISOLATION-P8: where the build record of a deployed version goes (default, awaiting the owner)

Scenario: Ana deploys build `4f1c...` of the Sync project `orders` to the Runner `jobs`. The console signs the version with its publisher key, and the
Runner must keep the build record (the Workspace server, project, workspace, build, commit and what the validators found) with the version.

- Option A (built): the record is a field of the publish (`build`, one line of JSON, `build-record/1`). The signature covers it: the message is
  `dbdatakit-definition/2`, with the record's SHA-256 after the manifest's. The Runner or Gateway stores the text in a new nullable column
  (`definition_version.build_record`, an appended migration of the `definitions` module), returns it with the version, verifies it again at
  preparation, and a promotion carries it.
- Option B: the record is a file of the package (`build-record.json`), covered by the signature as every file is. No schema change, but the same
  content from another build is a new version, and the Runner installs the file with the package.
- Option C: the record goes in the version's note. It is kept, but the signature does not cover it.

Recommendation: A.

### WORKSPACEISOLATION-P9: who may deploy a build through the console (default, awaiting the owner)

Scenario: Ben holds the author role on the Workspace server and the publisher role on the Runner `jobs`. He asks the console to deploy his build to
`jobs`.

- Option A (built): the console asks for the approver role on the Workspace server (what the Workspace's own publish asked), then the target decides
  with the person's roles there. Ben is refused: "Deploying a build needs the approver role on Workspace."
- Option B: only the target's roles count; anyone who may read the build and publish on the target deploys it.
- Option C: an administrator of the console only.

Recommendation: A.

### WORKSPACEISOLATION-P10: a deploy of the same content from another build (default, awaiting the owner)

Scenario: Ana deploys build 1 of `orders` to `jobs` in `dev`, then builds again without a change and deploys build 2. The package's files and
manifest are the same.

- Option A (built): the Runner answers "the same content as before, nothing changed" and keeps version 1 with build 1's record; the console's deploy
  record names build 2 and version 1.
- Option B: the build record is part of what makes a version different, so build 2 becomes version 2 with its own record.

Recommendation: A: a version is its content, and the console's deploy records say which builds sent it.

### WORKSPACEISOLATION-P11: who the console names as owner of what it publishes on a Gateway (default, awaiting the owner)

Scenario: the console deploys the Store project `territories` to the Gateway `gw1`. The entity model's manifest names an owner (`managedBy`), and the
Gateway gives the caller's client the definition.

- Option A (built): `managedBy` is the console's own id (its identity's id, `console-...`), and the Gateway records the console's client as the
  definition's owner. This is option B of WORKSPACEISOLATION-P6 for what the console publishes from now on; what the Workspace's keys made before
  still needs P6's answer.
- Option B: `managedBy` names the person who deployed it, as option C of WORKSPACEISOLATION-P6.

Recommendation: A, following P6's recommendation.

### WORKSPACEISOLATION-P12: how long the console keeps its deploy records (default, awaiting the owner)

Scenario: a team deploys twenty builds a day for two years. The console's `deploys` state module keeps one row per deploy, with the build record.

- Option A (built): every deploy is kept until an administrator removes it, as releases are.
- Option B: a retention age (180 days, say), as the runs have.

Recommendation: A for now: a row is small, and the Builds and Runs tabs read the history.

## Shared areas touched

`DbDataKit.Authoring.Server`, the control service's host, the Workspace screens (`Shared/web/workspace`), `DbDataKit.State.Api` (the publish client
and signing), `docs/ui-editors.md` (the Publisher keys screen goes).
