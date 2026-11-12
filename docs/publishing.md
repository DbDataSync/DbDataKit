# Publishing: how a definition reaches the serving layer and the runner

What is built of plan 30 ("one way to publish"), read from the code on 2026-11-09. What is left is [plan 30](plans/30-publishing.md). Channels and promotion rules are [plan 21](plans/21-channels-and-previews.md).

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
- **Activate** makes the staged version active; `ActivateIfAllReady` does it when every live instance reports `ready`. A failed preparation never activates silently; an administrator may activate anyway as a recorded choice. **Switch** points a channel at any stored version (promotion), **rollback** returns to the previous one, **retire** stops serving and keeps the versions.
- **Checks on every move:** declared slots must be bound in the channel (a missing binding refuses the move and names the slot); `depends_on` must be active in the target channel; a kind that is trusted code needs an approved version (`RequiresApproval`); a format that a live instance of the group cannot read is refused naming the instance (`DefinitionFormats`, `DefinitionFormat.Refusal`). `PromoteWithDependencies` moves a version with the dependencies it pins, in one transaction, and the matching rollback restores them (plan 32 DBR10).
- **Artifacts** are content-addressed payloads (`PublishArtifacts`, over `PayloadStore`): a file whose hash is stored is not written again. The runner side has the HTTP form: `HEAD` and `PUT /api/v1/artifacts/{sha256}` (refused when the hash differs), `PUT /api/v1/definitions/integration/{name}` with the manifest, the file list, `depends_on`, channel, `serve` or `stage` and `expected_version`, and `POST .../activate|switch|rollback|retire` (`DbDataKit.Runner.Api/RunnerPublishApi.cs`; client `DbDataKit.Runner.Client/RunnerPublishClient.cs`, which also promotes a version from one channel to the next).
- **Followers.** `DefinitionFollower` hands each subscriber (`IDefinitionSubscriber`) what its kind serves and then polls the events; a subscriber builds the new version before replacing the old one, and if it cannot, the old one keeps serving and the failure is reported. The runner's `StagingFollower` prepares a staged version (materialises files into the disk cache by hash, checks hashes) and reports to the instance status.
- **Activation and running work** (`ActivationHolds`): a run in flight finishes on the version it started with; activation of a runner kind holds the definition until its runs in flight end, then migrates the task's progress (plan 29 section 4.3).
- **Signatures.** A publisher signs the message of a version (the manifest and the path and SHA-256 of each file) with an Ed25519 key (`DefinitionSigning`, `ed25519:<key id>:<signature>`). The shared API verifies it against the group's trust store (`DefinitionTrustStore`: the keys each kind trusts; add, revoke; rotation is a new key and a revocation) and stores the signature and signer with the version. Signatures are required for every kind unless an administrator sets the kind optional (`PUT /api/v1/settings/signatures/{kind}`) or marks the group `test` (`PUT /api/v1/settings/group`); an invalid signature is refused wherever they are optional. The runner still has only the seam (`IVersionSignatureVerifier`, default unsigned) and its own `require_signatures` setting; instances do not yet verify at preparation.

## The registry of kinds

`DefinitionKinds.Register(new DefinitionKind(name, targetTypes, defaultMode, requiresApproval) { Formats, FormatOf, slots, validator })`. A publisher built for a target type refuses a kind that does not list it. Registered today:

| Kind | Target | Default mode | Where |
| --- | --- | --- | --- |
| `integration` (runner package, `package/1`) | runner | `stage`, activated when every runner is ready | `DbDataKit.Runner/Hosting/RunnerPackages.cs` |
| `dashboard`, `report` | serving | `serve` | `DbDataKit.Views/Publishing/ViewDefinitions.cs` |
| `semantic-model`, `data-source`, `entity-model` | serving | `serve` | `DbDataGateway.Server/Serving/ServingKinds.cs` |
| `app`, `app-instance`, `app-nav`, `app-approval` | serving | `serve` | `DbDataKit.Apps.Instances/AppDefinitions.cs` |

The Gateway's stores (`PublishedSemanticModels`, `PublishedDataSources`, `PublishedEntityModels`, `AppInstanceStore`) publish through `DefinitionStore.WithRegistry(ServingKinds.All, "serving")`: the kind's validator, its format and the dependencies being active in the channel are checked on every publish. No serving kind declares a slot yet. The shared HTTP API (`DefinitionsApi`, `DbDataKit.State.Api`) is mounted on the Gateway's management listener for these kinds and for dashboards and reports: artifacts by hash, publish, activate, switch, rollback, retire, prune, bindings and reads under `/api/v1/artifacts`, `/api/v1/definitions/{kind}/{name}` and `/api/v1/bindings`. The per-kind endpoints are still there and write the same rows (they go when Sync is operational on the runner, plan 30 PB02).

## Rules that hold for every kind

- **A channel is part of a version's key** (preview, test, prod). Promotion publishes the same version to the next channel; bindings, not the definition, differ between channels.
- **The publisher is a paired Publisher** with an owner scope (plan 13 W12): it sees and changes only what it published.
- **Versions are kept** until an administrator prunes them (owner, 2026-11-04), through an API and a screen that show the active, staged and previous versions, each instance's status and what pruning would remove; an artifact goes when no kept version names it. `DefinitionPublisher.Prune` removes the versions an administrator names (or all but the newest N), never one a channel has active, staged or as its previous, nor one another version pins in `depends_on`; a file goes when no kept version names it. It is `POST /api/v1/definitions/integration/{name}/prune` on the runner and `POST /api/v1/definitions/{kind}/{name}/prune` on the Gateway's management listener (administrator; body `keep` or `versions`, and `dry_run`, which is true unless the call says false). The runner's `PackagesPage.tsx` and the Gateway's models screen show the dry run before the button that removes works. The console's versions panel of dashboards and reports (UE04) uses it too; plan 14a SL07 does not yet.
- **Clean break, not silent:** a version in a format the program does not read is refused naming the kind, the name and both numbers.
