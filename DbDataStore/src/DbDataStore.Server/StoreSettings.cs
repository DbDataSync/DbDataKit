namespace DbDataStore.Server;

/// <summary>Where a DbDataStore server keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The server's directory: <c>server.yaml</c> (its instance id and the clients paired with it), the pairing codes and its certificate. Models, their storage and the state database come in the slices that follow.</param>
/// <param name="Listen">The address it listens on; HTTPS, and a name or address of this machine.</param>
public sealed record StoreSettings(string Directory, Uri Listen)
{
    public const string Product = "dbdatastore";
    public const string DefaultListen = "https://127.0.0.1:8490";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses).</summary>
    public const string DirectoryVariable = "DBDATASTORE_SERVER_DIR";

    public string ServerYaml => Path.Combine(Directory, "server.yaml");

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatastore", "server");
}
