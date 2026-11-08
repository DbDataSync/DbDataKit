# Plan 30: one way to publish, to the serving layer and to the runner

Status: **direction decided by the owner (2026-11-04)**: one publish API, artifacts as content-addressed payloads, stage then activate,
bindings per channel, per-instance applied status. High level; not sliced. It generalizes what the serving layer has (plan 20 O03) so
the runner (plan 28) and its publishers use the same thing.

## 1. What exists today

`DefinitionStore` in `DbDataKit.State`, in the serving group's state database:

| Table | Holds |
| --- | --- |
| `DefinitionVersions` | every version of a definition, keyed by kind, name and version: the content as one text column, its SHA-256, what it depends on (`kind:name@version`), who published it, when, a note; never changed or removed |
| `DefinitionServing` | per kind, name and channel: the version served and the one before it |
| `DefinitionEvents` | append-only: publish, switch, rollback, retire; polled by instances by id; the audit of what a channel serves |

- **Publish** validates, stores the next version and points the channel at it in one transaction: publishing is serving. The same
  content again changes nothing, so a retry is safe. **Switch** points a channel at any version (promotion), **rollback** returns to the
  previous one, **retire** stops serving and keeps the versions.
- **`DefinitionFollower`** on each instance hands each subscriber (`IDefinitionSubscriber`) what its kind serves, then polls the events.
  A subscriber builds the new version before replacing the old; if it cannot, the old one keeps serving and the problem is reported. The
  publisher need not be running.
- **Kinds:** `semantic-model` and `data-source` (Proxy), `app`, `app-instance` and `navigation` (the apps libraries; an app's files are
  serialized into the text content, under a size cap).
- **Publishers:** the Build server, paired with Proxy as a Publisher (13a T10), calls kind-specific endpoints
  (`PUT /api/v1/data-sources/{name}`, `/api/v1/semantic-models/{name}`) with `expected_version`, and pushes secret values in a separate call.
  Apps are published through the `apps` command.

What carries over unchanged: versions, channels, the group's state as the source of truth, followers, build-before-replace, idempotent
publish, optimistic concurrency, pairing as a Publisher.

## 2. Decisions (owner, 2026-11-04)

1. **One publish API** for every kind and every target, with a registry of kinds.
2. **Artifacts are content-addressed payloads** (plan 29 section 3.6), apps included.
3. **Stage, then activate**, as well as publish-and-serve.
4. **Bindings per channel**, outside the version, shared by every kind.
5. **Per-instance applied status**, for serving groups and runner groups.

## 3. The model

Kept in the `definitions` module (plan 29 section 3.4), one copy per target group:

| Table | Holds |
| --- | --- |
| `version` | kind, name, version number, the **manifest** (small text: what the version is), its hash, depends-on, published by, at, note, and for runner kinds the version's progress declaration (plan 29 section 4.3) |
| `version_artifact` | the files of a version: path, SHA-256, size, media type, and the payload id. A file unchanged between versions is one payload, stored once |
| `serving` | per kind, name and channel: the **active** version, the previous one, and a **staged** version if any |
| `binding` | per channel, kind and name: each slot the definition declares (a connection, a secret, a data source) and what it is bound to in that channel |
| `instance_status` | per instance, kind, name and channel: the version it has, and its state (`preparing`, `ready`, `active`, `failed`), the error, when |
| `event` | append-only: publish, stage, activate, switch, rollback, retire, bind; the change log followers poll and the audit |

### 3.1 Stage, then activate

- **Publish with `serve`** (today's behaviour): the version becomes active at once; each instance prepares it and swaps when ready, or
  keeps the old one and reports `failed`.
- **Publish with `stage`**: the version is staged. Every instance of the group prepares it (for the runner: resolve packages, compile,
  load, migrate progress in a dry run; for the serving layer: build the model or the app) and reports `ready` or `failed`. **Activation**
  then makes it active everywhere at once. It happens when an operator activates it, or automatically when every instance is ready; the
  publisher chooses, within the kind's defaults. **No activation at a set time** for now (owner, 2026-11-04); a later feature if wanted.
- **Defaults:** serving kinds `serve` (as today); runner kinds `stage`, activated automatically when every runner is ready.
- **A failed preparation never activates silently.** The console shows which instances failed and why; an administrator can activate
  anyway (the instances that failed keep the previous version and say so), as a recorded choice.

### 3.2 Bindings per channel

Plan 21: the same version is promoted from channel to channel and bound differently in each. A version declares its **slots** (named
connections, secrets, data sources, other definitions it calls); a binding fills a slot for a channel. Promotion copies nothing but the
version pointer; a channel without a binding for a required slot refuses the promotion, naming the slot. Today's separate secret push
from Build and app instances' `bind` settings both become bindings.

### 3.3 Per-instance status

Every instance writes its row as it prepares, applies or fails a version. The console shows, per definition and channel, which instances
run which version. An instance that leaves the group (its lease expires) is shown as gone and its rows removed by retention.

### 3.4 Dependencies

`depends_on` is checked, not only recorded: a publish or a promotion whose dependencies are not active in the target channel is refused,
naming them (plan 21 section 2, option D).

### 3.5 Versions and running work

A run that is in flight finishes on the version it started with; the next run uses the active version. Every run records the kind, name
and version it ran (plan 29's `runs` module), for audit and lineage. Activation of a runner kind waits, per task, for its runs in flight
to finish before migrating that task's progress (plan 29 section 4.3).

## 4. The API

On every target's management API, for every kind:

| Call | Does |
| --- | --- |
| `HEAD /api/v1/artifacts/{sha256}` | is this file already stored (so an unchanged file is never sent again) |
| `PUT /api/v1/artifacts/{sha256}` | upload one file, streamed; refused if its hash is not the one named |
| `PUT /api/v1/definitions/{kind}/{name}` | publish: the manifest, the artifact list (paths and hashes), depends-on, the channel, `serve` or `stage`, `expected_version` |
| `POST /api/v1/definitions/{kind}/{name}/activate`, `/switch`, `/rollback`, `/retire` | the life cycle |
| `PUT /api/v1/bindings/{channel}/{kind}/{name}` | bind slots for a channel |
| `GET /api/v1/definitions/...` | versions, what each channel serves, instance status, events |

**The registry of kinds.** Each kind registers: its validator (what the kind-specific endpoints check today: a catalog that loads, sources
that exist, an app manifest), its slots, its subscribers, the target types that accept it, its default publish mode, and whether it is
trusted code needing an administrator's approval at activation (plans 26 and 27 section 4.7).

**Clean break:** `PUT /api/v1/data-sources/{name}`, `/api/v1/semantic-models/{name}` and the apps command's own publishing are removed;
their checks become the kinds' validators. The Build server's `ProxyPublishing` uses the new API.

## 5. Kinds and targets

| Target | Kinds | Publishers |
| --- | --- | --- |
| Serving group | `semantic-model`, `data-source`, `app`, `app-instance`, `navigation`; later `agent`, `action`, `skill`, `knowledge-project` (plan 27 section 2.6), DbDataStore entity models and rules | Build, the apps back end, DbDataStore |
| Runner group | `integration`; later Sync's replication sets, Build's refresh plans, engine runtimes (plan 28 section 8) | DbDataIntegration; later Sync and Build |

Who may publish what is the pairing's Publisher role and its scope (plan 13 W12, `OwnerScope`), as today.

**Formats (plan 24a UC01).** Every version records the format of its manifest, `definition_version.format`, numbered from 1 per kind; every version stored before the column is format 1.
A kind registers the formats it reads (`DefinitionKind.Formats`: its current format and the one before it) and may say how a manifest states its format (`FormatOf`: the runner's
`integration` reads `schema: package/<n>`); a publish may also name it (`format` in the body). A program refuses what it does not read, with a message that names the kind, the name and both
numbers (`DefinitionFormat.Refusal`): a publish in a format the kind does not read; a publish, a switch, a rollback or an activation of a version that a **live instance of the group** cannot
read (the instance rows say which formats each instance reads, `definition_formats`, and the refusal names the instance and its software version); a serving follower (`DefinitionFollower`) and the
runner (`StagingFollower`, `DefinitionVersionSource`) handed a version newer than they read, which leaves the version they served before serving. An instance row that records no formats predates
the column and reads format 1.

## 6. Artifacts: what is published, how it is trusted, how it is served

Proposed 2026-11-04 from the owner's questions, and **decided by the owner the same day** as written here.

### 6.1 Compiled code and its source, both

**What happens today:** a `.csx` script is compiled where it runs (`CsxCompiler`, cached on disk by a key over the text, the
references and Roslyn's version), and its packages are resolved there (`PackageResolver`, from the feeds the configuration names).

**Proposed:** code is **built where it is published** (the DbDataIntegration or apps back end, or CI), and a version carries **both**:

- **the compiled assemblies**, which is what runs: no compiler and no package feed on the runner or serving instance, faster preparation,
  and what was approved is exactly what runs;
- **the source**, kept beside them: the console shows and compares what a version contains, a reviewer reads what was approved, and an
  administrator can rebuild it to check the build (section 6.2).

A `.csx` script is compiled at publish time too, with the same compiler. Compiled code depends on the host's contract assemblies and the
.NET runtime, so the manifest records the versions it was built against; an instance that cannot load it (a runner upgraded since)
**fails the staging with a message** asking for a republish. Recompiling from the source on the instance is an option an administrator can
turn on (the security posture), off by default.

### 6.2 Signatures: proof that a version came from a trusted publisher

A hash proves a file is the one the manifest names; it does not prove who wrote the manifest. Anyone who can write to a state database
(a database administrator, an attacker with that login) could add a version with matching hashes. So:

- **The publisher signs the version's manifest**, which lists every artifact's path and SHA-256, with its own key: Ed25519, as the update
  system already signs releases (`DbDataKit.Update`'s `Crypto`, `ReleaseVerifier`).
- **Each target group keeps a trust store:** which publishers' public keys it trusts, for which kinds. A publisher's key is registered when
  it pairs as a Publisher (plan 13 W12), and can be rotated or revoked from the console.
- **Every instance verifies** the signature when it prepares a version, and every file's hash when it fills its local cache (6.4). A
  version that fails either is refused, and the previous one keeps running.
- **Approvals are signed too.** An administrator's approval of trusted code (plans 26, 27 section 4.7) is a second signature, over the same
  manifest, with that person's key from the identity home, so "who approved what runs" is provable as well as recorded.
- **Defaults:** signatures required for every kind. An administrator can allow unsigned versions for a kind (for example in a test
  group), as a recorded choice.
- **Reproducible builds as an extra check:** compiled with `Deterministic` builds, the source in a version can be rebuilt and compared byte
  for byte. Offered as a console action, not required.

What signing does not protect against is a compromised publisher, whose key signs whatever it is given; that is what the approval
signature and the review in a workspace are for.

### 6.3 Packages published with the version

**Proposed: yes.** A version carries the **`.nupkg` files of its whole package closure** as artifacts, by hash:

- the runner needs **no access to a package feed** (air-gapped installs work) and does **no resolution** at activation;
- the bytes that run are the bytes that were reviewed and approved; the lock file's hashes (`DbDataKit.Scripting`, `scripting-lock/1`) and NuGet's own hash match them;
- a package shared by many versions or many integrations is **stored once** (content addressing).

Whole packages rather than the extracted files for one platform, so one version serves a group with Windows and Linux runners: each
instance extracts the assets for its own runtime identifier into its cache, as `DbDataKit.Scripting` does today. Resolving from a feed on
the instance stays possible as an administrator's option.

### 6.4 How an app is published and served

**Today:** an app version is one definition whose content is a JSON document holding the manifest and every file in base64 (up to
16 MB, `AppPackage`). Each request for a file resolves the instance (`AppInstanceStore.Resolve`): it reads the instance and the app version
from the state database, parses the whole package, decodes every file and loads the manifest, then answers with that one file. Correct,
but every file request costs database reads and the whole package's decoding; it does not scale past small apps.

**Proposed:**

1. **Published file by file, committed as one version.** The publisher sends the manifest with the list of files (path, hash, size,
   media type), uploads only the files the target does not already have (`HEAD`, then `PUT` by hash), and then publishes the version in one
   call. An app that changes one file sends one file.
2. **Each instance keeps a local cache on disk**, keyed by hash, under its data directory, filled when it **prepares** the version
   (verifying each hash and the signature). The cache is disposable: deleting it only means the next preparation fills it again from the
   state database.
3. **Prepared once, not per request:** while preparing, the instance builds an in-memory index of the version (path to hash, media type,
   size), writes the entry document with its `<base href>` already inserted, and stores compressed copies (Brotli, gzip) of text files.
4. **Served from disk:** a file is sent straight from the cache (the server's send-file path), with the hash as a strong `ETag`, a
   `304` for a matching `If-None-Match`, long caching for files whose names carry a hash, and `no-cache` for the entry document. The
   state database is not read to serve a file.
5. **One artifact cache for everything:** the same local cache holds apps' files, compiled assemblies and packages, on serving instances and
   runners, with eviction of hashes no prepared version uses.

## 7. Questions

| # | Question | Status |
| --- | --- | --- |
| 1 | How long versions and their artifacts are kept | **Decided** (owner, 2026-11-04): kept until an administrator prunes them, through an **API and a console screen that show the current state**: per definition and channel, the active, staged and previous versions, each instance's status, what is no longer served, and what pruning would remove. An artifact goes when no kept version names it |
| 2 | Activation at a set time | **Not now** (owner, 2026-11-04); maybe a later feature |
| 3 | Promotion from a test group to a production group | **Decided** (owner, 2026-11-04): a deploy of the same version to the other group. Artifacts are sent by hash, so nothing the production group already has is sent again; bindings are that group's own |
| 4 | Compiled code with its source (6.1) | **Decided** (owner, 2026-11-04): both; the compiled code runs, recompiling on the instance an option, off by default |
| 5 | Signatures (6.2) | **Decided** (owner, 2026-11-04): Ed25519 over the manifest, a trust store per group, approvals signed too, required by default |
| 6 | Packages with the version (6.3) | **Decided** (owner, 2026-11-04): whole `.nupkg` closure as artifacts; feed resolution on the instance an option |
| 7 | Apps (6.4) | **Decided** (owner, 2026-11-04): file by file by hash, one version commit, a local disk cache filled at preparation, served from disk |
