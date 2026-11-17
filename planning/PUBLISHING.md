# PUBLISHING: one way to publish

## Goal

Every kind is published through the one shared API (`docs/publishing.md`): artifacts by hash, stage then activate, bindings per channel, signatures,
prune. The per-kind routes are gone, apps are published file by file and served from a disk cache, trusted code needs the approver's signature, and a
definition that reads a blocked dataset is refused.

## What exists today

- The shared API, its registry and client (`DefinitionsClient`); the Runner and the Gateway mount it; Build's data sources and models and the
  Workspace's models and data-source documents publish through it; data-source secrets are slots bound per channel.
- Signatures, trust stores and the Publishers screens; instances verify at preparation; no second signature by an approver.
- Prune: built for runner versions and the serving kinds through the management API, with screens for runner versions and semantic models; none for
  entity models and apps, and the semantic models' prune is not called from the console (old 14a SL07).
- Callers of the per-kind routes: the apps command (`dbdatagateway apps publish`, one JSON text with every file in base64, up to 16 MB,
  `AppPackage.cs`), `dbdatastore model publish --proxy` and `model unpublish` (`StoreAdminCommand.cs`), the Workspace's dashboard and report `PUT`, the
  Gateway's own screens, the console's copy of a data source, the views command.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| PUBLISHING-1 | Apps by file and from disk: an app version is a manifest plus artifacts by hash; `apps publish` sends only missing files over the shared API; an instance fills a disk cache at preparation and serves from it (hash as `ETag`, compressed text) | none | L | a second publish sends only changed files; an instance serves a file with the state database stopped; a file whose hash does not match is refused |
| PUBLISHING-2 | Every caller on the shared API, then the per-kind routes removed with the sentence: the apps command (with PUBLISHING-1), `dbdatastore model publish\|unpublish --proxy` and its `gateway add`, the Workspace's dashboard and report `PUT`, the Gateway's own screens, the console's copy of a data source, the views command. Voids the old per-kind route rows of `docs/ui-editors.md` (updated in the same change) | PUBLISHING-1 | M | each old route answers the sentence naming the shared route; each caller's tests pass on the shared route |
| PUBLISHING-3 | The approver's second signature for trusted code, with the approver's key from the identity home; required by default, an administrator may allow unsigned for a kind as a recorded choice | none | M | a trusted version without the approver's signature is refused at preparation naming it; with it, it activates |
| PUBLISHING-4 | Prune screens for entity models and apps, and the console's call of a semantic model's prune | none | S | vitest and Playwright: publish three versions, prune to one; the served version and a rollback target stay |
| PUBLISHING-5 | `blocked_by_check`: a publish or promotion of a definition that reads a dataset a check blocks is refused, naming the check and the dataset | none | M | the publish API refuses a model reading a blocked dataset with the check's name; clearing the block lets it through |

## Needs from other subjects

- INSTALLER-16: publisher keys registered by the console (the Publishers screens keep showing them).

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Publishing*` (the shared API, registry, client), `DbDataKit.Apps.Instances`, `DbDataGateway.Server` management routes, `DbDataStore` command line.
- `docs/formats.json` (app version format), `docs/ui-editors.md`, `docs/publishing.md`.
