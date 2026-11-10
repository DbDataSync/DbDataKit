using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataWorkspace.Server;

/// <summary>
/// The Workspace server's state database (plan 22a GP31): <c>state.db</c> in the server's directory, built from the <c>trust</c> module (the server's instance id, the clients paired with it, the pairing codes waiting) and the
/// <c>workspace</c> module (projects, drafts, their history, builds, the publisher's key reference). It is kept per service, so two Workspace servers on one database each have their own copy.
/// </summary>
public static class WorkspaceState
{
    public const string FileName = "state.db";

    public const string ServiceId = "dbdataworkspace";

    public static StateScope Scope { get; } = new(Group: "main", Service: ServiceId);

    /// <summary>The modules the server's state is built from.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [TrustModule.Module, WorkspaceModule.Module];

    /// <summary>The state engine: <c>DBDATAKIT_STATE_WORKSPACE_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>.</summary>
    public static string EngineVariable => SuiteEnvironment.StateEngineVariableOf(WorkspaceSettings.ServiceName);

    /// <summary>The state connection string: <c>DBDATAKIT_STATE_WORKSPACE_CONNECTION</c>, else <c>DBDATAKIT_STATE_CONNECTION</c>.</summary>
    public static string ConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(WorkspaceSettings.ServiceName);

    public static string SettingsFile(string directory) => Path.Combine(directory, "server.yaml");

    /// <summary>
    /// The state settings and where each came from (plan 29b C07): the flags, then <see cref="EngineVariable"/> and <see cref="ConnectionVariable"/>, then the <c>state:</c> section of <c>server.yaml</c> in the directory (read strictly: a setting
    /// that is not one is refused naming the file and the nearest that is), then SQLite in <c>state.db</c>. Nothing is created.
    /// </summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null, string engineFlagName = "--state-engine", string connectionFlagName = "--state-connection") =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(WorkspaceSettings.ServiceName, SettingsFile(directory), engineFlag, engineFlagName, connectionFlag, connectionFlagName, environment));

    /// <summary>The resolver for the references in a server engine's connection string: the server's own secret store, then the environment.</summary>
    public static DbDataKit.Secrets.ISecretResolver Secrets(string directory, Func<string, string?>? environment = null) =>
        new DbDataKit.Secrets.SecretResolverChain([WorkspaceSecrets.Open(directory), new DbDataKit.Secrets.EnvSecretProvider(environment)]);

    /// <param name="settings">Where the state is; null: <see cref="Resolve"/> without flags.</param>
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        Directory.CreateDirectory(directory);
        settings ??= Resolve(directory, environment).Settings;
        return StateOpener.Open(settings, Path.Combine(directory, FileName), Modules, Scope, settings.IsSqlite ? null : Secrets(directory, environment));
    }

    /// <summary>Where the server keeps what pairing needs, for the server and for the <c>server pair</c>, <c>clients</c> and <c>unpair</c> verbs.</summary>
    public static PairingFiles Pairing(string directory, Func<string, string?>? environment = null) => new(Path.Combine(directory, "server.yaml"), Group: Open(directory, environment: environment));
}
