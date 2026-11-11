# DbDataWorkspace

The server where people (and, later, agents) author projects in git workspaces (plan 22a GP31, decision D8; plan 27). A project is a git repository of some kind (an app today; plan 27 adds agent, integration, skill and knowledge projects); a **workspace** is one person's draft of it, a git worktree on its own branch in a folder the server owns, so a draft never changes what is published. The server lists, creates and abandons workspaces, shows their changes, commits, pushes and merges them (the shared library `DbDataKit.Authoring`, which Build serves its workspaces with too), and keeps a record of what was done in its state database. It has no web page of its own: a console pairs with it and shows its screens (Servers, then the Workspace server, then Workspaces).
**Built:** editing a draft's files, commit, push and merge (GP35a); an app project starts as the app template (GP32); builds from a vendored package store, a read-only preview and publishing to a paired Gateway (GP35b, below). **Not built:** a preview with live data (the semantic route and the apps host's preview channel), a git remote for a project. The `build` table and the publisher's key reference are in the state module; the builds write the one and publishing reads the other.

```
dbdataworkspace server run [--repo <server directory>] [--listen https://127.0.0.1:8471] [--state-engine sqlite|mssql|postgres --state-connection <connection string>]
dbdataworkspace server pair --max-role <roles> [--repo <server directory>]
dbdataworkspace server clients | unpair <client> [--repo <server directory>]
dbdataworkspace project create <id> [--title <text>] [--kind app|build|store|sync] [--repo <server directory>]   # a git repository in projects/<id> with a first commit
dbdataworkspace project add <id> --path <repository folder> [--title <text>] [--kind app|build|store|sync] [--repo <server directory>]   # host a repository that is already on this machine, in place
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

The console relays an explicit table of these (`ServerProducts.WorkspaceRoutes` in `DbDataKit.Server`: 30 routes, each with its role, checked at the relay against what the person holds, in the route's project too, and by the server again), not the whole API; the publisher's key reference is not relayed.

## Build and test

From this folder: `dotnet build`, then `dotnet test --project tests/DbDataWorkspace.Tests` (xunit v3). The server tests run on SQLite always and on PostgreSQL and SQL Server when `DBDATAKIT_TEST_POSTGRES` and `DBDATAKIT_TEST_SQLSERVER` are set (`testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env dbdatakit)"`); `DBDATAKIT_TEST_REQUIRE_POSTGRES=true` makes a missing engine a failure, not a skip. The module's own convention, layout and retention tests are `WorkspaceStoreTests` in `DbDataKit.State.Tests`.
