using DbDataKit.State;

namespace DbDataConsole.Server;

/// <summary>Where a console keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The console's directory: <c>state.db</c> (users, sessions, invitations, notifications), `instances.yaml` (the servers it manages), `secrets/` (the key it signs for them with), its certificate, and <c>FIRST-RUN.txt</c> until the first administrator exists.</param>
/// <param name="Listen">The address it listens on; HTTPS. A passkey is bound to the host name, so a name such as <c>localhost</c> works and a bare IP address does not.</param>
/// <param name="AnnounceListen">A private address (plain HTTP) where servers on the network the console shares with them announce themselves, to wait for an administrator to confirm them; null: none. It serves nothing else, and must not be published outside that network.</param>
/// <param name="State">The engine of the state database; null is SQLite in <see cref="Directory"/>. A server engine is a database of its own (the Proxy uses another), named by a connection string whose password is a secret reference.</param>
/// <param name="Home">The identity home this console signs people in through, when it keeps no users of its own (plan 13a U14); null: the console is its own home and signs people in itself.</param>
/// <param name="PublicUrl">The address people open it at, when that is not the address it listens on (a container listens on every address; a proxy answers on a name of its own). A passkey is bound to this name and port, and an invitation link is made from it, so it has to be the one in the browser's address bar. Null: the address it listens on, or <c>localhost</c> on the same port where that is every address.</param>
public sealed record ConsoleSettings(string Directory, Uri Listen, Uri? PublicUrl = null, Uri? AnnounceListen = null, StateSettings? State = null, IdentityHomeSettings? Home = null)
{
    /// <summary>The address a browser opens, and the origin a passkey is bound to.</summary>
    public Uri Public => PublicUrl ?? (IsEveryAddress(Listen.Host) ? new UriBuilder(Listen) { Host = "localhost" }.Uri : Listen);

    private static bool IsEveryAddress(string host) => host is "0.0.0.0" or "[::]" or "::" or "*" or "+";

    public const string Product = "dbdatakit";
    public const string DefaultListen = "https://localhost:8480";

    /// <summary>The variable that names the console's directory for every command, so <c>docker exec</c> reaches the files the server uses.</summary>
    public const string DirectoryVariable = "DBDATAKIT_CONSOLE_DIR";

    public string InstancesPath => System.IO.Path.Combine(Directory, "instances.yaml");

    public string PendingPath => System.IO.Path.Combine(Directory, "pending.json");

    public string StatePath => System.IO.Path.Combine(Directory, "state.db");

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "console");
}

/// <summary>Where this console's people are signed in: the home's address, the client id the home registered this console under (and its secret, read from the variable named), and which app of the home is this console (its roles decide who may sign in here).</summary>
public sealed record IdentityHomeSettings(Uri Authority, string ClientId, string? ClientSecret, string AppId = "console");

/// <summary>The console's state database: the users, their sessions and invitations, and the notification feed.</summary>
public static class ConsoleState
{
    public static StateSchema Schema { get; } = new([IdentitySchema.Accounts, IdentitySchema.Invites, NotificationSchema.Feed, DbDataKit.Identity.FederatedSchema.Roles]);

    public const string EngineVariable = "DBDATAKIT_CONSOLE_STATE_ENGINE";
    public const string ConnectionVariable = "DBDATAKIT_CONSOLE_STATE_CONNECTION";

    /// <summary>The state engine the flags or the environment name (the flags win); null when neither does, which is SQLite in the console's directory.</summary>
    public static StateSettings? SettingsFrom(Func<string, string?>? environment, string? engineFlag = null, string? connectionFlag = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var engine = engineFlag ?? environment(EngineVariable);
        var connection = connectionFlag ?? environment(ConnectionVariable);
        return string.IsNullOrWhiteSpace(engine) && string.IsNullOrWhiteSpace(connection) ? null : new StateSettings(engine, connection);
    }

    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        settings ??= new StateSettings();
        if (settings.IsSqlite) return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), Schema);
        // the password of a server engine is a secret reference: the console's store, then the environment
        var secrets = new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
        return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), Schema, secrets);
    }
}
