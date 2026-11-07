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

    /// <summary>The console's settings file, <c>server.yaml</c> in its directory, like the other products': its <c>state:</c> section says where the state database is (<see cref="ConsoleState.Resolve"/>). The console never creates it.</summary>
    public const string SettingsFileName = "server.yaml";

    public string SettingsPath => System.IO.Path.Combine(Directory, SettingsFileName);

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "console");
}

/// <summary>Where this console's people are signed in: the home's address, the client id the home registered this console under (and its secret, read from the variable named), and which app of the home is this console (its roles decide who may sign in here). A session asks the home for the person's roles again every <paramref name="RefreshInterval"/> (default 5 minutes); when the home cannot be asked, the last roles are kept for <paramref name="Grace"/> more (default 10 minutes) and then the person holds no role on any server.</summary>
/// <param name="TrustedCertificate">A certificate file (PEM or DER) to trust for the home's address besides the machine's own trust: the home's certificate itself (a serving layer's self-signed one, which it writes beside its PFX as a .cer) or the CA that issued it. Null: the machine's trust only.</param>
public sealed record IdentityHomeSettings(Uri Authority, string ClientId, string? ClientSecret, string AppId = "console", TimeSpan? RefreshInterval = null, TimeSpan? Grace = null, string? TrustedCertificate = null);

/// <summary>
/// The console's state database, built from modules (plan 29a Q15): <c>identity</c> (the users, their sessions, invitations and one-time values), <c>notifications</c> (the feed and each
/// reader's position) and <c>federation</c> (what it keeps about people who signed in through the identity home). A state database made by the earlier structure, one schema version for the whole
/// database, is refused with a message naming it; set the console up again on a new one.
/// </summary>
public static class ConsoleState
{
    /// <summary>The service name the console records on what it writes to a database it may share (the feed's <c>service_id</c>).</summary>
    public const string ServiceId = ConsoleSettings.Product;

    public static IReadOnlyList<StateModule> Modules { get; } = [IdentityModule.Module, NotificationsModule.Module, DbDataKit.Identity.FederationModule.Module, RunsModule.Module, AuditModule.Module];

    private static string AppliedBy => $"{ConsoleSettings.Product} {typeof(ConsoleState).Assembly.GetName().Version}";

    public const string EngineVariable = "DBDATAKIT_CONSOLE_STATE_ENGINE";
    public const string ConnectionVariable = "DBDATAKIT_CONSOLE_STATE_CONNECTION";

    /// <summary>
    /// The state settings of a console whose directory is <paramref name="directory"/>, from the first place that names them (plan 29b C07): the flags (<c>--state-engine</c> and <c>--state-connection</c>, or whatever <paramref name="engineFlagName"/>
    /// says they are called), then <see cref="EngineVariable"/> and <see cref="ConnectionVariable"/>, then the <c>state:</c> section of <c>server.yaml</c> in the directory, then SQLite. A file with a setting that is not one is refused with
    /// a message naming the file and the nearest setting that is.
    /// </summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null, string engineFlagName = "--state-engine", string connectionFlagName = "--state-connection") =>
        StateSettingsResolver.Resolve(new StateSettingsInputs(
            System.IO.Path.Combine(directory, ConsoleSettings.SettingsFileName), engineFlag, engineFlagName, connectionFlag, connectionFlagName, EngineVariable, ConnectionVariable, environment ?? Environment.GetEnvironmentVariable));

    /// <summary>The state engine the flags or the environment name (the flags win); null when neither does, which is SQLite in the console's directory. Does not read the settings file: <see cref="Resolve"/> does.</summary>
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
        if (settings.IsSqlite) return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), Modules, StateScope.Install, appliedBy: AppliedBy);
        // the password of a server engine is a secret reference: the console's store, then the environment
        var secrets = new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
        return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), Modules, StateScope.Install, secrets, AppliedBy);
    }
}
