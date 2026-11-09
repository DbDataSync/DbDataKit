namespace DbDataWorkspace.Server;

/// <summary>Where a DbDataWorkspace server keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The server's directory: <c>server.yaml</c> (the <c>state:</c> section), the state database, the secret store, its certificate, the projects it made (<c>projects/&lt;id&gt;</c>) and the workspaces (<c>workspaces/&lt;project&gt;/&lt;id&gt;</c>).</param>
/// <param name="Listen">The address it listens on; HTTPS, and a name or address of this machine.</param>
public sealed record WorkspaceSettings(string Directory, Uri Listen)
{
    /// <summary>Where the state database is, when a flag named it (<c>server run --state-engine</c>); null: the environment and the <c>state:</c> section of <c>server.yaml</c> say (<see cref="WorkspaceState.Resolve"/>), else SQLite.</summary>
    public DbDataKit.State.StateSettings? State { get; init; }

    /// <summary>Where each of those settings came from (a flag, the environment, <c>server.yaml</c>, the default), for <c>GET /api/v1/state</c>; null: the server was not started by <c>server run</c> and the files and the environment say.</summary>
    public DbDataKit.State.StateSources? StateSources { get; init; }

    /// <summary>How apps are built (the <c>apps:</c> section of <c>server.yaml</c>), when something other than the file says (tests); null: the file is read for every build.</summary>
    public WorkspaceAppSettings? Apps { get; init; }

    public const string Product = "dbdataworkspace";
    public const string DefaultListen = "https://127.0.0.1:8495";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses).</summary>
    public const string DirectoryVariable = "DBDATAWORKSPACE_SERVER_DIR";

    public string ServerYaml => Path.Combine(Directory, "server.yaml");

    /// <summary>The folder the server made for a project that has none of its own.</summary>
    public string ProjectFolder(string id) => Path.Combine(Directory, "projects", id);

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdataworkspace", "server");
}
