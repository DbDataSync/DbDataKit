# Plan 08: Install and Update System (`DbData.Update`)

Status: proposed. Depends on: Plan 03 (service control, directories, `/version`, drain), Plan 01 (config migration), Plan 02 (signing keys are not secrets but must be handled carefully).

## 1. Problem

| | Sync | Proxy | Build |
|---|---|---|---|
| Install | `dotnet tool install -g`, machine-wide tool, Windows service (`service install`), systemd, container | `scripts/install.ps1` (download zip + `SHA256SUMS`, copy over `%ProgramFiles%\DbDataProxy`, optionally install service), Docker (untested) | GitHub Release archives (win-x64 zip, linux-x64 tar.gz), Scoop manifest (in-repo bucket), `.mcpb` bundle |
| Update | `DbDataSync.Updates`: opt-in (`Updates:Mode` default `Disabled`), NuGet + GitHub snapshot prereleases, SHA-512 sidecar (same release), work drain, `dotnet tool update`, confirmation after restart | re-run `install.ps1`; preserves `appsettings.json`; **no rollback, no config migration**; failed copy leaves the service stopped | Scoop `update`; native Rust lib fetched by `fetch-native.sh`, checksum from the same release |
| Integrity | SHA-512 same release (integrity not authenticity); NuGet signature for stable | SHA256SUMS same release; `irm` piped to `iex` one-liner as admin | SHA256SUMS; Scoop hash pin; no signing |
| Release pipeline | `release.yml` (Trusted Publishing, no stored NuGet key), snapshot publishing, promote workflow | tag `v*`, zip + SHA256SUMS + install.ps1 | tag `v*`, build matrix, `scoop` job pushes straight to `main` |

The stated insight: Scoop and `dotnet tool` work for pure CLI programs, but poorly for Windows services and system
installs (elevation, stopping/starting a service, binaries in use, ACLs, config/data migration, rollback, no UI of an installed program).
So the system has to treat **per-user CLIs** and **machine services** as different deployment kinds with one shared release format and verification.

## 2. Deployment kinds

| Kind | Examples | Update mechanism | Rollback |
|---|---|---|---|
| A. Per-user CLI | Build CLI; Sync/Proxy CLI use | package manager: Scoop, `dotnet tool update`, winget; or self-replace for archive installs | reinstall previous version (package manager) |
| B. Machine service | Sync service, Proxy service | **staged side-by-side swap by a privileged updater** (this plan) | automatic on failed health check |
| C. Container | Sync, Proxy images | pull new image tag; no in-place update | previous tag |
| D. Native/side assets | Build's polyglot-sql FFI library, DuckDB natives | pinned in the release manifest with expected hashes | with the app version |

## 3. Design

### 3.1 Signed release manifest (the trust root)
Every release publishes `release.json` (and a detached signature `release.json.sig`):
```json
{ "schema":"release/1", "product":"dbdataproxy", "version":"1.4.0", "channel":"stable", "published":"...",
  "minUpdater":"1.0", "requiresConfigMigration":true, "schemaVersions":{"config":3},
  "artifacts":[{"rid":"win-x64","kind":"service-zip","url":"...","sha256":"...","size":1234,"signature":"..."},
               {"rid":"linux-x64","kind":"tarball", ...}, {"kind":"native","name":"polyglot_sql_ffi","rid":"win-x64","sha256":"..."}],
  "supersedes":[], "revoked":false, "notes":"..." }
```
- Signature: ed25519 (minisign-style) with the public key **embedded in the product and installer**, so a compromised release
  or GitHub account cannot swap artifacts and checksums together (closes Build F-2a, Proxy S-13, Sync 9.12). Keys rotate via a manifest of
  keys signed by the previous key; the signing key lives in a protected CI environment/HSM, not in the repo.
- Also publish SBOM and GitHub build provenance attestation; verification of attestations is optional on the client.
- `revoked` and a monotonic `minimumSafeVersion` support emergency withdrawal.

### 3.2 Updater library (`DbData.Update`)
```
IReleaseSource    GitHubReleases | NuGetFlatContainer | Folder/UNC share (air-gapped) | Url
UpdateChecker     latest per channel (stable/beta/snapshot), min-version and revocation checks
Downloader        trusted-prefix only, resumable, size + sha256 verified, writes to staging then verifies signature
Verifier          manifest signature -> artifact hash -> optional Authenticode (Windows) -> zip-slip-safe extraction
IUpdateApplier    DotnetToolApplier | PackageManagerHint (scoop/winget) | ServiceSwapApplier | ContainerNotice | ManualApplier
UpdateMode        Disabled | Notify | Approve | Auto(window) -- Sync's modes generalised, default Disabled/Notify
```
Install-method detection for CLIs (Scoop shim path, dotnet tool store, archive) picks the applier and prints the right command or runs it with
consent. Sync's `ReleaseSources`, `SnapshotStager` (trusted `releases/download/` prefix, `.partial` rename), `UpdateApplier` and `UpdateConfirmationService` are the starting code.

### 3.3 Service update: side-by-side with a privileged helper
Windows layout (Plan 03): `versions\<ver>\` plus a `current` junction; service `ImagePath` points at `current\<product>.exe`.
Flow:
1. Service (unprivileged identity) checks/downloads/verifies into `%ProgramData%\<Product>\updates\staging\<ver>` and extracts to a staged `versions\<ver>.pending` request; it writes `update-plan.json` (from, to, manifest hash, migration steps, health URL, timeout).
2. Service drains (Plan 03 drain hook), then exits or signals the helper.
3. **`dbdata-updater` helper** (small separate executable, installed as a one-shot Scheduled Task running as SYSTEM, or SCM recovery action, triggered via a named event) re-verifies plan, signature and hashes independently, then:
   a. stops the service; b. backs up config and data pointers (`versions\<old>` is retained; copy `config` and run a DB pre-update backup hook if the product registers one);
   c. runs config migrations via Plan 01 (`config migrate --apply`, with `.bak`) and product `IUpgradeStep`s (Sync `State/Migrations`, Build tracking layout upgrade scripts);
   d. repoints `current` to the new version and starts the service;
   e. health check: poll `/version` and `/health` until the new version reports ready or timeout.
4. On failure: stop, repoint `current` to the old version, restore config backup, restart, write `update-result.json` with the reason, raise an Event Log entry. Data-schema migrations that cannot be undone set a "no automatic rollback past this point" flag in the plan, and require a verified DB backup first.
5. Journal: every step is recorded in `update-plan.json` with state so an interrupted update (power loss) resumes or rolls back on the next helper run or service start.
Least privilege: the service never needs write access to `Program Files`; a compromised service can only request installation of a **signed** release
that is **not older** than the installed one (downgrade protection) unless an Administrator passes `--allow-downgrade` interactively. Retention: keep N old versions (default 2).
Linux: same layout with a `current` symlink, helper via a systemd path/oneshot unit or `systemd-run`, `ExecStart` pointing at `current/`.

### 3.4 CLI tools (Build and per-user installs)
- `<tool> update --check` reports channel, version, notes, migration requirements. `<tool> update` delegates to the detected package manager (Scoop `scoop update <tool>`,
  `dotnet tool update -g <id>`) or, for archive installs, downloads and verifies and replaces itself (Windows: rename running exe to `.old`, move new, cleanup on next run).
- **Scoop**: keep the in-repo bucket (hash-pinned `checkver`/`autoupdate`), but generate it from the signed manifest, and open a PR for the bucket bump instead of pushing to `main` (Build F-2c).
- **winget** manifest as a second channel; **MSI/WiX** evaluated below for services.
- Native assets (Build): the product embeds expected SHA-256 per RID (or reads them from the signed manifest) instead of trusting the same release's `SHA256SUMS`.

### 3.5 Installer (`install.ps1` and friends)
Single bootstrap template `DbData.Install` parameterised per product: `-Product -Version -Channel -InstallService -Account -DataDir -ConfigFrom`:
downloads `release.json` + signature, verifies with the embedded public key, verifies artifacts, creates directories/ACLs and the service via Plan 03's
CLI (`<tool> service install`), migrates/preserves config, supports `-WhatIf`, writes a log, and is idempotent (re-run = repair/upgrade). The documented
one-liner remains but pins a version/hash of the bootstrap script itself in docs. Rollback behaviour shared with the updater (failure leaves the previous service running, not stopped).

### 3.6 MSI/WiX as an option for services
| | Script + helper (this plan) | MSI (WiX) |
|---|---|---|
| Upgrade/rollback | custom, fully controlled | built in (Windows Installer transactions) |
| Enterprise deployment (SCCM/Intune, GPO) | scripts work, less standard | native, expected by IT (the user's environment includes SCCM) |
| Config/data migration | custom steps | custom actions (harder to test) |
| In-app auto-update | yes | needs a bootstrapper |
| Effort | medium | high; separate skill set |
Recommendation: build the script + helper path first (it also powers auto-update); add an MSI for Proxy/Sync later that installs the same payload and registers the same layout, driven by the same manifest. Keep one directory layout so the two are interchangeable.

### 3.7 Release pipeline (`reusable-release.yml`)
Build, test, package artifacts, produce `release.json`, **sign in a protected environment**, attach SBOM and provenance, publish to GitHub Releases (and NuGet via
Trusted Publishing for tools), SHA-pinned Actions, least-privilege `permissions:`, no direct push to `main` (Scoop/winget bump via PR), tag name only through `env:` (Proxy `release.yml` interpolation issue).
Snapshot/beta channels get distinct channel names and are never offered to `stable` installs.

## 4. Phases

| Phase | Work | Exit |
|---|---|---|
| 1 | Manifest schema, signing tooling, `reusable-release.yml` producing signed manifests; key management decision | Proxy release produces and verifies a signed manifest |
| 2 | `DbData.Update` core: sources, downloader, verifier (zip-slip tests, tamper tests, revocation, downgrade protection); port Sync `Updates` onto it | Sync stable/snapshot flow unchanged for users |
| 3 | Install-method detection and CLI update command; Build uses it (Scoop/archive); native asset hashes from manifest | Build `update --check` works; fetch-native verifies embedded hashes |
| 4 | Service swap helper (Windows) + side-by-side layout + health/rollback journaling; Proxy first | VM test: v1 to v2 success, v3 failing health rolls back, interrupted update recovers |
| 5 | Sync service adoption incl. DB schema upgrade steps and pre-update backup hook | Update with migration succeeds; no-rollback flag works |
| 6 | New `install.ps1` bootstrap for all products; systemd variant | Repair/upgrade idempotent |
| 7 | MSI evaluation spike; winget manifest | Decision doc |

## 5. Test plan
Integration on Windows runners/VMs (real SCM): install v1, update to v2, fail v3 health, rollback; interrupted update; tampered hash, tampered signature, wrong
key, downgrade attempt, revoked version, zip-slip archive, path traversal in plan file; permission tests (service identity cannot write Program Files); drain with
in-flight work; config migration with backup restore; Scoop/dotnet-tool detection on clean machines.

## 6. Risks and decisions
- **Signing key custody** and rotation process: decide early (a single maintainer today).
- A privileged helper is attack surface; keep it tiny, verify everything itself, never trust the service's claims.
- Auto-update in enterprise networks may be forbidden: ship `Disabled`/`Notify` as defaults, support file-share sources.
- Database migrations make rollback partial; design migrations expand/contract style where possible, require backup otherwise.
- Docker/K8s users must not see in-place update prompts.
- The Proxy worker (x86) ships inside the zip and updates in lockstep with the Proxy.

## 7. Acceptance
A signed manifest is the single trust root; no installer trusts a checksum from the same download origin; a failed update never leaves a service stopped
or half-installed; services update without elevated service identities; CLIs update through their package manager or a verified self-replace; the same
`release.json` drives installer, updater, Scoop and (later) MSI.
