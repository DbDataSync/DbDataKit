using DbDataKit.State;

namespace DbDataConsole.Server;

/// <summary>Where a console keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The console's directory: <c>state.db</c> (users, sessions, invitations, notifications), `instances.yaml` (the servers it manages), `secrets/` (the key it signs for them with), its certificate, and <c>FIRST-RUN.txt</c> until the first administrator exists.</param>
/// <param name="Listen">The address it listens on; HTTPS. A passkey is bound to the host name, so a name such as <c>localhost</c> works and a bare IP address does not.</param>
/// <param name="PublicUrl">The address people open it at, when that is not the address it listens on (a container listens on every address; a proxy answers on a name of its own). A passkey is bound to this name and port, and an invitation link is made from it, so it has to be the one in the browser's address bar. Null: the address it listens on, or <c>localhost</c> on the same port where that is every address.</param>
public sealed record ConsoleSettings(string Directory, Uri Listen, Uri? PublicUrl = null)
{
    /// <summary>The address a browser opens, and the origin a passkey is bound to.</summary>
    public Uri Public => PublicUrl ?? (IsEveryAddress(Listen.Host) ? new UriBuilder(Listen) { Host = "localhost" }.Uri : Listen);

    private static bool IsEveryAddress(string host) => host is "0.0.0.0" or "[::]" or "::" or "*" or "+";

    public const string Product = "dbdatakit";
    public const string DefaultListen = "https://localhost:8480";

    /// <summary>The variable that names the console's directory for every command, so <c>docker exec</c> reaches the files the server uses.</summary>
    public const string DirectoryVariable = "DBDATAKIT_CONSOLE_DIR";

    public string InstancesPath => System.IO.Path.Combine(Directory, "instances.yaml");

    public string StatePath => System.IO.Path.Combine(Directory, "state.db");

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "console");
}

/// <summary>The console's state database: the users, their sessions and invitations, and the notification feed.</summary>
public static class ConsoleState
{
    public static StateSchema Schema { get; } = new([IdentitySchema.Accounts, IdentitySchema.Invites, NotificationSchema.Feed]);

    public static StateDatabase Open(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        return new StateDatabase(System.IO.Path.Combine(directory, "state.db"), Schema);
    }
}
