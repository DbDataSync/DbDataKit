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
| PUBLISHING-6 | A rules table no published Build model produces is refused: a publish of a semantic model or an entity model whose access rules name a rules table that the group's `build-metadata` definitions do not name for that data source is refused naming the rule, the table and what to publish (today it is accepted with a warning, because no `build-metadata` is published); the Store's publish reads its rules table the same way | KNOWLEDGE-4 | S | with a `build-metadata` definition published for the data source, a model whose rules table it does not name is refused naming rule and table; with none published the warning stays; a Store model's rules table is checked the same way |
| PUBLISHING-7 | The person who staged a version held for its preflight activates it: with no error found, the Runner lets that person activate it with the role that staged it (publisher); after an error, the same person confirms the errors, recorded with their name as today. `docs/publishing.md` ("Preflight before activation" and the route table) follows | none | S | a test: Bob, a publisher on the Runner, serves a package in a production channel; its preflight finds nothing and Bob activates it; a second version's preflight finds an error and Bob's activation with `"confirm": true` is recorded with his name |

## Needs from other subjects

- KNOWLEDGE-4: Build publishes `build-metadata`, before PUBLISHING-6 can refuse an unknown rules table.
- The console registers its own publisher key in each group it manages (`ConsoleTrust`, `docs/install.md`, "The console's publisher key"); the Publishers screens keep showing the keys.

## Proposed decisions

None.

## Open designs

Not scheduled. Each becomes tasks only when the owner schedules it.

### Comparing a reproducible build from the console

**What.** An action on a published version (in the console's screens of a Runner or a Gateway) that rebuilds the version from its published source and
compares the result with the published compiled code, file by file by hash, and says whether they are the same. **Why.** Code is published compiled,
with its source, and the compiled code runs; a comparison lets an approver check that the code that runs is what the source says, without trusting the
publisher's machine. It is offered, not required: approval does not depend on it. **Open questions:** where the rebuild runs (a Runner task, so the
console builds nothing), which kinds can be rebuilt deterministically (C# projects need deterministic compilation and pinned packages; `.csx` scripts are
compiled on the instance already), and what a difference shows to the person.

### An approval chain across groups

**What.** Today one approval covers a release, by an approver who may approve every item in it. A chain would ask for approvals in order across several
groups (for example the data's owners, then security, then the administrators) before a version or a release moves, each recorded. **Why.** An
organisation whose rules need more than one approver per change, from different groups, cannot express that with one approval. **Open questions:** how
a chain is declared (per kind, per channel, per release), what happens to the earlier approvals when a later approver rejects, and how it meets the
approver's signature (PUBLISHING-3) and the Store's own change requests.

## Shared areas touched

- `DbDataKit.Publishing*` (the shared API, registry, client), `DbDataKit.Apps.Instances`, `DbDataGateway.Server` management routes, `DbDataStore` command line.
- `docs/formats.json` (app version format), `docs/ui-editors.md`, `docs/publishing.md`.
