# Working in DbDataWorkspace

The Workspace server (`dbdataworkspace`): plan 22a GP31 and decision D8, with plan 27 (more project kinds) and plan 26 (apps) to follow. It is a server on plan 13's shape (the same shape as DbDataStore and DbDataRunner): HTTPS with the shared pairing, announcement and token check, a management API, a state database, and the `server`, `service`, `state`, `secret` and `health` nouns from the shared libraries. What it adds is projects and their git workspaces. **The workspace calls are a library**, `DbDataKit.Authoring` (GP30), because Build serves the same calls for its projects: keep them there and only the host here. Do not copy shared code into this product.

| File | What |
| --- | --- |
| `WorkspaceCli.cs` | the command line: the router, `server run`, the shared nouns |
| `WorkspaceAdminCommand.cs` | `project add\|create\|list` and `workspace list` on the server's directory |
| `WorkspaceServer.cs` | the web host: Kestrel, pairing, announcement to a console, the token check, maintenance, the state API, the management API |
| `WorkspaceManagement.cs` | the management API: info, projects, the shared workspace calls (`DbDataKit.Authoring.WorkspaceApi`), a draft's events, the publisher's key reference; what happens to a draft is recorded in the state module as it happens |
| `WorkspaceProjects.cs` | the projects (kept in the module), made or added, served to the authoring calls (`IAuthoringProjects`) |
| `WorkspaceState.cs`, `WorkspaceSettings.cs`, `WorkspaceSecrets.cs` | the modules the state is built from and how it is opened; the directory and the listen address; the server's own secret store |
| `WorkspaceService.cs` | the `service` noun |

## Rules

- **The state module is `workspace`** (`DbDataKit.State`, `WorkspaceModule` and `WorkspaceStore`), kept per service. Migrations are appended, never edited once released. A change to a draft and its event are written in one transaction; a record that fails to write is logged and never turns a finished git operation into an error.
- **The publisher's key is a reference.** `publisher_key` holds `secret:...`, never a key; a value that is not a reference is refused and is never written to a row or the audit log.
- **Roles are the server's.** Making a project is an administrator's; making and abandoning a workspace needs the author role and only the owner abandons or commits their own; merging needs the approver role. The console's relay is an explicit table (`ServerProducts.Relayable`), and the server checks the role again on every call.
- **A project kind is a row, not a code path.** `WorkspaceModule.ProjectKinds` lists the kinds (`app`); plan 27 adds the others there. The server does not interpret a project's files (builds are GP32 to GP35).
- **A clean break is allowed, never a silent one**: a changed file format fails with a message naming the file.

## Build and test

From this folder: `dotnet build`, then `dotnet test --project tests/DbDataWorkspace.Tests` (xunit v3; if the test host cannot start, run the built executable: `tests/DbDataWorkspace.Tests/bin/Debug/net10.0/DbDataWorkspace.Tests`; `-class` and `-method` select tests).

- The tests start real servers. They run on SQLite always, and on PostgreSQL and SQL Server when `DBDATA_TEST_POSTGRES` and `DBDATA_TEST_SQLSERVER` are set (`testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env dbdatakit)"`); `DBDATA_REQUIRE_POSTGRES=true` makes a missing engine a failure, not a skip.
- The module's convention, layout and retention tests are `WorkspaceStoreTests` in `DbDataKit/tests/DbDataKit.State.Tests` (on the three engines); the library's are `DbDataKit.Authoring.Tests`.
