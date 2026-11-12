using DbDataKit.Authoring.Server;

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

    /// <summary>The program that checks a Build project (<c>dbdatabuild</c>), when something other than "beside the server or on PATH" says (tests); null: found the usual way.</summary>
    public string? BuildExecutable { get; init; }

    /// <summary>The path of <c>dbdatasync</c> when it is not beside the server or on PATH (a test, or a machine with Sync installed elsewhere); a Sync project is checked and published with it.</summary>
    public string? SyncExecutable { get; init; }

    /// <summary>The platform the runners run on (<c>linux-x64</c> or <c>win-x64</c>) when it is not the one of this machine: a Sync project's package is made with the driver libraries restored for it (<c>dbdatasync package publish --platform</c>); null: the machine's own.</summary>
    public string? SyncPlatform { get; init; }

    /// <summary>The path of <c>dbdatarunner</c> when it is not beside the server or on PATH: an integration project (csx) is checked, tested and built with its <c>package build</c> and <c>package test</c>, which need only the .NET runtime.</summary>
    public string? RunnerExecutable { get; init; }

    /// <summary>The path of <c>dotnet</c> when it is not on PATH: only a compiled integration project is built with it, and only where the SDK is installed.</summary>
    public string? DotnetExecutable { get; init; }

    /// <summary>Where a csx integration finds NuGet packages: v3 index addresses or folders in the global-packages layout (an offline feed); null: nuget.org.</summary>
    public IReadOnlyList<string>? IntegrationSources { get; init; }

    /// <summary>Where the packages of csx integrations are kept; null: <c>integration-cache</c> in the server's directory.</summary>
    public string? IntegrationCache { get; init; }

    public const string Product = "dbdataworkspace";
    public const string DefaultListen = "https://127.0.0.1:8471";

    /// <summary>The service's name in the suite's folders and variables (<see cref="DbDataKit.State.SuiteEnvironment"/>): <c>workspace</c>.</summary>
    public const string ServiceName = "workspace";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses): <c>DBDATAKIT_DATA_WORKSPACE</c>, or the folder <c>workspace</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => DbDataKit.State.SuiteEnvironment.FolderVariable(ServiceName);

    public string ServerYaml => Path.Combine(Directory, "server.yaml");

    /// <summary>The folder the server made for a project that has none of its own.</summary>
    public string ProjectFolder(string id) => Path.Combine(Directory, "projects", id);

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/> or the folder under <c>DBDATAKIT_DATA</c>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        DbDataKit.State.SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdataworkspace", "server"));
}
