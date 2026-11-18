# DbDataWorkspace

The server where people (and, later, agents) author projects in git workspaces (plan 22a GP31, decision D8; plan 27). A project is a git repository of some kind (an app today; a Build, Store, Sync and integration project beside it, see [`docs/integrations.md`](../docs/integrations.md); plan 27 adds agent, skill and knowledge projects); a **workspace** is one person's draft of it, a git worktree on its own branch in a folder the server owns, so a draft never changes what is published. The server lists, creates and abandons workspaces, shows their changes, commits, pushes and merges them (the shared library `DbDataKit.Authoring`, which Build serves its workspaces with too), and keeps a record of what was done in its state database. It has no web page of its own: a console pairs with it and shows its screens (Servers, then the Workspace server, then Workspaces).
**Built:** editing a draft's files, commit, push and merge (GP35a); an app project starts as the app template (GP32); builds from a vendored package store, a read-only preview and publishing to a paired Gateway (GP35b, below). **Not built:** a preview with live data (the semantic route and the apps host's preview channel), a git remote for a project. The `build` table and the publisher's key reference are in the state module; the builds write the one and publishing reads the other.

```
dbdataworkspace server run [--repo <server directory>] [--listen https://127.0.0.1:8471] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]
dbdataworkspace server pair --max-role <roles> [--repo <server directory>]
dbdataworkspace server clients | unpair <client> [--repo <server directory>]
dbdataworkspace project create <id> [--title <text>] [--kind app|build|store|sync|integration] [--template <name>] [--repo <server directory>]   # a git repository in projects/<id> with a first commit
dbdataworkspace project add <id> --path <repository folder> [--title <text>] [--kind app|build|store|sync|integration] [--template <name>] [--repo <server directory>]   # host a repository that is already on this machine, in place
dbdataworkspace project list [--repo <server directory>]
dbdataworkspace workspace list [--project <id>] [--repo <server directory>]
dbdataworkspace publisher pair <name> --url <https address of a Gateway's management API> --code <pairing code> [--client-name <text>] [--repo <server directory>]   # make the key for a target and pair it with a Gateway (the code: `server pair --max-role publisher` there)
dbdataworkspace publisher list [--repo <server directory>]   # each target's key reference (never the key) and its Gateway
dbdataworkspace service install|uninstall|status|start|stop|restart|doctor|unit
dbdataworkspace state init --topology service|group|install [--engine mssql|postgres --connection <connection with Password=secret:NAME or env:NAME>] [--login <role or user> [--migrate]] [--admin-connection <reference> --apply] [--group <name>] [--settings <file>] [--repo <server directory>]   # writes the state section of server.yaml (a SQLite file per service is the default)
dbdataworkspace state show [--offline] [--engine mssql|postgres] [--repo <server directory>]      # the state database: engine, where each setting came from, namespaces, module versions, pending migrations; creates nothing
dbdataworkspace state test [--repo <server directory>]                   # opens it read-only and checks rights, engine version and clashes; stops at the first failure with the fix
dbdataworkspace state grants --login <database role or user> [--migrate] [--repo <server directory>]
dbdataworkspace state export --to <directory> [--include-secrets-check] [--repo <server directory>]   # the state as a snapshot; stop the server first
dbdataworkspace state import --from <directory> [--migrate-after true|false] [--repo <server directory>]   # into an empty directory's state
dbdataworkspace state verify --snapshot <directory> [--db] [--rehash-payloads] [--repo <server directory>]   # the snapshot against itself, or the state against it
dbdataworkspace state move --to-engine sqlite|mssql|postgres [--to-connection <string with secret references>] [--yes] [--repo <server directory>]   # offline: export, import into the empty target, verify, then write the new settings
dbdataworkspace secret set|list|delete|... [--repo <server directory>]   # the server's own secret store (the shared secret verbs)
dbdataworkspace health check [--url <url>]
```

## A project's two areas: Files and Designer

A draft is edited in two areas, the same two for every kind of project, each with its own address and its own entries in the project's navigation:

- **Files** is the stock editor every kind has: the file tree, each file as the form generated from its format's schema or as YAML in the code editor, and the draft's tabs (Changes, Check, the kind's own tabs, Builds, History). Every file of every project can always be read and changed here. Its addresses are `/:project/:draft/files/...` (a file's path after `files/`).
- **Designer** is the kind's own designed screens: pages laid out around what the person authors rather than around files (for a sync project, each replication's page with its tabs and the table mappings with the source and target side by side). A Designer reads and writes the same files of the same draft; it adds no format of its own. Its addresses are `/:project/:draft/designer/...`, and its navigation entries are the kind's own (for a sync project, the project's replications and **New replication**).

The person moves between the two by clicking: a file in the Files tree, a replication or a mapping under Designer, or a link one area gives to the other. Nothing is remembered per person and there is no switch to set: the address alone says which area and which place is open, so a reload or a shared link opens the same one. A kind without a Designer shows only Files; a draft of a kind with one opens on its Designer, always: the landing page is fixed by the kind, not a choice stored per person or per draft (a person who wants Files clicks it, or keeps a link to a `files/` address).

Both areas edit one draft: an edit not yet saved in the Designer is still there, not saved, when the person clicks into Files, and the reverse; the draft's chrome (**not saved** with **Save**, the check's count, **Commit…**) is the same in both, and the check runs on the files whichever area changed them. A file the Designer cannot show (it does not parse, or it is of a format version newer than the screens know) is shown in the Designer as a line naming the file with a link that opens it in Files, never as an empty or partial screen. History, diffs and earlier texts of a file are the Files area's (History, Changes, a file's earlier texts); a Designer does not repeat them.

Why: one way to move between designed screens and raw files for every kind, so a person who learned it on one kind knows it on the next, and the stock editor is always there as the fallback for whatever a Designer does not offer yet (a key the screens do not show, a file they cannot read, a part of the project the Designer does not cover yet).

Example: in the sync project `erp-sync`, an engineer opens the draft `add-order-lines` and lands on the Designer, on the replication `erp-to-dw`. On the mapping `customers` she ticks two key columns, then wants `relationshipColumns`, which the Column Mapping tab does not offer: under Files in the navigation she clicks the mapping's file, it opens as a form with her two key columns already in it (not saved), she sets the key in YAML, clicks `customers` under `erp-to-dw` in the Designer's entries and is back on the mapping's Column Mapping tab, saves once and commits.

A kind's Designer is a web package of its own, registered with `@dbdatakit/workspace` (a component, its routes and its navigation entries), built and tested on its own; components any product uses go to `@dbdatakit/shell`. The sync Designer is `@dbdatakit/sync-ui` (`Shared/web/sync-ui`, an npm workspace like `store-ui`): the Sync server's authoring screens of a replication, restored, with everything that watched or operated a running replication left out. Why a package rather than a folder of the Workspace's: the Workspace's own package stays the frame and Files, and a later pass can put the screens that watch a replication beside the authoring ones for the console's Runner pages.

The sync Designer's server calls (the script slots a binding can name, a connection's readers, staging providers and writers, the databases, tables and columns of the catalog cache, a mapping's inferred column types and key, bulk create of mappings) are answered in the Workspace's own process, from Sync's libraries under `Shared/src/DbDataKit.Sync.*` (the configuration model, the driver descriptors and registry, the script host's slot list, the mapping column reader), so the column picker can ask as the person types. Anything that compiles or runs a script a person wrote stays a child process on a copy of the draft, as the script test is: the Workspace never runs an author's code in its own process.

A sync project's engines come from the suite's engine registry (`DbDataKit.Connections.Descriptor`'s `engines/*.engine.yaml`, read into the web build), one list that the Files area's Sync forms and the Designer's screens both read (`useSyncEngines` of `@dbdatakit/workspace`): as a source the engines whose `sync_source` role is `supported` or `unverified`, as a target those whose `sync_target` is, each with the role's note; a role that is `planned` is shown as planned and not offered, and an engine with no Sync role is not listed. A driver the project carries itself (`drivers/<id>/driver.yaml` or `driver.csx`) is listed as custom. Example: the new-connection form of Files offers `postgres: source and target` and `flightsql: source`, and a connection naming `flightsql` says under its form that it may be a source and not a target.

The sync Designer's navigation lists the draft's replications and ends with **New replication**, the last entry, which opens a dialog asking only a name and a schedule (the old Replications page's form); the replication is made in the draft and its Overview opens, where the person picks the source's and the target's connections. With no connection in the draft the dialog says so and links to `config/connections/` in Files.

The Designer is not built yet; today every project shows Files only. Sync is the first kind to get a Designer, and it covers what lives inside a replication; a sync project's connections and scripts are edited in Files.

## Reading a database while authoring

The Workspace holds no login of a customer database and never opens one. What a project's databases hold is read on a Runner, as a task run as the person who asked, with the login a channel binds to the project's slot: discovery of databases, schemas, tables and columns, a query's columns, the connection test, and each package's `preflight` before it is served in production. Before anything is published, **Set up for development** binds a slot in a non-production channel on the Runner with a login the person types once (it is sent to the Runner and kept nowhere in the Workspace). What discovery finds is committed in the draft as `catalog-cache.yaml` with the channel and Runner group it was read from, refreshed only on request with the difference shown, and read offline by the pickers and the draft's check; no row, sample, value statistic, login or address is kept. The design, with what is built and examples: [docs/authoring-metadata.md](../docs/authoring-metadata.md).

## The server's directory

| Path | What |
| --- | --- |
| `server.yaml` | the `state:` section (engine and connection) and the `apps:` section (how an app is built: `packageStore`, `allowedPackages`, `packageStoreSha256`, `isolation: required\|auto`, `buildTimeoutSeconds`, `bun`; a server with no `packageStore` does not build) |
| `state.db` | the state database on SQLite (the default); `DBDATAKIT_STATE_WORKSPACE_ENGINE` and `DBDATAKIT_STATE_WORKSPACE_CONNECTION`, or the `state:` section, name SQL Server or PostgreSQL |
| `projects/<id>/` | a project the server made (a git repository); a project added by path stays where it is |
| `workspaces/<project>/<id>/` and `<id>.json` | a workspace's worktree and the record that says whose it is (the layout Build's server has always had; `DbDataKit.Authoring`) |
| `builds/<project>/<id>/` | a build: `app/` (the built folder), `result.json`, `publishes.jsonl` |
| `publish-targets.json` | the Gateway each publisher key is paired with (address, instance id, pinned fingerprint, roles; never a key) |
| `management-audit.log` | who made a project or changed a publisher's key reference, never a value |

`DBDATAKIT_DATA_WORKSPACE` (or the folder `workspace` under `DBDATAKIT_DATA`) names the directory for every command; the service's own is `%ProgramData%\DbDataKit\workspace` (`/var/lib/dbdatakit/workspace`).

## The state module `workspace`

Kept per service in the state database (`DbDataKit.State`, `WorkspaceModule`): `project` (id, kind, repository folder), `workspace` (the drafts that exist now), `workspace_event` (created, committed, pushed, merged, abandoned, with who; kept a year), `build` (kept 90 days; nothing writes it yet) and `publisher_key` (for a target, the **reference** to the secret that holds the publisher's key; a key itself is refused). The retention rules are the shared job's.

## The management API

Every call needs a token a paired console issued; the roles are the suite's (`DbDataKit.Web`).

| Call | Role |
| --- | --- |
| `GET /api/v1/info` | any |
| `GET /api/v1/project-kinds` | viewer (the kinds of project, what each publishes as and to which product; plan 33 SC20a) |
| `GET .../{id}/check` (under `/api/v1/projects/{project}/workspaces`) | viewer (the draft checked by its kind's validators: diagnostics with file and line) |
| `GET /api/v1/projects` | viewer (a token scoped to some projects lists those) |
| `POST /api/v1/projects` `{id, title?, kind?}` | admin |
| `GET\|POST /api/v1/projects/{project}/workspaces`, `DELETE .../{id}` | viewer; author to make one; the owner abandons theirs |
| `GET .../{id}/files`, `GET .../{id}/files/{path}`, `GET .../{id}/changes`, `GET .../{id}/history`, `GET .../{id}/events` | viewer |
| `PUT .../{id}/files/{path}` `{content}`, `DELETE .../{id}/files/{path}`, `POST .../{id}/move` `{from, to}` | author, the owner (text files of at most 1 MiB; a path stays inside the draft and never names `.git`; saves are not history, the commit is) |
| `POST .../{id}/commit`, `POST .../{id}/push` | author, the owner |
| `POST .../{id}/merge` | approver (a project that follows a git remote is pushed to, never merged here) |
| `GET /api/v1/publisher-keys`, `PUT\|DELETE /api/v1/publisher-keys/{name}` `{secret_ref}` | admin |
| `POST /api/v1/publisher-keys/{name}/pair` `{url, code, product?}`, `GET /api/v1/publish-targets[?project=<id>]` | admin; approver (each target says its `product`, `gateway` or `runner`, found from the server's own `/version`; with `project`, `accepts` says whether that project's kind publishes to it; plan 33 SC20b) |
| `GET /api/v1/state` and the maintenance calls | the shared state API (`DbDataKit.State.Api`) |

A build of a project whose kind produces something to publish (`build`, `sync`, `store`, `integration`) is the kind's check followed by its artifact in `builds/<project>/<id>/artifact/` (`package.yaml` and `files/` for a Runner, `models/` for a Gateway); `POST .../builds/{build}/publish` then takes `{target, note?}` and, by the target's product, `{channel, serve?, expected_version?, bind?}` for a Runner (the package is staged on the channel; `bind` is slot to connection name) or `{data_source?}` for a Gateway (the models' own storage connection by default). The answer is the Runner's or the Gateway's own; a refusal comes back in its words.

The `integration` kind (IX20) is a manifest (`package.yaml`), job classes in `src/*.csx` (or a `.csproj` for a compiled project), and test scripts in `tests/`: the check is `dbdatarunner package build` into a scratch folder (no .NET SDK needed for csx), a build runs `package test` first and then writes the package, and a compiled project is built with `dotnet publish` only where the SDK is installed (a server without it says so, `DBD-WS-INTEGRATION-SDK`). The programs are `dbdatarunner` and `dotnet`, found beside the server or on PATH (`RunnerExecutable`, `DotnetExecutable`, `IntegrationSources` and `IntegrationCache` name them and the NuGet feeds otherwise); see [`docs/integrations.md`](../docs/integrations.md).

The console relays an explicit table of these (`ServerProducts.WorkspaceRoutes` in `DbDataKit.Server`: 30 routes, each with its role, checked at the relay against what the person holds, in the route's project too, and by the server again), not the whole API; the publisher's key reference is not relayed.

## Build and test

From this folder: `dotnet build`, then `dotnet test --project tests/DbDataWorkspace.Tests` (xunit v3). The server tests run on SQLite always and on PostgreSQL and SQL Server when `DBDATAKIT_TEST_POSTGRES` and `DBDATAKIT_TEST_SQLSERVER` are set (`testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env dbdatakit)"`); `DBDATAKIT_TEST_REQUIRE_POSTGRES=true` makes a missing engine a failure, not a skip. The module's own convention, layout and retention tests are `WorkspaceStoreTests` in `DbDataKit.State.Tests`.
