# Plan 29a: what is left of the state rework

Status: **in progress, what is left** (audited 2026-11-09 against the code and the tests). The mechanism (modules, namespaces, the registry and migrator, conventions, payloads, retention, the `state:` settings, grants, SQLite files per namespace), the shared modules (notifications, app storage, definitions, identity, trust, scheduling, runs, audit, lineage) and the moves of the console, the serving layer, the Build server, DbDataStore and the console's views of every service's runs are built and tested on SQLite, PostgreSQL and SQL Server, and so are the runner's progress store, the declared-progress migration planner, the tracking follower's library and Sync's and Build's modules. Their slices Q01 to Q19, Q23 and Q24 are deleted from this plan; what is still true of them is documentation beside the code: **`Shared/src/DbDataKit.State/README.md`** (modules, namespaces, conventions, payloads, the progress store and progress across versions, tracking with `both`, the topologies, moving state), `DbDataKit.State.Engines/README.md` (the `state:` section, the settings order, the engine support matrix, rolling upgrades of a group), `DbDataKit.State.Cli/README.md` and `DbDataKit.State.Api/README.md` (the verbs and endpoints). Moving state between engines is [29b](29b-state-migration-slices.md).

The owner allowed breaking changes (2026-11-06), so every conversion is a **clean break** (the rule in `docs/architecture.md`): a database written by the old structure is refused with a message naming it, never converted.

Slices follow the rules of `CLAUDE.md` ("Making changes"): one small pull request, "done" is the test in the last column, every slice runs on all three engines (SQLite always; PostgreSQL and SQL Server through `testenv/`), and the row is deleted in the commit that finishes it. A row that keeps an id of the old plan is what remains of that slice.

## Open slices

The old path (`StateSchema`, `SchemaVersion`, the per-product `Migrations` lists, `UserStore`, `SessionStore`, `InviteStore`, `OneTimeValues`, the legacy `NotificationStore` and the work-queue fragment) stays until the last product has moved; each remaining product is converted in its own slice and nothing in the middle leaves a product unable to start.

| # | What is left | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| Q20 | **The runner's progress, the rest:** a **migration function** for the change the standard rules cannot express (one checkpoint split into two by parsing; it runs at staging over the old values and its output is checked against the new declaration; today the planner refuses such a change naming the value). It needs a `migrate` key in the package manifest, which is the frozen `package/1` format: a `package/2` (`docs/formats.json`) and a place to run the function (the new version's task host, at staging) | none | M | a version whose declaration needs a function stages with the function's output checked |
| Q21 | **The tracking follower, the rest:** the follower runs on the runner (after every refresh task and from a small `catch-up` task of the Build project package; plan 28c E16), the **console's divergence screen** and its resolve and switch-primary actions (28c E18), and the **`runs` module reads Build's runs from the copy** | 28c E16, E18 | M, split | a restored target is marked diverged in the console and the administrator resolves it; Build's runs and Sync's and the runner's are one query |
| Q22 | **Sync on the modules, the rest:** the running processes use the runner-owned state (28c E12), the verification result is a payload, and `Migrations.Templates` is deleted | 28c E12 | M | Sync's integration suites on all engines with the processes on the runner; nothing references `Migrations.Templates` |
| Q25 | **Delete the old path:** `StateSchema`, `SchemaVersion`, `user_version` handling, the per-product `Migrations` lists and the older stores listed above | Q22, 28c E22 | S | nothing references them; the schema tests are the module tests |

## Left over from built slices

- The Proxy's `group` name is the default `main` (`ProxyState.Open(..., group)` takes one; naming it in `server.yaml` is the `state:` section's work).
