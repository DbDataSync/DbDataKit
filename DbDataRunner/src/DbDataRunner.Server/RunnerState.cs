using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Scheduling;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Server;

/// <summary>
/// The runner's state database (plan 28a R10, plan 29): SQLite in <c>state.db</c> in the server's directory by default, or SQL Server or PostgreSQL when the operator names one
/// (<see cref="RunnerSettings.StateEngineVariable"/>, <see cref="RunnerSettings.StateConnectionVariable"/>; the same two settings the console reads, until the shared <c>state</c> section of plan 29b C07 replaces them). It is built from
/// the modules a runner needs: <c>runner</c> (its instances, capabilities, posture choices and the task projection), <c>runs</c> (the run history and logs), <c>runner_progress</c> (what a task got to), <c>scheduling</c> (the work queue,
/// leases, run locks, job state and pauses), <c>definitions</c> and <c>payloads</c> (the published package versions and their files), <c>audit</c> (who paused, resumed and started what), <c>notifications</c> and <c>trust</c> (the
/// instance id, the clients paired with it and the pairing codes waiting).
/// </summary>
public static class RunnerState
{
    public const string FileName = "state.db";

    /// <summary>The service name recorded on what the runner writes to a database it may share (the audit's <c>service_id</c>).</summary>
    public const string ServiceId = DbDataKit.Runner.RunnerNames.ServiceId;

    /// <summary>The service name of the work queue, the run records and the job state: the engine's default, the same for every instance of the group.</summary>
    public const string EngineService = DbDataKit.Runner.RunnerNames.EngineService;

    public const string DefaultGroup = DbDataKit.Runner.RunnerNames.DefaultGroup;

    /// <summary>The modules the runner's state is built from.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [.. DbDataKit.Runner.RunnerNames.EngineModules, TrustModule.Module];

    public static StateScope ScopeOf(string? group = null) => new(Group: string.IsNullOrWhiteSpace(group) ? DefaultGroup : group, Service: ServiceId);

    public static string SettingsFile(string directory) => Path.Combine(directory, "server.yaml");

    /// <summary>
    /// The state settings and where each came from (plan 29b C07): the flags, then <see cref="RunnerSettings.StateEngineVariable"/> and <see cref="RunnerSettings.StateConnectionVariable"/> (then the install's), then the <c>state:</c> section of
    /// <c>server.yaml</c> in the directory, then SQLite in <c>state.db</c>. The file is read strictly and never created.
    /// </summary>
    public static ResolvedState Resolve(string directory, Func<string, string?>? environment = null, string? engineFlag = null, string engineFlagName = "--state-engine") =>
        StateSettingsResolver.Resolve(SuiteEnvironment.StateInputs(RunnerSettings.ServiceName, SettingsFile(directory), engineFlag, engineFlagName, null, "--state-connection", environment));

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    public static bool IsGroup(StateDatabase state) => DbDataKit.Runner.RunnerNames.IsGroup(state);

    /// <summary>Opens the state (and makes or migrates it). A server engine's connection string may name secrets, looked up in the environment (<c>DBDATAKIT_SECRET_*</c>).</summary>
    public static StateDatabase Open(string directory, StateSettings? settings = null, Func<string, string?>? environment = null, string? group = null, TimeProvider? time = null)
    {
        Directory.CreateDirectory(directory);
        settings ??= new StateSettings();
        var secrets = settings.IsSqlite ? null : Secrets(environment);
        return StateOpener.Open(settings, PathIn(directory), Modules, ScopeOf(group), secrets, $"{ServiceId} {typeof(RunnerState).Assembly.GetName().Version}", time, new StandardGroupInstanceSource());
    }

    public static ISecretResolver Secrets(Func<string, string?>? environment) => new SecretResolverChain([new EnvSecretProvider(environment)]);

    /// <summary>Where the pairing verbs keep what pairing needs: the state, with the instance id in it.</summary>
    public static PairingFiles Pairing(string directory, StateSettings? settings = null, Func<string, string?>? environment = null) =>
        new(Path.Combine(directory, "server.yaml"), Group: Open(directory, settings, environment));
}
