using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>Where a console keeps what is its own, and where it listens.</summary>
/// <param name="Directory">The console's directory: <c>state.db</c> (users, sessions, invitations, notifications), `instances.yaml` (the servers it manages), `secrets/` (the key it signs for them with), its certificate, and <c>FIRST-RUN.txt</c> until the first administrator exists.</param>
/// <param name="Listen">The address it listens on; HTTPS. A passkey is bound to the host name, so a name such as <c>localhost</c> works and a bare IP address does not.</param>
/// <param name="AnnounceListen">A private address (plain HTTP) where servers on the network the console shares with them announce themselves, to wait for an administrator to confirm them; null: none. It serves nothing else, and must not be published outside that network.</param>
/// <param name="State">The engine of the state database; null is SQLite in <see cref="Directory"/>. A server engine is a database of its own (the Gateway uses another), named by a connection string whose password is a secret reference.</param>
/// <param name="Home">The identity home this console signs people in through, when it keeps no users of its own (plan 13a U14); null: the console is its own home and signs people in itself.</param>
/// <param name="PublicUrl">The address people open it at, when that is not the address it listens on (a container listens on every address; a gateway answers on a name of its own). A passkey is bound to this name and port, and an invitation link is made from it, so it has to be the one in the browser's address bar. Null: the address it listens on, or <c>localhost</c> on the same port where that is every address.</param>
public sealed record ConsoleSettings(string Directory, Uri Listen, Uri? PublicUrl = null, Uri? AnnounceListen = null, StateSettings? State = null, IdentityHomeSettings? Home = null)
{
    /// <summary>Where each state setting came from (a flag, the environment, <c>settings.yaml</c>, the default), for <c>GET /api/v1/state</c>; null: not said, the file and the environment are read.</summary>
    public StateSources? StateSources { get; init; }

    /// <summary>The runner embedded in the console (plan 28 section 6a, plan 28a R20); null: the <c>runner:</c> section of <c>settings.yaml</c> in the directory says (<see cref="ConsoleRunnerSettings.Load"/>), and without one the runner is off.</summary>
    public ConsoleRunnerSettings? Runner { get; init; }

    /// <summary>The runner settings in effect: the given ones, else the settings file's.</summary>
    public ConsoleRunnerSettings RunnerOrFile() => Runner ?? ConsoleRunnerSettings.Load(Directory);

    /// <summary>The address a browser opens, and the origin a passkey is bound to.</summary>
    public Uri Public => PublicUrl ?? (IsEveryAddress(Listen.Host) ? new UriBuilder(Listen) { Host = "localhost" }.Uri : Listen);

    private static bool IsEveryAddress(string host) => host is "0.0.0.0" or "[::]" or "::" or "*" or "+";

    public const string Product = "dbdatakit";
    public const string DefaultListen = "https://localhost:8480";

    /// <summary>The service's name in the suite's folders and variables (<see cref="SuiteEnvironment"/>): the DbDataKit service itself is <c>server</c> (plan 33 D9).</summary>
    public const string ServiceName = "server";

    /// <summary>The variable that names the console's directory for every command, so <c>docker exec</c> reaches the files the server uses: <c>DBDATAKIT_DATA_SERVER</c>, or the folder <c>server</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => SuiteEnvironment.FolderVariable(ServiceName);

    public string InstancesPath => System.IO.Path.Combine(Directory, "instances.yaml");

    public string PendingPath => System.IO.Path.Combine(Directory, "pending.json");

    public string StatePath => System.IO.Path.Combine(Directory, "state.db");

    /// <summary>The console's settings file, <c>settings.yaml</c> in its directory: its <c>state:</c> section says where the state database is (<see cref="ConsoleState.Resolve"/>). The console never creates it.</summary>
    public const string SettingsFileName = "settings.yaml";

    public string SettingsPath => System.IO.Path.Combine(Directory, SettingsFileName);

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/> or the folder <c>server</c> under <c>DBDATAKIT_DATA</c>, else a folder under the user's local application data (an interactive run; the registered service names its folder, <see cref="SuiteEnvironment.ResolveServiceFolder"/>).</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        SuiteEnvironment.ResolveDataFolder(ServiceName, environment, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatakit", "console"));
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

    /// <summary>
    /// The modules of a console whose embedded runner is <paramref name="runner"/>: the console's own, and when the runner is enabled the runner's (<c>runner</c>, <c>runner_progress</c>, <c>scheduling</c>, <c>definitions</c>, <c>payloads</c>, and
    /// <c>trust</c> for the publishing settings). A console that does not enable the runner has none of them, so its state gains no table.
    /// </summary>
    public static IReadOnlyList<StateModule> ModulesFor(ConsoleRunnerSettings? runner)
    {
        if (runner is not { Enabled: true }) return Modules;
        var names = Modules.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return [.. Modules, .. DbDataKit.Runner.RunnerNames.EngineModules.Append(DbDataKit.Web.Trust.TrustModule.Module).Where(m => names.Add(m.Name))];
    }

    /// <summary>The scope of the console's state: the install, and with the embedded runner the group <c>console</c> (its modules <c>runner</c>, <c>runner_progress</c> and <c>definitions</c> are per group; the console's own are per install and do not change).</summary>
    public static StateScope ScopeFor(ConsoleRunnerSettings? runner) =>
        runner is { Enabled: true } ? new StateScope(Group: ConsoleRunnerSettings.Group, Service: ServiceId) : StateScope.Install;

    /// <summary>The embedded runner's settings as the console's directory says them (off when there is no <c>runner:</c> section).</summary>
    public static ConsoleRunnerSettings RunnerIn(string directory) => ConsoleRunnerSettings.Load(directory);

    private static string AppliedBy => $"{ConsoleSettings.Product} {typeof(ConsoleState).Assembly.GetName().Version}";

    /// <summary>The state engine of the service: <c>DBDATAKIT_STATE_SERVER_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>.</summary>
    public static string EngineVariable => SuiteEnvironment.StateEngineVariableOf(ConsoleSettings.ServiceName);

    /// <summary>The state connection string of the service: <c>DBDATAKIT_STATE_SERVER_CONNECTION</c>, else <c>DBDATAKIT_STATE_CONNECTION</c>.</summary>
    public static string ConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(ConsoleSettings.ServiceName);

    /// <summary>The console's settings file: the <c>state:</c> section of <c>settings.yaml</c> in its directory (the same section every product reads, plan 29b M7), which <c>state move</c> and <c>state init</c> write. The console never creates it itself.</summary>
    public static string SettingsFileIn(string directory) => System.IO.Path.Combine(directory, ConsoleSettings.SettingsFileName);

    /// <summary>
    /// The state settings of a console whose directory is <paramref name="directory"/>, from the first place that names each setting (plan 29b C07): the flags (<c>--state-engine</c> and <c>--state-connection</c>, or whatever <paramref name="engineFlagName"/>
    /// says they are called), then <see cref="EngineVariable"/> and <see cref="ConnectionVariable"/>, then the <c>state:</c> section of <c>settings.yaml</c> in the directory, then SQLite. A file with a setting that is not one is refused with
    /// a message naming the file and the nearest setting that is.
    /// </summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null, string engineFlagName = "--state-engine", string connectionFlagName = "--state-connection") =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(ConsoleSettings.ServiceName, SettingsFileIn(directory), engineFlag, engineFlagName, connectionFlag, connectionFlagName, environment));

    /// <summary>The state settings the flags, the environment and (when <paramref name="directory"/> is given) the settings file name, as <see cref="Resolve"/> finds them; null when nothing names any, which is SQLite in the console's directory.</summary>
    public static StateSettings? SettingsFrom(Func<string, string?>? environment, string? engineFlag = null, string? connectionFlag = null, string? directory = null)
    {
        if (directory is not null)
        {
            var resolved = Resolve(directory, environment, engineFlag, connectionFlag);
            return resolved.Sources == StateSources.Defaults ? null : resolved.Settings;
        }

        return SuiteEnvironment.ResolveState(ConsoleSettings.ServiceName, environment, engineFlag, connectionFlag);
    }

    /// <param name="runner">The embedded runner's settings; null reads the <c>runner:</c> section of <c>settings.yaml</c> in <paramref name="directory"/>.</param>
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null, ConsoleRunnerSettings? runner = null)
    {
        System.IO.Directory.CreateDirectory(directory);
        settings ??= new StateSettings();
        runner ??= RunnerIn(directory);
        var modules = ModulesFor(runner);
        var scope = ScopeFor(runner);
        if (settings.IsSqlite) return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), modules, scope, appliedBy: AppliedBy);
        // the password of a server engine is a secret reference: the console's store, then the environment
        var secrets = new DbDataKit.Secrets.SecretResolverChain([ConsoleSecrets.Open(directory, environment), new DbDataKit.Secrets.EnvSecretProvider(environment)]);
        return StateOpener.Open(settings, System.IO.Path.Combine(directory, "state.db"), modules, scope, secrets, AppliedBy);
    }
}
