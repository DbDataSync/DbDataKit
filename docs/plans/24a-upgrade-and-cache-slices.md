# Plan 24a: the slices of plan 24 (upgrades, compatibility and caching)

Status: **proposed** (2026-11-07). [Plan 24](24-upgrades-and-caching.md) says *what* the owner decided (2026-10-27); this plan breaks it into pull requests in the shape of
[22a](22a-gap-slices.md), [28a](28a-runner-slices.md) and [29a](29a-state-slices.md): one slice, one branch `suite/uc<nn>-<short-name>`, one pull request, "done" is the test
in the last column but one, and the status goes in the slice's row in the same change.

**Slice ids** `UC01`... (U is taken by 13a and 29b; UC was free in 13a, 13c, 18, 19, 20, 26, 28a, 28c, 29a, 29b). The order across plans 22, 23 and 24 is in
[22a, "The owner's order"](22a-gap-slices.md#the-owners-order-and-the-reason). **UC01 and UC02 are the first two slices of the three plans**: a format version on every published definition,
and the freeze of formats, are what lets the rules (22a), the checks (23a) and the cache setting (UC03) change a definition once.

## What exists (verified on `origin/main` at `4ffa395b`, 2026-11-07)

| Part of plan 24 | What is there | Where | What is not |
| --- | --- | --- | --- |
| **1. Versions shown** | the console asks each paired server for `/version` (product, program version, ready) and the management API version, and lists them | `DbDataConsole.Server/ConsoleInstances.cs` (`InstanceStatus`) | **no list of available versions**, no upgrade action |
| **1. Installers** | MSI, zip, Scoop manifest, `install.ps1`, `install.sh`, images, one CalVer per suite release (`<product>/v<YYYY.M.D.HHmm>`), stable URLs per product (`<product>/dist`), `SHA256SUMS` | `dist/`, `.github/workflows/suite-release.yml` | nothing that runs an installer on a server **from the console**; no feed the console reads |
| **1. The shelved updater** | signed `release/1` manifest, `update-plan/1`, detect, service swap with rollback, `UpdateDrainMiddleware` | `DbDataKit.Update`, `.Update.Abstractions`, `.Update.Detect`, `.Update.Service`, `.Update.Tools`, `.Update.Updater` (tests exist) | shelved by the owner (plan 24 section 1.1); its pieces are *candidates* for UC09, see QU3 |
| **1. Draining** | `IDrainCoordinator` (stop taking work, finish running work), serving modules have a drain step and health, a `maintenance` gate on the state management API that stops a service taking work | `DbDataKit.Hosting.Abstractions/Drain.cs`, `IServingModule`, `DbDataKit.State.Api` (`MaintenanceGate`) | no orchestration across the instances of a group; plan 20 O05 (two instances behind a balancer, one stopped) is not built |
| **1. Expand then contract** | plan 21 states the rule; `StateModule.CompatibleWithPrevious` (0 or 1) and the migrator's refusal "a migration is refused while a live instance cannot read the new version, naming it" (plan 29b U02) | `DbDataKit.State/Modules/StateModule.cs`, `StateMigrator.cs`, `MixedVersionGroupTests` | the declaration is **trusted, not checked**: nothing verifies that a migration marked compatible only adds |
| **2. N and N-1** | the **management API integer version**: each side supports its own and the one before; checked at pairing and on every call (`ManagementApi`, `ManagementVersionHandler`, `PairingClient`, `IncompatibleServerException` with the message "Upgrade this console / this server") | `DbDataKit.Web` | the check is of **one integer, the management API**; it does not cover the identity home, the publish protocol, the serving protocols, the state modules of a *pair*, or published definition formats |
| **2. State N-1** | the state modules' N and N-1 (`CompatibleWithPrevious`); CI job `state-older-engines` runs the State and Scheduling suites on the **previous release of SQL Server and PostgreSQL** (plan 29b U01) | `.github/workflows/dbdatakit-ci.yml` | this is **database engines** N-1, not **product releases** N-1; no job runs a previous release of a product against the current one |
| **2. Definition formats** | file formats carry `schemaVersion` or an id (`model/1`, `package/1`, `app/1`, `connection/1`, `dialect/1`, `engine/1`, `state-snapshot/1`) and a newer one is refused (DBD-110); `definition_version` has **no format column** and `content` is "opaque text: the kind's business" (29a D14); only the runner's `integration` kind is registered in `DefinitionKinds` | `DbDataKit.Config.Yaml/Diagnostics.cs`, `DbDataKit.State/Definitions/DefinitionsModule.cs`, `DefinitionKinds.cs` | a serving instance cannot tell a definition's format without parsing it; a publish is not refused when an instance cannot read it |
| **3. Caches** | **only a metadata catalog cache** (`CachingCatalogReader`, `ICatalogCache`) | `DbDataKit.Metadata` | **no query result cache** (`git grep` for `ResultCache`, `QueryCache` and `MemoryCache` finds nothing outside the metadata cache); no `cache:` setting in any definition |
| **3. What a key needs** | `ServingContext` carries channel, preview and trace; `ServingProvenance` carries the definition version; the lineage graph answers `Impact` | `DbDataKit.Serving.Abstractions`, `DbDataKit.Lineage` | access-rule attribute values: none yet (plan 22, [22a GP10](22a-gap-slices.md)) |
| **3. Change events** | `definition_event` (an append-only feed of publishes and switches, polled with `WHERE id > $last`); runs in each product's own state | `DbDataKit.State/Definitions` | no "data changed" event from a Build load, a Sync run or a DbDataStore write to the serving layer |

## Rules for every slice

- The rules of [13a](13a-suite-slices.md) and [22a](22a-gap-slices.md) apply: one small pull request, "done" is the test, CI green and independently revertable, behaviour changes
  named, docs and the status line in the same change; **a wave 1 slice names the formats it touches in the first line of its pull request**; a changed format is a clean break with a message naming the file.
- State runs on **SQLite, SQL Server and PostgreSQL**; a screen has a Playwright test; a library has unit tests; a CI job has a test of the job itself (a deliberately broken input makes it fail).

## Decisions made while planning

| # | Decision | Why |
| --- | --- | --- |
| D1 | **"Release N" means a compatibility generation**, an integer `SuiteCompatibility` that a change bumps when it changes a contract between parts (a management API, a protocol, a definition format, a state module marked incompatible). A release carries the generation it was built with; **N-1 is the generation before** | CalVer releases are every day, and "the previous release" would make N-1 mean "yesterday"; a generation says what the owner means (`ManagementApi.Version` is the same idea for one contract) |
| D2 | The generation is a **constant in `DbDataKit.Web`**, reported in `/version`, in the pairing hello and in the instance row; a repository check fails a pull request that changes a frozen contract (UC02) without bumping it | the number must not depend on someone remembering |
| D3 | **Definition formats are per kind and numbered from 1.** `format` is a column of `definition_version` and a field of the manifest; a kind registers the formats it reads (`Current`, `Previous`); a serving instance reads those two and **refuses newer** with a message | plan 24 section 2.4; the same clean-break rule as configuration files |
| D4 | A **publish is refused** when a live instance of the target group cannot read the version's format (reusing plan 29b U02's instance rows), naming the instance and its version | the group must never hold a version one of its instances cannot serve |
| D5 | **Format 2 is open until the first release that holds a section of it** ([22a D1](22a-gap-slices.md)): `access` (22a GP01), `checks` (23a FQ01) and `cache` (UC03) are added to it, then UC02 freezes it | one break for three plans |
| D6 | The upgrade is done **by the server's own management API** (`upgrade prepare`, `upgrade apply`) using the product's installer; the console orders and watches. A server cannot be reached by the console running an installer on another machine | the console has no agent on other machines, and plan 24 keeps the self-updater shelved, so the server's own installer is the only thing both can use |
| D7 | The cache is a **serving-layer service** (`IServingCache` in the core services, plan 20 section 10.3 "Platform"), never a module's own | a module is never handed a raw executor; the key is built in one place, with rules and channel |

## Questions for the owner

None blocks UC01 to UC05. Each has a default.

| # | Question | Default |
| --- | --- | --- |
| QU1 | What "N" means (D1) | the **compatibility generation** of D1, an integer, bumped by the repository check |
| QU2 | Where the console learns the available versions | the **GitHub release feed** at the stable URLs of `dist/README.md` (and `SHA256SUMS`), with a setting for a mirror folder or URL for sites without internet. Not the shelved `Update.*` packages |
| QU3 | Who runs the installer on a server's machine | **the server itself** (D6): a Windows server runs its MSI through a short-lived helper, a Linux server a script for its zip and systemd unit. **Images and package installs are not upgraded by the console**: it shows the command and the new version (a container is replaced by the orchestrator). Reusing `Update.Service`'s swap with rollback is allowed *as code*, without enabling the updater |
| QU4 | The order for a mixed install | **console first, then serving groups (rolling), then back-ends**; any order works inside N and N-1, this one puts the part that must understand both first |
| QU5 | How long a drain may wait | **10 minutes** (a setting); then the upgrade of that instance stops and asks; queries are never killed silently |
| QU6 | The shared cache store for serving groups | the **group's state database** (a state module with payloads), not a new service such as Redis |
| QU7 | Default time-to-live of a cached dataset | **5 minutes**, a setting per dataset, at most 24 hours |
| QU8 | How a serving group learns that data changed | **pushed** by the paired Build, Sync and DbDataStore servers over the management channel they already have (a signed `data.changed` message with the dataset identity), plus the time-to-live; no polling of another product's state |
| QU9 | The "previous release" the CI job runs | the **latest stable release of each product** at the time of the run (the `<product>/dist` assets); a pull request that bumps the generation also runs against the one before it |
| QU10 | Is a cache hit's provenance different | **no**: a hit says the same version and channel; it adds `cache: hit` to the provenance only |

## Milestones

| Milestone | Slices | What is true after it |
| --- | --- | --- |
| **U-A: formats and pairing** | UC01 to UC05 | every published definition has a format; formats are frozen by a check; the cache setting has its place; pairing and instance rows carry the generation |
| **U-B: compatibility proved** | UC06, UC07 | state migrations are checked, not trusted; a previous release is run against the current one in CI |
| **U-C: upgrades** | UC08 to UC11 | the console shows what is available, upgrades one server, then a group rolling |
| **U-D: caching** | UC12 to UC16 | an owner turns the cache on in a definition; no one sees another's rows; it clears when data changes |

## The slices

Size: S under a day, M a few days, L about a week. **Format** says whether the slice changes a stored format or a definition, and which.

### U-A: formats and pairing (wave 1)

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| UC01 | **A format version on every published definition (D3, D4):** `definition_version.format`; `DefinitionKind` gains the formats it reads; the publish API records the manifest's format and refuses a format the kind does not read, or one a live instance of the group cannot read (naming the instance); reads refuse newer with the message; the serving and runner instance rows say which formats they read | plan 30 R15 and plan 29b U02 (built) | M | **`definitions` module migration** (`format` column, additive, existing rows are format 1), **instance rows** (an additive column) | on SQLite, SQL Server and PostgreSQL: a migrated database keeps its versions as format 1 (a migration test); a format-2 publish to a group with an instance that reads format 1 is refused naming the instance (two instances at different versions); a definition newer than the instance reads is refused with the message naming kind, name and both formats; the mixed-version tests of 29b U02 still pass **Status: done** (2026-11-07, local branch `work/uc-formats`, the formats touched: the `definitions` module's `definition_version` (migration 4, `format`), the Proxy's `serving_instance` (migration 4) and the runner's `instance` (migration 3) rows (`definition_formats`)). The done-when is `DefinitionFormatTests` in `DbDataKit.State.Tests`, run here on SQLite, SQL Server and PostgreSQL: a database migrated from before the column keeps its versions as format 1; a format-2 publish to a group with an instance that reads format 1 is refused naming the instance, its host and software version (two instances at different versions; a switch, a rollback and an activation too); a version newer than a follower reads is refused with a message naming kind, name and both formats and the version served before keeps serving; the runner's staging and version source refuse the same way. **Built:** `DefinitionFormats`, `DefinitionFormat.Refusal` and the instance rows' `definition_formats` text (`DefinitionFormatText`) in `DbDataKit.State`; `DefinitionKind.Formats` and `FormatOf`; `PublishRequest.Format` (the hash covers a format above 1, so format-1 hashes are unchanged); `IDefinitionSubscriber.Formats`; the runner's `integration` kind reads `schema: package/<n>` as its format and the publish API takes `format`; the Proxy's lease writes what its follower reads; `GET /api/v1/state` lists `definition_formats` per instance. The `definitions` module declares itself compatible with the version before (the column has a default). **Not done:** no kind of the serving layer has a format above 1 yet (UC03 gives the three of format 2); the Proxy publishes through `DefinitionStore`, which takes the format as an argument and does not look the instance rows up for format 1 (every instance reads it) |
| UC02 | **The format registry and its freeze:** `docs/formats.json` lists every definition and file format (id, current, previous, schema hash, status `open` or `frozen`); `scripts/check_definition_formats.py` fails when a frozen format's schema or loader tests change without a bump, and when a bump has no generation bump (D2); a unit test over the kinds registered at runtime agrees with the file | UC01 | M | none (a lock file) | `scripts/tests`: the check passes on the repository; each negative fixture fails (an edited frozen schema, a bump without a generation, a format missing from the file); the runtime agreement test lists every kind |
| UC03 | **The `cache:` section of a definition:** `enabled`, `ttl` (QU7), `scope` (`caller`, `rules`, `shared`), `dataset`; validated, part of the open format 2 (D5); the kinds' validators refuse it on a dataset that cannot be cached (an entity read with effective-dated writes in flight, a data source with no lineage identity) | UC02, GP01 for the format 2 shape | S | **new section `cache`** (format 2) | unit tests: each key valid, each bad value refused with file and line; the schema agrees with the loader; a definition with `cache` read by a format-1 instance is refused by UC01's rule |
| UC04 | **The compatibility generation at pairing (D1, D2):** `SuiteCompatibility` in `DbDataKit.Web`; `/version` and the pairing hello carry it next to the program version; a pairing, or a reconnection after an upgrade, **refuses a pair further apart than N-1** with a message naming both program versions and generations and which side to upgrade; the registry file `instances.yaml` records it | UC02 | M | **`instances.yaml`** (a new key `generation`; an older console reads it as absent and keeps working), **pairing records in a group's state** (additive) | `DbDataKit.Web.Tests` and `DbDataConsole.Tests`: a server one generation behind pairs; two behind is refused with a message that names versions and the side to upgrade; after the server's upgrade the reconnect succeeds; a registry file without the key loads; the management API integer check of today is untouched |
| UC05 | **Release and generation in the instance rows and screens:** each serving and runner instance writes its program version and generation into its row (plan 29b U02 wrote module versions); `state show`, `instance list` and the console's Servers page show them and mark a pair outside N-1 | UC04 | S | **instance rows** (additive columns) | on three engines: two instances of one group at different generations are both listed with their numbers; the Servers page (Playwright) shows the warning on the one outside N-1; the migration of 29b U02 still names a blocking instance |

### U-B: compatibility proved (wave 3)

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| UC06 | **Expand-then-contract is checked:** a test over every state module's migrations that classifies each new statement as *additive* (a table, a nullable or defaulted column, an index) or *removing* and fails a migration marked `CompatibleWithPrevious = 1` that removes or renames; a removal must name the release in which the previous code stopped using the thing (`Contract` note on the migration), which the test reads | UC02 | M | none (a note on migrations) | the test passes over the modules of the repository; negative fixtures fail (a dropped column marked compatible, a rename, a removal with no note); an existing incompatible migration is listed, not hidden |
| UC07 | **The compatibility CI job:** a workflow that fetches the previous stable release of each product (QU9) and runs it against the current build: management API and pairing (console N with server N-1 and the reverse), publish (a Build server N-1 publishing to a serving group N), and the serving protocols (a Flight SQL client N-1 against the serving layer N and back) | UC04 | L | none | the workflow runs on every pull request touching a contract and passes; **a test of the job**: with a stub "previous release" that speaks a generation two behind, the job fails naming the pair; the job's matrix is generated from `docs/formats.json` and the generation |

### U-C: upgrades (wave 3; plan 24 section 1)

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| UC08 | **The console shows what is available:** a release feed reader (QU2) with a mirror setting, the available versions per product, each paired server's current and newest, a mark for "outside N-1 after this release", and the release notes link | UC05 | M | **new setting** `upgrade.feed` in the console | `DbDataConsole.Tests` with a fake feed: versions listed per product, a mirror folder works offline, a malformed feed is refused naming it, the `SHA256SUMS` of a release is read; Playwright: the Servers page shows "newer available" and the target versions |
| UC09 | **Upgrade one server (D6, QU3):** the management API `POST /api/v1/upgrade` (admin) with `prepare` (download, verify the checksum, check the generation against its peers, report what will happen) and `apply` (drain, run the installer, restart, health), with rollback when the new version does not become healthy; a status endpoint the console polls | UC08, plan 13 L, `Hosting.Abstractions` drain | L | none | on a Linux service with a zip and a systemd-like fake unit: prepare verifies and reports; a tampered download is refused; apply drains, swaps and the new version answers `/version`; a new version that fails health is rolled back and the old one serves again (a test with a deliberately broken build); Windows MSI path in the existing MSI CI jobs |
| UC10 | **The console drives it one server at a time (QU4):** a plan (which servers, in which order), a start per server, a stop on the first failure, the audit; images and package installs show the instruction instead (QU3) | UC09 | M | none | `DbDataConsole.Tests` with fake servers: the order of QU4; the run stops at the first server that fails its health check and the rest stay on the old version; an image-based server is listed with its command and skipped; Playwright: start, watch, see the stop |
| UC11 | **Serving groups upgrade rolling, draining first:** per instance stop taking new work, finish running queries (the drain timeout, QU5), upgrade, pass the instance's module health checks, rejoin; a failed health check stops the group's upgrade there and the rest keep serving the old version | UC09, UC05, plan 20 O05 | L | none | two Proxy instances behind a test balancer: an upgrade of both under a running query load never drops a query that was accepted; with the second instance's new build failing its health check, the upgrade stops, the first serves the new version, the second the old, and the group answers throughout (the O05 pattern) |

### U-D: caching (wave 3; plan 24 section 3)

| Slice | What | Needs | Size | Format | Done when |
| --- | --- | --- | --- | --- | --- |
| UC12 | **The cache service and its key (D7):** `IServingCache` in the serving core; the key is the canonical query, the **attribute values that applied** (the function [22a GP10](22a-gap-slices.md) provides; empty until rules exist), the channel, the preview draft and the published version; off unless the definition's `cache` says so; in memory per instance with a size bound; a cold or lost cache changes no result | UC03, GP10 | M | none | unit tests: two users with the same effective access share an entry, with different values they do not, two channels do not, a preview does not share with its channel, a new published version is a new key; a dataset without `cache` is never stored (asserted); dropping the cache gives byte-identical results |
| UC13 | **Expiry when data changes (QU8):** a signed `data.changed` message from Build (a load), Sync (a run) and DbDataStore (a write) to the paired serving group; the lineage graph's `Impact` names which entries depend on the dataset; a new publish clears the definition's entries | UC12, lineage (built) | L | none | with two paired fake servers and a serving instance: a Build load of a table clears the entries of every model that reads it and no others; a DbDataStore write clears its entity's entries; a forged or replayed message is refused; a publish clears; the message carries a dataset identity and no row data |
| UC14 | **The time-to-live backstop and the shared store (QU6, QU7):** every cached dataset expires by its time-to-live; an optional shared store for groups in a state module `result_cache` (payloads, retention by the time-to-live) | UC12, Q04 and Q05 (built) | M | **new state module `result_cache`** (optional, per group) | on three engines: two instances share a hit; an entry past its time-to-live is a miss; the shared store down is a miss, not an error; the module's convention, retention and layout tests |
| UC15 | **Cache safety as a conformance suite:** the same queries as users with different rules, on different channels, with and without a preview, through every module that caches, with the cache on and off, return the same rows | UC12, GP11 to GP14 | M | none | the suite passes for Flight SQL and the semantic module; **a negative test** (a module that builds its own key without the attributes) fails it; run with the cache cold, warm and shared |
| UC16 | **The console and the documents:** cache settings shown per dataset, hit rate and entries per instance, a clear button for an administrator, the docs for `cache:` (what is keyed, what clears, what the time-to-live is for) | UC14 | S | none | Playwright: a dataset's hit rate rises after repeated queries, the clear button empties it and the next query is a miss; the docs links resolve |

## What each slice unblocks

| Waiting work | Unblocked by |
| --- | --- |
| [22a GP02](22a-gap-slices.md) (rules in published definitions), [23a FQ05](23a-first-run-and-quality-slices.md) (checks in the serving definition) | UC01, UC02 |
| Plan 20's O05 outage and upgrade story, plan 29b's remaining operations slices | UC05, UC11 |
| Plan 28c's runtime packages with N and N-1 (`launch.runtime`) | UC04 (the generation), UC06 |
| Plan 30's "kinds and their formats" (section 5) | UC01 (the kind registers its formats) |
| Plan 21's expand-then-contract rule becomes a test, not a sentence | UC06 |
| Plan 27's knowledge index (cache of embedding searches, if wanted) | UC12 (the service exists) |

## What can start now

**UC01, UC04 and UC06 need nothing that is not built** and can start together. UC02 follows UC01 (it needs the registry's runtime half) but its script part can start at once. UC03 waits for UC02 and for the
shape of format 2 from 22a GP01. UC05 follows UC04. UC08 can start when UC05 is done; UC09 is the long one and can start in parallel with UC08 (it needs the generation, not the feed). UC12 can be written
with an empty attribute set before 22a's GP10 lands, and then given the real one.

## Risks

- **The generation (D1) is a new idea in the suite.** If the owner prefers a rule on CalVer ("the previous stable release"), UC04 changes and UC07's matrix changes with it; the formats and instance rows do not. QU1 is the question to answer before UC04.
- **UC09 is where plan 24 meets the shelved updater.** The service swap with rollback is real work that `DbDataKit.Update.Service` already does. The slice may reuse that code without reviving the updater (QU3); if the owner wants no reuse at all, UC09 grows from L to a second L.
- **Format 2 open** (D5): a release that ships with only part of format 2 needs a format 3 for the rest. UC02's check and the release note guard it.
- **A cache serves wrong rows if a key is incomplete.** UC15 is the guard and has a negative test; UC12 does not turn the cache on for any definition by default (QU7 only sets the default time-to-live of one that opts in).
- **Data-change events (UC13) cross products.** A Build server that cannot reach the serving group falls back to the time-to-live; the console shows "events not arriving" so a long time-to-live is not a silent staleness.
- **Rolling upgrades need the serving layer's O05** (two instances behind a balancer in CI); until it exists UC11's test uses a test balancer and says so.
- **Images and package installs** are not upgraded by the console (QU3); a site that runs only containers sees UC08 and UC10's instructions, not an upgrade.
