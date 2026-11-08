# DbDataKit.Config.Git

Git for a product's configuration directory: `GitCommitService` (auto-commit right after a configuration write reaches disk, so there is no staging step; history, diff and the author of each change), `GitAuthor` (who changed it, and through which console), worktrees and remotes for Build's authoring workspaces (`GitWorktrees`, `GitWorkspaceOperations`, `GitRemote`, `GitPullRequestLink`).

## The layout every product keeps

A server's settings are YAML files in the `config` folder of its data directory, a folder a git repository can track:

```
<data>/config/
  server.yaml                   the server's own settings (listen, TLS, state store, secrets, the pairedClients list), schemaVersion
  connections/<name>.yaml       this server's connections (connection/1); the file name is the connection's name
  <product>/                    files that are only the product's (the Proxy's are proxy/auth.yaml and proxy/users.yaml)
```

Rules: one strict YAML format for every product (`DbDataKit.Config`); a file in an older format, an `appsettings.json` where `server.yaml` belongs, or a file with no `schemaVersion` is refused with a message that names the file, never converted; secrets are references to the store, which is kept outside the history. The same `connection/1` file is a Sync connection, a Proxy data source and a Build connection, so a console can copy one between servers. Each product's own README has its exact files.
