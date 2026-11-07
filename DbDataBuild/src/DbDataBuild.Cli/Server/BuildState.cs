using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Where the Build server's state database is (plan 29b C07): the first place that names it of the flags (<c>--state-engine</c>, <c>--state-connection</c> of <c>server run</c>, <c>--engine</c> of the <c>state</c> verbs), the environment
/// (<see cref="EngineVariable"/>, <see cref="ConnectionVariable"/>), the <c>state:</c> section of <c>server.yaml</c> in the server's directory (read with the shared <see cref="StateSettingsReader"/>, so a setting that is not one is refused with the file's
/// name and the nearest that is), then SQLite in <c>state.db</c>. The password of a server engine is a secret reference: the server's own store, then the environment.
/// </summary>
internal static class BuildState
{
    public const string EngineVariable = "DBDATABUILD_STATE_ENGINE";
    public const string ConnectionVariable = "DBDATABUILD_STATE_CONNECTION";

    public static string SettingsFile(string serverDirectory) => Path.Combine(serverDirectory, "server.yaml");

    public static string SqlitePath(string serverDirectory) => Path.Combine(serverDirectory, "state.db");

    /// <summary>The settings and where each came from; raises <see cref="InvalidOperationException"/> naming <c>server.yaml</c> for a state section that is not valid. Nothing is created.</summary>
    public static ResolvedState Resolve(string serverDirectory, Func<string, string?>? environment = null, string? engineFlag = null, string? connectionFlag = null, string engineFlagName = "--state-engine", string connectionFlagName = "--state-connection") =>
        StateSettingsResolver.Resolve(new StateSettingsInputs(SettingsFile(serverDirectory), engineFlag, engineFlagName, connectionFlag, connectionFlagName, EngineVariable, ConnectionVariable, environment ?? Environment.GetEnvironmentVariable));

    /// <summary>The resolver for the references in a server engine's connection string: the server's secret store, then the environment.</summary>
    public static ISecretResolver Secrets(string serverDirectory, Func<string, string?>? environment = null) =>
        new SecretResolverChain([ServerSecrets.Open(serverDirectory, environment), new EnvSecretProvider(environment)]);

    /// <summary>Opens the state database from <paramref name="settings"/>, or from <see cref="Resolve"/> (no flags) when none is given.</summary>
    public static StateDatabase Open(string serverDirectory, TimeProvider? time = null, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        settings ??= Resolve(serverDirectory, environment).Settings;
        Directory.CreateDirectory(serverDirectory);
        var secrets = settings.IsSqlite ? null : Secrets(serverDirectory, environment);
        return StateOpener.Open(settings, SqlitePath(serverDirectory), BuildSchedulerSchema.Modules, StateScope.Install, secrets, time: time);
    }
}
