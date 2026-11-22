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

- **Building a package**: each kind's package output, the manifest and the files by SHA-256. This stays on the Workspace service.
- **Checks and validation before a publish**: a kind's validators, the checks of slots and bindings, data source adoption, the format and version
  checks, and `preflight`. These run where the deploy is decided, with the same results.
- **The shared publish API's client and signing**: `DefinitionsClient`, `PublishArtifacts` and `DefinitionSigning`.
- **The calls to a Runner or Gateway**: run a staged or served task, a run's result, a binding test, lineage, and the catalog read.
- **Target pinning** by certificate fingerprint.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| WORKSPACEISOLATION-1 | The list: every class and route of the Workspace's publish, run, check and catalog paths, each marked stays on the Workspace (building, checking a draft), moves to a shared library for the control service, or is removed (only what calls another service or holds a publisher key); it also says where the sync draft's `script-test` goes, since it runs the author's code | none | S | the list is in this file and the owner has read it |
| WORKSPACEISOLATION-2 | The code the list marks for moving goes to shared libraries, with an adapter in the Workspace so its tests pass unchanged | WORKSPACEISOLATION-1 | M | the moved code's tests run in `Shared/tests`; the Workspace's tests pass unchanged |
| WORKSPACEISOLATION-3 | The Workspace service serves a build's package (manifest and files by hash) and its build record (the manifest hash and the commit) to the control service. The control service runs the checks and deploys to a Runner or Gateway as the person, signing with its own key a message that holds the build record; for trusted code, the approver's signature is added once PUBLISHING-3 is built. The Workspace service holds no signing key | WORKSPACEISOLATION-2 | L | a test publishes a build, a sync and a store project and an app through the control service; the checks refuse a bad package with the same message as today; the target records the person, the signature and the build record; the Workspace service made no outbound call |
| WORKSPACEISOLATION-4 | The publish panel on the Workspace screens calls the control service's deploy (the Designer's **Build and publish…** opens this panel, SYNCAUTHORING-17) | WORKSPACEISOLATION-3 | M | Playwright: publish from the panel; the target shows the version |
| WORKSPACEISOLATION-5 | Runs, run results, binding tests and lineage reads go through the control service | WORKSPACEISOLATION-2 | M | the Runs tab's tests pass with the Workspace service unable to reach the Runner |
| WORKSPACEISOLATION-6 | Catalog refresh goes through the control service: the Gateway refresh (`WorkspaceCatalogRefresh`) moves there, beside the Runner read AUTHORING-21 builds on the console (AUTHORING-P4 asks which comes first) | WORKSPACEISOLATION-2 | S | the catalog refresh test passes with the Workspace service unable to reach a Gateway or Runner |
| WORKSPACEISOLATION-7 | Development logins (AUTHORING-19): the screen sends the login through the relay to the Runner; the Workspace service only learns which binding exists | none | M | a test sets up a login for `dev`; the value is in no Workspace log, state or audit, and the Runner holds it |
| WORKSPACEISOLATION-8 | Remove what the list marks for removal: the Workspace's publisher keys, its pairing with targets, its clients of the Runner and Gateway, and the Publisher keys screen | WORKSPACEISOLATION-4, WORKSPACEISOLATION-5, WORKSPACEISOLATION-6, WORKSPACEISOLATION-7 | M | no Workspace code opens a connection to a Runner or Gateway; the removed routes answer with the sentence naming the control service |
| WORKSPACEISOLATION-9 | The Workspace service's outbound list, through `DbDataKit.Outbound` (`OutboundGate`, the `outbound:` setting, `OutboundDestination.FromGitRemote`; built): package sources and the projects' git remotes; any other address is refused and audited (the Workspace gives the library an `IOutboundAudit` that writes to its audit). The rules the operating system applies come with TRUSTZONES' open design "Outbound rules applied by the operating system" | WORKSPACEISOLATION-8 | S | a test: a call to a Runner's address is refused and audited; a NuGet restore and a git fetch work |
| WORKSPACEISOLATION-10 | Docs: the Planned entries in `docs/architecture.md` become descriptions of what is built; `docs/apps.md`, `docs/authoring-parity.md`, `docs/authoring-metadata.md` and the Workspace README follow | WORKSPACEISOLATION-8, WORKSPACEISOLATION-9 | S | the owner approves the text |

## Needs from other subjects

- PUBLISHING-3: the approver's signature for trusted code, added to what the control service signs.
- COMMANDLINES-2, COMMANDLINES-3, COMMANDLINES-8: the Workspace stops starting `dbdatabuild`, `dbdatasync` and `dbdatarunner`.
- AUTHORING-21: the console's catalog read from a Runner channel; WORKSPACEISOLATION-6 puts the Gateway refresh on the same route (AUTHORING-P4 asks
  which is built first).

## Shared areas touched

`DbDataKit.Authoring.Server`, the control service's host, the Workspace screens (`Shared/web/workspace`), `DbDataKit.State.Api` (the publish client
and signing), `docs/ui-editors.md` (the Publisher keys screen goes).
