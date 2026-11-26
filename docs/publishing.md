# Publishing: how a definition reaches the serving layer and the runner

What is built of plan 30 ("one way to publish"), read from the code on 2026-11-09. What is left is [planning/PUBLISHING.md](../planning/PUBLISHING.md) and [planning/CHANNELS.md](../planning/CHANNELS.md).

Back-end products (Build, the apps back end, DbDataStore, DbDataIntegration) **publish versioned definitions** to a *target group*: a serving group (the Gateway, plan 20) or a runner group (DbDataRunner, plan 28). The target runs from what was published and does not call the publisher. One mechanism, the `definitions` state module, serves both.

## The store (`DbDataKit.State`, `Definitions/`)

| Table | Holds |
| --- | --- |
| versions | every version of a definition, keyed by kind, name and number: the manifest as text, its SHA-256, the format of the manifest (`definition_version.format`, plan 24a UC01), what it depends on (`kind:name@version`), the files (path, SHA-256, size, media type, payload id), who published it, when, a note. Never changed or removed |
| serving | per kind, name and **channel**: the active version, the one before it and a staged version |
| bindings | per channel, kind and name: what each **slot** the definition declares (a connection, a secret, a data source) is bound to there |
| instance status | per instance, kind, name and channel: the version it holds and its state (`preparing`, `ready`, `active`, `failed`), the error; an instance that left the group is marked gone and removed by retention (`InstanceStatusStore`) |
| events | append-only: publish, stage, activate, switch, rollback, retire, bind; followers poll it and it is the audit of what a channel serves |

- **Publish** (`DefinitionPublisher`) validates with the kind's validator, stores the next version and points the channel at it (`serve`) or stores it as the channel's staged version (`stage`), in one transaction. The same content again changes nothing, so a retry is safe. `expected_version` is the optimistic check.
- **Activate** makes the staged version active; `ActivateIfAllReady` does it when every live instance reports `ready`. A version staged with a `hold` (the publish body's text, which says what it waits for) is never activated by `ActivateIfAllReady`; an administrator's activation ends the hold, and the next stage in the channel replaces it. A failed preparation never activates silently; an administrator may activate anyway as a recorded choice. **Switch** points a channel at any stored version (promotion), **rollback** returns to the previous one, **retire** stops serving and keeps the versions.
- **Checks on every move:** declared slots must be bound in the channel (a missing binding refuses the move and names the slot; a slot declared `access: read` is read from production's binding when the channel has none, a write slot never is (the integration kind declares it per connection of a task in `package.yaml`, `access: read|write`, `ConnectionSlot.Access`; the agent kinds' model slots are all write): `DefinitionPublisher.ResolveSlots` says for each slot whether its binding is the channel's own or inherited); `depends_on` must be active in the target channel; a kind that is trusted code needs an approved version (`RequiresApproval`); a format that a live instance of the group cannot read is refused naming the instance (`DefinitionFormats`, `DefinitionFormat.Refusal`). `PromoteWithDependencies` moves a version with the dependencies it pins, in one transaction, and the matching rollback restores them (plan 32 DBR10).
- **Artifacts** are content-addressed payloads (`PublishArtifacts`, over `PayloadStore`): a file whose hash is stored is not written again. The runner side has the HTTP form: `HEAD` and `PUT /api/v1/artifacts/{sha256}` (refused when the hash differs), `PUT /api/v1/definitions/integration/{name}` with the manifest, the file list, `depends_on`, channel, `serve` or `stage` and `expected_version`, and `POST .../activate|switch|rollback|retire|prune`. Those routes (`DbDataKit.Runner.Api/RunnerPublishApi.cs`) hand every call to the shared library `DefinitionsApi` (`RunnerPublishing.Api`), and what is the runner's own sits in the hook of its kind (below); the runner keeps its own audit verbs (`runner.package.*`) and answer shapes (`already_stored`, `tasks`, `unbound`, a `202` while an activation waits for runs; client `DbDataKit.Runner.Client/RunnerPublishClient.cs`, which also promotes a version from one channel to the next).
- **Followers.** `DefinitionFollower` hands each subscriber (`IDefinitionSubscriber`) what its kind serves and then polls the events; a subscriber builds the new version before replacing the old one, and if it cannot, the old one keeps serving and the failure is reported. The runner's `StagingFollower` prepares a staged version (materialises files into the disk cache by hash, checks hashes) and reports to the instance status.
- **Activation and running work** (`ActivationHolds`): a run in flight finishes on the version it started with; activation of a runner kind holds the definition until its runs in flight end, then migrates the task's progress (plan 29 section 4.3).
- **Signatures.** A publisher signs the message of a version (the manifest and the path and SHA-256 of each file) with an Ed25519 key (`DefinitionSigning`, `ed25519:<key id>:<signature>`). The shared API verifies it against the group's trust store (`DefinitionTrustStore`: the keys each kind trusts; add, revoke; rotation is a new key and a revocation) and stores the signature and signer with the version. Signatures are required for every kind unless an administrator sets the kind optional (`PUT /api/v1/settings/signatures/{kind}`) or marks the group `test` (`PUT /api/v1/settings/group`); an invalid signature is refused wherever they are optional. The runner uses the same trust store (`RunnerPublishing.Trust`, `GET /api/v1/trust/integration`, `PUT .../keys`, `POST .../{keyId}/revoke`) and keeps its own switch: `require_signatures` (off by default) says whether an unsigned version is refused; a signed one is always verified. **Instances verify at preparation** (`VersionSignatureGate`): a version that carries a signature is verified again over the manifest and files as stored, against the trust store (an untrusted key or a signature that does not hold refuses the version; a key revoked since does not unseat what it signed), and an unsigned version is refused only where the host says it needs one: the Gateway for a kind an administrator set to `required` (`SignaturePolicy.RequiredByAdministrator`, not the default for a kind nobody set, because the per-kind routes still publish unsigned versions), the runner when `require_signatures` is on. A refused version is not served; the one served before keeps serving and the host reports why (`DefinitionFollower` `gate`, `GatewayVersionGate`; `StagingOptions.Gate`, `RunnerVersionGate`: the version fails its staging on that instance). The screens: the Gateway's Publishers page (`/trust`, `TrustPage.tsx`) and the runner's Publishing card (`PublishingSettings.tsx`) draw the shared `TrustStore` (`@dbdatakit/shell`): keys per kind, add (a public key is pasted), revoke after asking, the requirement per kind and the group's `test` mode. `GET /api/v1/trust` answers every kind the group takes, each as `GET /api/v1/trust/{kind}` answers it (a viewer or an administrator; a publisher is refused); the console reads it to add its own publisher key to each kind of every group it manages ([install.md](install.md#the-consoles-publisher-key)).

**Promotion between groups** (plan 30 PB06). `DefinitionPromoter` (`DbDataKit.State`) moves a version from one group to another over their shared APIs, each reached through an `IDefinitionsLink` (a request relative to `/api/v1/`): it reads `GET definitions/{kind}/{name}/versions/{n}` (the manifest, format, `depends_on`, files by hash and the signature) from the source, asks the target `HEAD artifacts/{sha}` for each file, reads only the missing ones (`GET artifacts/{sha}`) and sends them (`PUT`), then publishes the same manifest in the channel asked (`mode` the kind's default unless `serve` or `stage` is asked) with the signature, which the target verifies against its own trust store. The target's checks decide: a slot its channel does not bind and a dependency that is not active there refuse the move naming it (nothing is stored; the files stay and are not sent again). `PromoteRequest.Bindings` writes the target channel's own bindings first (slot to the name of what it is bound to there). The groups number their versions on their own; the same content again changes nothing and sends nothing. The source is read as a viewer or an administrator (a publisher may not read a runner), the target is written as a publisher or an administrator. `RunnerPublishClient.PromoteToGroupAsync` and `dbdatarunner package promote` are the client and the command for two paired runners (`PromoteAsync` moves a version between channels of one group); tests: `PromotionTests` (two in-process groups) and `PublishPromotionTests` (two runner servers). **The console's action:** `POST /api/promotions` of the console (`ConsolePromotions`) runs the promoter as the signed-in person over two links: a registered runner through `ConsoleRelay.SendAsAsync` (the relay's table and roles apply, the answer stays a stream); the Packages screen of a runner shown by a console has the **Promote** action. Tests: `ConsolePromotionTests`, `EmbeddedRunnerTests`. **Releases of Gateway definitions** (plan 21 CH02): a release item of product `gateway` promotes a Gateway's `semantic-model`, `data-source`, `entity-model`, `dashboard`, `report`, `report-catalog`, `app` or `app-nav` the same way, between two registered Gateways, as the person who starts the release; the relay passes a Gateway's publish routes with the role each needs (`ServerProducts.GatewayRoutes`, the Gateway's own roles: a viewer reads, an administrator or a publisher writes) and the approval permission of the item is the administrator role on the target Gateway.

**Kind hooks.** What one kind adds to the shared API is an `IDefinitionKindHook` (`DbDataKit.State.Api/DefinitionKindHook.cs`; every member has a default that adds nothing, so a kind without one is published as before): the channel a host serves (`CheckChannel`), checks before a version is stored (`BeforePublish`: names, the trigger loop) and what follows (`AfterPublish`: the task projection, extra answer fields), the life-cycle calls a host does itself (`ActAsync`: the runner's progress migration, the runs in flight, the holds of an activation, the sandbox run), the checks around a binding (`BeforeBind`/`AfterBind`: the posture of a binding that shares production's connection, which answers its own body; `BeforeUnbind`/`AfterUnbind`), its own list and description, whether a publisher may read, the status a refused registry rule answers and whether signatures are required (the runner's `IntegrationKindHook`). The Gateway has hooks too (`GatewayDefinitionsManagement.HooksOf`; `SemanticModelKindHook`, `DataSourceKindHook` and `EntityModelKindHook`, each a thin adapter over the checks of the per-kind route in `GatewaySemanticModels`, `GatewayDataSources` and `GatewayEntityModels`): a check that waits (`BeforePublishAsync`, the data source's test query and the table and column reads of a model), the request body (`PublishHookContext.Body`, where `adopt` is), the owner a definition names in its content when the group's settings hold none (`OwnerOf`: the stamp `managed_by` of the per-kind routes), a read refused by the owner rules (`BeforeRead`), the binding of a model's slots (`BeforeBind`), the data source's adopt call (`ActAsync`, `POST definitions/data-source/{name}/adopt`: an administrator hands a data source made by hand to a paired client, the route's `POST /api/v1/data-sources/{name}/adopt` as it is), what the channel binds a slot to (`PublishHookContext.BoundTo`), the serving at once on the instance that took the publish and the audit entries of the per-kind route (`semantic-model publish`, `data-source write`, `data-source adopt`). The caller's `Context` is the management caller. The manifest of a publishing client carries its own stamp (it is signed, so the Gateway checks the line and does not add it); a model is published under its lower-case name; the format is read from the content when the body gives none (`DefinitionKind.FormatOf`). While the per-kind routes publish unsigned versions, these kinds ask for a signature only where an administrator set the kind to `required` (the instances' rule), not by the group's default. A hook says no by throwing `DefinitionsRefusalException`; the API audits it as a refused call. A host passes the hooks and its own audit callback to `DefinitionsApi`; the caller's `Context` carries what the host knows of it (the runner's actor).

**The client.** `DefinitionsClient` (`DbDataKit.State.Api`) is the caller's side over an `IDefinitionsLink`: `HEAD` and `PUT artifacts/{sha256}` for the files the group lacks, `PUT definitions/{kind}/{name}` (the manifest, channel, mode, `expected_version`, `depends_on`, format, extra body fields), `PUT bindings/...`, the reads (`NamesAsync`: a caller that only publishes lists the names it owns and no others, an administrator or viewer all of them), the adopt call (`AdoptAsync`), and the signature: it asks `GET trust/{kind}` and signs when the group trusts its key, goes unsigned when the group does not require signatures, and refuses before sending, naming the key and the administrator's call, when the group requires them and does not trust it. Build's `ProxyPublisher` publishes its data sources and models with it, and so does the Workspace server (`WorkspaceGatewayPublish`, `DbDataKit.Authoring.Server`, plan 33 SW01) for a Store project's entity models and a Build project's data source documents and semantic models: it names the data sources a model needs as slots when the publishing client did not make them, binds them itself where the Gateway lets a publishing client (its own data sources), and says what an administrator binds otherwise. The Workspace's app publish (and the apps API's `PUT /api/v1/apps/{name}`, which the removed `apps publish --to` called) is not on the shared route yet: an app version is one JSON text with every file in base64 until PB04 stores it as a manifest and files by hash, and moving it before then would be a partial route to migrate.

## Deploying a build from the control service

The control service deploys a build of a Workspace project to a Runner or a Gateway as the person who asks. The Workspace service builds the project
and serves what the build holds; it never calls the Runner or the Gateway.

1. The person asks the console: `POST /api/deploys` with the Workspace server, the project, the workspace, the build and the target, and what the
   kind's deploy takes (for a Runner the `channel`, `serve`, `expectedVersion`, `bind`; for a Gateway the `dataSource` and `adopt`; the `model` of a
   Store build; for an app, the `previewInstance` it previews; a `note`).
2. The console asks the Workspace server for the project as the person (`GET /api/v1/projects/{project}`). The Workspace server answers a person who
   may read the project: one with the viewer role, or a role above it, on the project or on the whole server. Otherwise the deploy is refused in the
   Workspace server's words: "Workspace does not let you read the project 'orders': This needs the viewer role." The console also checks that the
   target is a registered Runner or Gateway. The console asks for no other role. The target decides what the person may do there (step 6).
3. The console reads `GET /api/v1/projects/{project}/workspaces/{workspace}/builds/{build}/contents` from the Workspace server, as the person. The
   answer has the build record and the items the build holds to send. A build that did not succeed is refused: "This build is failed, so there is
   nothing to publish. Only a build that succeeded is published."
4. The console picks the item that the kind's route to the target's product takes (`GET /api/v1/project-kinds`). A kind that does not go to that
   product is refused with the kind, the target and the products the kind goes to.
5. The console reads each file of the item by its SHA-256 (`GET .../builds/{build}/files/{sha256}`), lays the files out under their paths, and refuses a
   file whose bytes do not have the hash or the size the build listed.
6. The console sends the item to the target over the target's publish API, through the relay as the person. The target decides what the person may
   do there: a Runner or a Gateway takes a version from a person who holds its publisher or administrator role, and records them as the publisher.
   Each version is signed with the console's publisher key over its manifest, its files and the build record, when the target trusts that key
   (`GET /api/v1/trust/{kind}`). A target that requires signatures and does not trust the key refuses the deploy before anything is sent, and the
   message names the key.
7. The console keeps the deploy (the `deploys` state module) and audits it as `build.deploy`. `GET /api/deploys?workspace_server=&project=&workspace=&build=`
   lists the deploys of a Workspace server's builds, newest first. A refused deploy is kept too, with the target's words. Anyone signed in to the
   console can read this list, for every project: each deploy shows its build record (the commit, the workspace, what the validators found), the
   Runner, the channel and the version. The calls on one deploy are checked against the project ([Runs of a deployed build](#runs-of-a-deployed-build)).

What a build holds to send, by the routes of its kind:

| Item | Product | Files in the build's folder |
| --- | --- | --- |
| `package` | Runner | `artifact/package.yaml` and `artifact/files/` (a Sync set, a Build project's refresh plans, an integration) |
| `semantic-model` | Gateway | `artifact/gateway/` (a Build project's semantic models and views) |
| `model` | Gateway | `artifact/models/` (a Store project's models) |
| `model-package/<model>` | Runner | `artifact/runner/<model>/` (one deploy package per Store model; the person names the model when the build has several) |
| `app` | Gateway | `app/` (a built app) |

Each item lists its files with their SHA-256 and size, and `manifest_sha256`: the SHA-256 of the item's list of files (each path under the item's
folder, a tab, the file's SHA-256 and a line feed, in path order). Two builds of the same content have the same `manifest_sha256`.

Example: Ana stages build `4f1c...` of the Sync project `orders` on the Runner `jobs` in `dev`, with the slot `source` bound to `erp`:

```
POST /api/deploys
{"workspaceServer": "Workspace", "project": "orders", "workspace": "draft-1", "build": "4f1c...", "target": "jobs",
 "channel": "dev", "bind": {"source": "erp"}}
```

The answer has the steps, what the Runner answered (`package`, `channel`, `mode` `stage`, `version`, `signature` `verified`) and the deploy record.
An app is sent over the Gateway's app calls, which do not take a signature or a build record yet; the build record is kept by the console only.

An app build can also go to a Gateway as the preview of one of its instances. The body names the instance as `previewInstance`. The console sends the
build to the Gateway's `preview` channel (`PUT /api/v1/apps/{name}/channels/preview`) and points that instance's preview at it
(`PUT /api/v1/app-instances/{instance}/channels/preview`), both as the person. The Gateway refuses a preview of an instance that production does not
have: "There is no instance 'ghost' in production to preview." A build of any other kind is refused: "Only an app has a preview with data." The deploy
record keeps the preview with the channel `preview`, the mode `preview` and `preview` (the instance, and the preview's address and path on the
Gateway's apps host).

Example: Ana sends build `7d20...` of the app `sales` as the preview of the instance `sales` on the Gateway `edge`:

```
POST /api/deploys
{"workspaceServer": "Workspace", "project": "sales", "workspace": "draft-3", "build": "7d20...", "target": "edge", "previewInstance": "sales"}
```

The answer's `deployed` has `channel` `preview`, the app's `version` in that channel and `preview_address`, `https://apps.example.com/apps/sales~preview/`.
People who may open the instance `sales` open its preview there, signed in as themselves ([apps.md](apps.md#building-previewing-and-publishing-in-the-workspace-server)).

Example: Ben may read the project `orders` on the Workspace server and nothing else there. On the Runner `jobs` he holds the publisher and operator
roles. He serves a build of `orders` in `dev`, and the Runner records him as the publisher. On the Runs tab he runs the task `load` of the version
`dev` serves. **Test** of a connection's login is refused, in the Runner's words, because it needs the administrator role on `jobs`.

### Preflight before activation

A package can declare a read-only manual task named `preflight`. It reads the databases the package's slots bind and records what would stand in the way
of the version, as findings. Each finding has a severity (`error`, `warning` or `info`), words, and what it is about (a connection, a schema). Each part
of the task records the step `preflight-findings` with its findings.

What each kind's preflight reads:

| Package | What `preflight` does | An error |
| --- | --- | --- |
| A Build project's refresh package | `connection status` of each connection the plans use (the input `connections` lists them by default) | An object of the project is not on the connection, the deployed structure is behind the project, a deploy step is blocked, or the status could not be read. An object changed outside dbdatabuild is a warning. |
| A Store model's package | The `deploy` task's dry run, then the `contract` task's plan | The deploy needs a change it does not make (a column removed or retyped). What the contract would drop is information, and a blocked contract a warning, because the contract is approved on its own. |
| A Sync set | Each mapping of each enabled replication, read with the run login of its slots: the source and target tables and columns, the filter (a read that returns no row), the logins' privileges, the change capture the reader needs, types, keys and the state table on the target ([A sync project](authoring-metadata.md#a-sync-project)) | A table or column the mapping names is missing, the filter does not compile, a login may not read the source or make a change on the target that the mapping's writers make, the reader's change capture is off, or a reader or writer is not one the driver has. A type that may not fit and a key that does not agree are warnings. |

When the control service deploys a Runner package to the channel `prod` with `serve`, and the package declares `preflight`:

1. The console stages the version with a hold. The Runner prepares it as any staged version, and does not activate it by itself.
2. The console runs `preflight` on the staged version as the person, and waits for it (two minutes at most).
3. What the preflight found decides what happens next:
   - No error: the console activates the version. The Runner records the activation with the person and the preflight run.
   - An error, or a run that did not succeed: the version stays staged and held. The answer is 409, with the findings and the run's address.
   - The run has not ended: the answer is 202. The person activates the version once the run has ended.
4. The person activates the version with `POST /api/deploys/{id}/activate`. When the preflight found an error, the call needs `"confirm": true`.
   The Runner keeps the person's name and the errors they confirmed in its record of the activation, and the console audits the confirmation as
   `build.preflight.confirm`.

An activation is the Runner's administrator call. A person without the administrator role on the Runner is refused in the Runner's words, and the
version stays staged until an administrator activates it.

A package that declares no `preflight` is served at once, as before. The answer's steps say that nothing was checked. A deploy to any other channel,
or one that stages, runs no preflight.

Example: Ana serves build `9c2e...` of the Build project `sales` on the Runner `jobs` in `prod`. The preflight finds that `marts.dim_region` is missing
on the connection `warehouse`:

```
POST /api/deploys
{"workspaceServer": "Workspace", "project": "sales", "workspace": "draft-2", "build": "9c2e...", "target": "jobs", "channel": "prod", "serve": true}

409 {"error": "The preflight of version 4 found 1 error, so it was not activated in prod: warehouse: marts.dim_region is not on the connection ...",
     "deployed": {"mode": "stage", "version": 4, "preflight": {"run": "0193...", "link": "/servers/jobs/runs?run=0193...", "errors": 1, "warnings": 0,
                  "findings": [...], "activation": "refused"}},
     "deploy": {"id": "01a9...", ...}}
```

Ana reads the run, deploys the structure, and runs **Re-check**. Or she decides the error does not matter for this version and confirms it:

```
POST /api/deploys/01a9.../activate
{"confirm": true}
```

On the Builds tab the publish panel shows the preflight: the findings (errors first), the run's link, and **Activate** (once the run has ended) or
**Activate anyway** (after an error). On the Runs tab, **Re-check** runs `preflight` of the version the channel serves, through
`POST /api/deploys/{id}/runs`, and shows the findings.

A version staged with a hold says so on the Runner. The package's `channels` list shows `held` with the reason and who asked. An administrator can
activate it on the Runner too, and the activation ends the hold. A later stage of the package in that channel replaces the hold.

### Publishing from the Builds tab

On the console's screens of a Workspace server, the Builds tab of a draft makes this deploy. Ana opens build `4f1c...` of `orders`, and the publish
panel lists the Runners and Gateways the console manages:

- The project's kind goes to the server's product: the server is offered. A Sync project goes to a Runner, so `jobs` is offered and a Gateway is not.
- The server does not answer the console: it is offered with "not answering", and **Publish** stays off until it answers.
- The console manages no server the kind goes to: the panel says so, and an administrator adds one under **Servers**.

For a Runner, Ana names the channel (`dev`), the slots to bind (`source=erp`) and whether the version is served at once; for a Gateway of models,
the data source. **Publish** sends `POST /api/deploys` as Ana. The panel shows the version the target answered and lists the console's records of
the build's deploys (`GET /api/deploys`), taken or refused.

A build's panel has its own address: `/servers/<Workspace server>/<project>/<workspace>/files?tab=builds&build=<build>` opens the Builds tab with
that build and its publish panel.

A Sync project's Designer links here: **Build and publish…** in the rail of a replication's page opens the Builds tab of the same draft, and the browser's
Back returns to the replication (`DbDataWorkspace/README.md`, "A project's two areas").

### Runs of a deployed build

A version the console deployed to a Runner is run through the console too. The console's deploy record names the Runner, the package, the channel
and the version, so the Workspace server is never asked where a build went and never calls the Runner. These tabs of a draft run it:

- The **Provisioning** tab of a Sync project: **Apply on the Runner** runs the set's task `provision` of the deployed version.
- The **Runs** tab of a Sync or Build project: **Run now** runs a task of the version the channel serves, and **Test** tries the login bound to a
  connection's slot.
- The Sync Designer: **Preview rows** of a mapping and **Try** of a segmenting strategy run the set's tasks `preview` and `try-strategy` and show
  their answer.
- The **Deploy** tab of a Store project: **Dry run** builds the draft, has the console stage the model's package (`POST /api/deploys` with the
  `model` and the slot `storage` bound), then runs its task `deploy`; **Deploy** and the contract step run on that same deploy.

Each tab takes the newest deploy of a build of the draft that the Runner took (`GET /api/deploys?workspace_server=&project=&workspace=`). A refused
deploy and a deploy to a Gateway are skipped. When no deploy put a version on a Runner, the tab says to publish the build first.

The calls name the deploy by its id. For each call the console first asks the Workspace server for the deploy's project as the person, as a deploy
does (step 2): a person who may not read the project is refused in the Workspace server's words. The console then calls the Runner as the signed-in
person, and the Runner checks that person's roles there:

| Call | Role on the Workspace server | Role on the Runner | What it does |
| --- | --- | --- | --- |
| `POST /api/deploys/{id}/runs` | viewer of the project | operator | Starts `task` with `inputs` (names and text values). Without `served`, it runs the version the deploy put on the Runner, and only a task with no schedule and no trigger other than `manual: true` runs that way. With `"served": true`, it runs the version the channel serves now. With `"answer": true`, it waits up to 10 seconds for the run's answer and passes it on ([Rows and trials are answered, never kept](authoring-metadata.md#rows-and-trials-are-answered-never-kept)). |
| `GET /api/deploys/{id}/runs/{run}/answer` | viewer of the project | operator | Takes the answer of a run the person started, once: `done` with the answer, `running`, `failed` with the reason, or `gone`. The console keeps nothing of it. |
| `GET /api/deploys/{id}/runs/{run}?step=<name>` | viewer of the project | viewer | Reads the run, with the results of the steps named `step` it recorded. The answer is 404 while the run is queued and not started yet; the tabs read it again until it starts. |
| `GET /api/deploys/{id}/runs?task=<name>&step=<name>` | viewer of the project | viewer | Reads the newest run of `task` in the deploy's channel, of whatever version ran it, with the results of the steps named `step` it recorded; `run` is null when the task never ran there. The Runs tab's newest-check card reads the newest `preflight` this way ([Every kind uses the same pieces](authoring-metadata.md#every-kind-uses-the-same-pieces)). |
| `POST /api/deploys/{id}/binding-test` | viewer of the project | admin | The Runner tries the login bound to `slot` in the deploy's channel, with `role` `default`, `read` or `write`. The answer says how far the login got. It never holds a value. |
| `POST /api/deploys/{id}/contract` | viewer of the project | operator | Runs a Store model's approved contract plan (below). |
| `POST /api/deploys/{id}/activate` | viewer of the project | admin | Activates the version the deploy staged in `prod` for its preflight, once the run has ended; `"confirm": true` when the preflight found an error ([Preflight before activation](#preflight-before-activation)). |

A Store model's contract step removes what the model no longer has. It runs in two steps:

1. **Plan the contract** runs the task `contract` with no approval. The run records the plan, its hash and the columns it drops (the step
   `contract-report`).
2. **Contract** sends `{"planRun": "<that run>", "approve": "<its hash>", "allowConsumer": [...]}`. The console checks that the run read a plan with
   that hash. It then reads the lineage of every Gateway it manages, as the person. A published dashboard, report, app or model member that reads a
   dropped column refuses the step and is named, until it is published without the column or the person allows it as `<name>@<channel>`. A server
   that does not answer and is a Gateway, or whose product the console does not know yet, refuses the step too; `gateway:<name>` allows it. Then
   the console runs `contract` with the hash, and the Runner runs exactly that plan or nothing.

Example: Ana deployed build `4f1c...` of the Sync project `orders` to `jobs` in `dev` (deploy `01a2...`). On the Provisioning tab she applies the
setup of the mapping `orders`:

```
POST /api/deploys/01a2.../runs
{"task": "provision", "inputs": {"replication": "orders", "mapping": "orders", "action": "createTargetTable"}}
```

The answer (202) has the run's id. The tab then reads `GET /api/deploys/01a2.../runs/<run>?step=provision-applied` until the run ends, and shows
each statement the run reports.

The console audits each call under the person, with the deploy and the Runner: `build.run`, `build.binding_test`, `build.contract` and
`build.contract.allow_consumer`, refused calls included. An activation after a preflight is `build.activate`, and a confirmation of its errors
`build.preflight.confirm`.

## The build record

A version deployed from a build carries the record of that build (`build-record/1`), as one line of JSON:

```json
{"schema":"build-record/1","server":"<Workspace server id>","project":"orders","kind":"sync","workspace":"draft-1","build":"4f1c...",
 "commit":"9a2e...","uncommitted_changes":false,"item":"package","manifest_sha256":"c0ff...",
 "checks":{"passed":true,"findings":[{"severity":"warning","code":"...","message":"...","file":"config/task.yaml","line":4}]}}
```

- `checks` is what the kind's validators found when the build ran; `passed` is true only for a build that succeeded.
- The signature covers the record. A signed version with a record is signed over the message `dbdatakit-definition/2`: the kind, the name, the
  manifest's SHA-256, the line `build`, a tab and the record's SHA-256, then each file's path and SHA-256. A version without a record is signed over
  `dbdatakit-definition/1`, as before.
- The Runner or Gateway keeps the record's text with the version, as it was sent (`definition_version.build_record`). `GET definitions/{kind}/{name}`
  lists it with each version, and `GET definitions/{kind}/{name}/versions/{n}` returns it. An instance verifies the signature again over the stored
  record when it prepares the version, and a promotion to another group carries the record with the signature.

## The registry of kinds

`DefinitionKinds.Register(new DefinitionKind(name, targetTypes, defaultMode, requiresApproval) { Formats, FormatOf, slots, validator })`. A publisher built for a target type refuses a kind that does not list it. Registered today:

| Kind | Target | Default mode | Where |
| --- | --- | --- | --- |
| `integration` (runner package, `package/1`) | runner | `stage`, activated when every runner is ready | `DbDataKit.Runner/Hosting/RunnerPackages.cs` |
| `dashboard`, `report` | serving | `serve` | `DbDataKit.Views/Publishing/ViewDefinitions.cs` |
| `semantic-model`, `data-source`, `entity-model` | serving | `serve` | `DbDataGateway.Server/Serving/ServingKinds.cs` |
| `app`, `app-instance`, `app-nav`, `app-approval` | serving | `serve` | `DbDataKit.Apps.Instances/AppDefinitions.cs` |

The Gateway's stores (`PublishedSemanticModels`, `PublishedDataSources`, `PublishedEntityModels`, `AppInstanceStore`) publish through `DefinitionStore.WithRegistry(ServingKinds.All, "serving")`: the kind's validator, its format and the dependencies being active in the channel are checked on every publish. A model names the data sources it needs as slots in its manifest (`slots`, a list of the data sources the version reads: `semantic-model` slots are read slots, `entity-model` slots are write slots; `ServingKinds.SlotsOfModel`), and a version that names one is refused, naming the slot, until the channel binds it (`PUT /api/v1/bindings/{channel}/{kind}/{name}`). **The binding replaces the per-publish `adopt`** (plan 33 SW01): it is the group's recorded consent that this definition uses that data source, a row of the bindings (who bound it and when are kept, and the shared API audits it as `definition.bind`; a publish that goes over a bound slot is in the audit as `data-source use` and in the data source's `adoptions` as kind `... (slot)`). The rules (`ServingSlots`, the hooks' `BeforeBind`): a slot is bound to the data source of the same name (the model serves the data source its source names, so another name would bind to something the model does not read); the data source is one of this Gateway's; a publishing client binds only a data source it made (so its own data sources need its own binding before the first publish, which also makes it the owner of the name); a data source made by hand or by an administrator is bound by an administrator; one another client made is bound by nobody (an administrator hands it over first, `POST definitions/data-source/{name}/adopt`). A slot must be a data source the version uses. A version that names no slot is published as before (an own data source, or `adopt` for one made by hand), so the per-kind routes and Build's publisher are unchanged. The shared HTTP API (`DefinitionsApi`, `DbDataKit.State.Api`) is mounted on the Gateway's management listener for these kinds and for dashboards and reports: artifacts by hash, publish, activate, switch, rollback, retire, prune, bindings and reads under `/api/v1/artifacts`, `/api/v1/definitions/{kind}/{name}` and `/api/v1/bindings`. The per-kind endpoints are still there and write the same rows (they go when Sync is operational on the runner, plan 30 PB02).

## Rules that hold for every kind

- **A channel is part of a version's key** (preview, test, prod). Promotion publishes the same version to the next channel; bindings, not the definition, differ between channels.
- **The publisher is a paired Publisher** with an owner scope (plan 13 W12): it sees and changes only what it published.
- **Versions are kept** until an administrator prunes them, through an API and a screen that show the active, staged and previous versions, each instance's status and what pruning would remove; an artifact goes when no kept version names it. `DefinitionPublisher.Prune` removes the versions an administrator names (or all but the newest N), never one a channel has active, staged or as its previous, nor one another version pins in `depends_on`; a file goes when no kept version names it. It is `POST /api/v1/definitions/integration/{name}/prune` on the runner and `POST /api/v1/definitions/{kind}/{name}/prune` on the Gateway's management listener (administrator; body `keep` or `versions`, and `dry_run`, which is true unless the call says false). The runner's `PackagesPage.tsx` and the Gateway's models screen show the dry run before the button that removes works. The console's versions panel of dashboards and reports (UE04) uses it too; plan 14a SL07 does not yet.
- **Clean break, not silent:** a version in a format the program does not read is refused naming the kind, the name and both numbers.

## How publishing is meant to work everywhere

The parts above are built; these rules also bind what is still open ([planning/PUBLISHING.md](../planning/PUBLISHING.md)).

- **One publish API for every kind and target.** The per-kind routes (`PUT /api/v1/data-sources/{name}`, `/api/v1/semantic-models/{name}`, the apps
  command's own publishing) are removed once every caller is on the shared API, and answer with a sentence naming the shared route. One route means one
  set of checks, signatures and audit.
- **Data-source secrets are slots, bound per channel.** Every secret reference a data source names is a slot; each channel binds it to a secret the
  Gateway holds, and a data source with an unbound slot is blocked in that channel until it is bound. Example: `sales` names `warehouse-read`; in `prod`
  it is bound to the Gateway's secret `warehouse-read`, in `test` to `warehouse-read-test`; until `test` binds it, `sales` does not serve in `test`.
- **Promotion between groups is a deploy of the same version** to the other group: artifacts are sent by hash, bindings are that group's own. There is no
  activation at a set time.
- **Code is published compiled, with its source.** The compiled code runs; recompiling on the instance is an option, off by default. The publisher signs
  the manifest (Ed25519), each group keeps a trust store of publishers per kind and every instance verifies at preparation. **Approvals of trusted code are
  signed too**, with the approver's key from the identity home. Signatures are required by default; an administrator may allow unsigned versions for a
  kind as a recorded choice. Packages are published as the whole `.nupkg` closure by hash, so a runner needs no feed.
- **Apps are published file by file** (a manifest with a list of files; only files the target lacks are sent) and served from a local disk cache filled
  when the instance prepares the version (hash and signature checked), with an in-memory index, compressed text and the hash as `ETag`; serving a file
  never reads the state database.

## Channels

- **Channels live in one install**, not an install per environment. Promotion publishes the same version to the next channel; what changes is what it is
  bound to.
- **Promotion is per product, with dependency checks and releases.** A promotion is refused when something it depends on is not in the target channel at
  a compatible version. A release is a named, ordered list of promotions: it runs in order, stops at the first failure, says where and can be resumed; it is
  a sequence, not a transaction. One approval covers a release, by an approver who may approve every item in it.
- **Reads are inherited, writes never.** A channel uses production's binding of a read slot unless it overrides it; a write binding in a non-production
  channel is an explicit override that points elsewhere, and without one the channel cannot write to that connection. Lineage shows a dataset reached
  through an override as its own node, marked with the channel.
- **Expand, then contract.** Channels share databases, so a change to a shared structure goes in three steps: add, move the consumers, remove later. Where
  the suite sees the consumers it refuses the contract step, naming the package and the channel (a Build plan that drops a column a published package in
  any channel still reads).
- **Store writes in a preview or test channel go to a draft layer** only that channel or preview sees; reads show the draft on top of the real data.
  Previews are read-only unless the author turns writes on. Drafts are never promoted: "submit as change requests" turns draft rows into ordinary change
  requests in production. Drafts are visible to everything the serving layer answers in that channel (apps, dashboards, semantic queries, agents) and not to
  Build unless a Build run into an overridden target opts in. A preview lives with its workspace, is removed with its draft when the branch is merged or
  abandoned, and expires after 30 idle days (configurable) with a notice first.
- **Build's test target is made by an explicit command**, never automatically (`channel target create dw --channel test` makes `dw_test` and records the
  override as a binding of the write slot); it is filled by deferring to production: only the models changed in the version and their dependents are
  built, the rest is read from production's target.
- **Sync runs in production only.** An additive change goes to production by promotion; a risky one gets a trial run: a one-off, row-limited run into an
  overridden target in a test channel, never a continuous replication per channel.
