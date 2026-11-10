namespace DbDataStore.Server;

/// <summary>Where a DbDataStore server keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The server's directory: <c>server.yaml</c> (its instance id and the clients paired with it), the pairing codes and its certificate. Models, their storage and the state database come in the slices that follow.</param>
/// <param name="Listen">The address it listens on; HTTPS, and a name or address of this machine.</param>
public sealed record StoreSettings(string Directory, Uri Listen)
{
    /// <summary>Where the state database is, when a flag named it (<c>server run --state-engine</c>); null: the environment and the <c>state:</c> section of <c>server.yaml</c> say (<see cref="StoreState.Resolve"/>), else SQLite.</summary>
    public DbDataKit.State.StateSettings? State { get; init; }

    /// <summary>Where each of those settings came from (a flag, the environment, <c>server.yaml</c>, the default), for <c>GET /api/v1/state</c>; null: the server was not started by <c>server run</c> and the files and the environment say.</summary>
    public DbDataKit.State.StateSources? StateSources { get; init; }

    public const string Product = "dbdatastore";
    public const string DefaultListen = "https://127.0.0.1:8490";

    /// <summary>The service's name in the suite's folders and variables (<see cref="DbDataKit.State.SuiteEnvironment"/>): <c>store</c>.</summary>
    public const string ServiceName = "store";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses): <c>DBDATAKIT_DATA_STORE</c>, or the folder <c>store</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => DbDataKit.State.SuiteEnvironment.FolderVariable(ServiceName);

    public string ServerYaml => Path.Combine(Directory, "server.yaml");

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/> or the folder under <c>DBDATAKIT_DATA</c>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        DbDataKit.State.SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatastore", "server"));
}
