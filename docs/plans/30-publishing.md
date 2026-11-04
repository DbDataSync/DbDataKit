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
  then makes it active everywhere at once. It happens when an operator activates it, automatically when every instance is ready, or at a
  set time; the publisher chooses, within the kind's defaults.
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
| Serving group | `semantic-model`, `data-source`, `app`, `app-instance`, `navigation`; later `agent`, `action`, knowledge collections, DbDataStore entity models and rules | Build, the apps back end, DbDataStore |
| Runner group | `integration`; later Sync's replication sets, Build's refresh plans, engine runtimes (plan 28 section 8) | DbDataIntegration; later Sync and Build |

Who may publish what is the pairing's Publisher role and its scope (plan 13 W12, `OwnerScope`), as today.

## 6. Questions

| # | Question | Proposed |
| --- | --- | --- |
| 1 | How long versions and their artifacts are kept | versions are current state, kept (plan 29 section 3.3); an administrator can prune versions no channel serves and older than a limit; an artifact goes when no kept version names it |
| 2 | Activation at a set time | yes, for runner kinds, as a scheduled activation in the runner's schedules |
| 3 | Promotion across groups (a test group to a production group) | a publish of the same version, artifacts by hash (nothing is re-sent that the target has), bindings per group |
