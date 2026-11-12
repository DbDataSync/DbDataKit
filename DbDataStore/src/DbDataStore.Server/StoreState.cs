using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>
/// The DbDataStore server's state database (plan 29a Q18): <c>state.db</c> in the server's directory, built from the <c>trust</c> module. The server's instance id, the clients paired with it and the
/// pairing codes waiting are kept there, not in <c>server.yaml</c>. Pairings made before this were a list in <c>server.yaml</c>; they are not carried over (a clean break, plan 13 section 3): a server
/// whose file still lists them does not start, and says which file and what to do.
/// </summary>
public static class StoreState
{
    public const string FileName = "state.db";

    public const string ServiceId = "dbdatastore";

    public static StateScope Scope { get; } = new(Group: "main", Service: ServiceId);

    /// <summary>The modules the server's state is built from.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [TrustModule.Module, NotificationsModule.Module];

    /// <summary>The state engine: <c>DBDATAKIT_STATE_STORE_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>.</summary>
    public static string EngineVariable => SuiteEnvironment.StateEngineVariableOf(StoreSettings.ServiceName);

    /// <summary>The state connection string: <c>DBDATAKIT_STATE_STORE_CONNECTION</c>, else <c>DBDATAKIT_STATE_CONNECTION</c>.</summary>
    public static string ConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(StoreSettings.ServiceName);

    public static string SettingsFile(string directory) => Path.Combine(directory, "server.yaml");

    /// <summary>
    /// The state settings and where each came from (plan 29b C07): the flags, then <see cref="EngineVariable"/> and <see cref="ConnectionVariable"/>, then the <c>state:</c> section of <c>server.yaml</c> in the directory (read strictly: a setting
    /// that is not one is refused naming the file and the nearest that is), then SQLite in <c>state.db</c>. Nothing is created.
    /// </summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null, string engineFlagName = "--state-engine", string connectionFlagName = "--state-connection") =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(StoreSettings.ServiceName, SettingsFile(directory), engineFlag, engineFlagName, connectionFlag, connectionFlagName, environment));

    /// <summary>The resolver for the references in a server engine's connection string: the server's own secret store, then the environment.</summary>
    public static DbDataKit.Secrets.ISecretResolver Secrets(string directory, Func<string, string?>? environment = null) =>
        new DbDataKit.Secrets.SecretResolverChain([StorePublisher.OpenSecrets(directory), new DbDataKit.Secrets.EnvSecretProvider(environment)]);

    /// <param name="settings">Where the state is; null: <see cref="Resolve"/> without flags.</param>
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        var yaml = Path.Combine(directory, "server.yaml");
        if (File.Exists(yaml) && File.ReadLines(yaml).Any(line => line.StartsWith(PairedClientsFile.Key + ":", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{yaml} lists paired clients ({PairedClientsFile.Key}:), which a DbDataStore server now keeps in its state database ({Path.Combine(directory, FileName)}). " +
                $"Pairings are not converted: remove the {PairedClientsFile.Key} list from {yaml}, then pair each client again with 'dbdatastore server pair'.");
        Directory.CreateDirectory(directory);
        settings ??= Resolve(directory, environment).Settings;
        return StateOpener.Open(settings, Path.Combine(directory, FileName), Modules, Scope, settings.IsSqlite ? null : Secrets(directory, environment));
    }

    /// <summary>Where the server keeps what pairing needs, for the server and for the <c>server pair</c>, <c>clients</c> and <c>unpair</c> verbs.</summary>
    public static PairingFiles Pairing(string directory, Func<string, string?>? environment = null) => new(Path.Combine(directory, "server.yaml"), Group: Open(directory, environment: environment));
}
