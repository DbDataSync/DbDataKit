using DbDataKit.Scheduling;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataBuild.Cli;

/// <summary>
/// The state database of a directory Build keeps results in (the <c>--results</c> directory of <c>connection refresh</c>: the results of the models' checks, plan 23a FQ09): the first place that names it of the environment
/// (<see cref="EngineVariable"/>, <see cref="ConnectionVariable"/>) and the <c>state:</c> section of <c>server.yaml</c> in the directory (read with the shared <see cref="StateSettingsReader"/>, so a setting that is not one is refused with the file's
/// name and the nearest that is), then SQLite in <c>state.db</c>. The password of a server engine is a secret reference, resolved from the environment. (The Build server that kept its state in such a directory was deleted, plan 33 SC23.)
/// </summary>
internal static class BuildState
{
    /// <summary>The name the state variables are made from.</summary>
    public const string ServiceName = "build";

    /// <summary>The state engine: <c>DBDATAKIT_STATE_BUILD_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>.</summary>
    public static string EngineVariable => SuiteEnvironment.StateEngineVariableOf(ServiceName);

    /// <summary>The state connection string: <c>DBDATAKIT_STATE_BUILD_CONNECTION</c>, else <c>DBDATAKIT_STATE_CONNECTION</c>.</summary>
    public static string ConnectionVariable => SuiteEnvironment.StateConnectionVariableOf(ServiceName);

    public static string SettingsFile(string directory) => Path.Combine(directory, "server.yaml");

    public static string SqlitePath(string directory) => Path.Combine(directory, "state.db");

    /// <summary>The scheduling module, the <c>quality</c> module (the results of the models' checks: install scope) and the modules of the copy of the tracking tables (plan 28c E16), as the database was made; a database made by an earlier structure is refused naming the file.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [SchedulingModule.Module, RunsModule.Module, DbDataBuild.State.BuildRunsModule.Module, .. DbDataKit.Tracking.Build.BuildTrackingState.Modules, QualityModule.Module];

    /// <summary>Install-scoped modules ignore it; the copy's modules are kept for the group <c>main</c>.</summary>
    public static StateScope Scope => DbDataKit.Tracking.Build.BuildTrackingState.Scope;

    /// <summary>The settings and where each came from; raises <see cref="InvalidOperationException"/> naming <c>server.yaml</c> for a state section that is not valid. Nothing is created.</summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null) =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(ServiceName, SettingsFile(directory), null, "--state-engine", null, "--state-connection", environment));

    /// <summary>The resolver for the references in a server engine's connection string: the environment.</summary>
    public static ISecretResolver Secrets(Func<string, string?>? environment = null) => new SecretResolverChain([new EnvSecretProvider(environment)]);

    /// <summary>Opens the state database from <paramref name="settings"/>, or from <see cref="Resolve"/> when none is given.</summary>
    public static StateDatabase Open(string directory, TimeProvider? time = null, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        settings ??= Resolve(directory, environment).Settings;
        Directory.CreateDirectory(directory);
        return StateOpener.Open(settings, SqlitePath(directory), Modules, Scope, settings.IsSqlite ? null : Secrets(environment), time: time);
    }
}
