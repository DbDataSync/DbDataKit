# DbDataKit.Authoring

Authoring workspaces for any kind of project (plan 22a, GP30). A **workspace** is one person's draft of a project: a git worktree on a branch `ws/<owner>-<name>` in a folder the server owns, so a draft never changes what the scheduler, the deploys or an app's published version read. The library knows nothing of what a project is; it needs a git repository and a folder.

| Part | What it does |
| --- | --- |
| `Workspaces` | the store: `Create`, `List`, `Find`, `Abandon`, `RootOf`. A record `<id>.json` beside the worktree folder says whose it is (`workspaces/<project>/<id>` and `<id>.json` in the server's directory; unchanged from the Build server that first had it) |
| `WorkspaceApi.Map` | the calls of the management API, the same for every kind: `GET\|POST /api/v1/projects/{project}/workspaces`, `DELETE .../{id}`, `GET .../changes`, `GET .../history`, `POST .../commit`, `POST .../push`, `POST .../merge` |
| `WorkspaceFiles` | the file commands (GP35a): list, read, write, move, delete in a draft's folder, `GET .../{id}/files`, `GET\|PUT\|DELETE .../{id}/files/{path}`, `POST .../{id}/move`. A path is relative with `/`, and is refused when it could leave the folder or touch git: absolute, a drive or `\`, a control character, an empty, `.` or `..` part, any part named `.git`, a place through a symbolic link. Files are UTF-8 text of at most 1 MiB, at most 5000 in a draft |
| `IAuthoringProjects` | what the host tells the calls: which projects it serves (`AuthoringProject`: id, checkout, git remote) and the login for a remote |
| `WorkspaceApiOptions` | the mail domain of a commit's author, and a hook called before a workspace's folder goes (a process that has it open lets go) |

Roles (`ManagementAuthorization`): listing, reading files, changes and history are a viewer's; writing, moving and deleting a file are the owner's with the author role (and tell the host through `WorkspaceApiOptions.Changed` as `file-written`, `file-moved`, `file-deleted`); making and abandoning a workspace, committing and pushing need the author role, and only the owner abandons, commits or pushes their own; merging needs the approver role. A project with a git remote is pushed to and never merged inside the server; one without is merged in the author's name. A merge that conflicts changes nothing and answers 409 with the files.

Users: Build's server (`BuildAuthoringProjects` over its `projects.yaml`), the Workspace server (`DbDataWorkspace`, GP31) for app projects. The tests (`DbDataKit.Authoring.Tests`) run the behaviours Build's own server tests check (plan 13 W02, W08, W09) on a fake app project, so a second kind of project passes the same tests.
