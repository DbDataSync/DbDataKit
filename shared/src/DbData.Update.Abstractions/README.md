# DbData.Update.Abstractions

Pure contracts and models for the update system (Plan 08). No I/O, no crypto, no product dependencies.

## Public contract
- `ReleaseManifest` / `ReleaseArtifact` - the `release/1` JSON (product, version, channel, artifacts[rid, kind, url, sha256, size], minUpdater, requiresConfigMigration, schemaVersions, supersedes, revoked, minimumSafeVersion, notes). `Parse` validates structure (schema, semver, channel/version agreement, sha256 shape).
- `ReleaseVersion` - semver 2.0 precedence; `Channel` inferred from the pre-release tag (none = Stable, beta/rc/preview = Beta, anything else = Snapshot).
- `Channel`, `UpdateMode {Disabled, Notify, Approve, Auto}`, `UpdateWindow` (wraps midnight, optional days).
- `IReleaseSource`, `ReleaseRef`, `IContentFetcher`, `IUpdateApplier`, `UpdateApplyRequest`, `ApplyResult`, `CommandLine`, `InstallMethod`.

## Decisions
- Trust is the detached signature over the manifest's exact bytes; parsing happens only after verification (see `DbData.Update`).
- A version must belong to the manifest's channel (`1.0.0-beta.1` cannot be on `stable`), so a snapshot cannot be relabelled stable.
- `ReleaseRef.IsUnsignedHint` models sources (NuGet) that cannot carry a signed manifest; hosts may only notify for those.

## Threat notes
Every field is attacker-controlled until `ReleaseVerifier.VerifyManifest` succeeds. Never act on `Manifest` values from an unverified parse.
