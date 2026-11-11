using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>The names the runner writes to a state database under, which every host of the engine shares (<c>dbdatarunner</c> and the console's embedded runner).</summary>
public static class RunnerNames
{
    /// <summary>The service name recorded on what a <c>dbdatarunner</c> writes to a database it may share (the audit's <c>service_id</c>, the instance's service).</summary>
    public const string ServiceId = "dbdatarunner";

    /// <summary>The service name of the work queue, the run records and the job state: the engine's default, the same for every instance of a group.</summary>
    public const string EngineService = "runner";

    public const string DefaultGroup = "main";

    /// <summary>True for a state on a database server (SQL Server, PostgreSQL), where several runners may form a group; SQLite is one runner's.</summary>
    public static bool IsGroup(StateDatabase state) => !string.Equals(state.Dialect.Engine, "sqlite", StringComparison.OrdinalIgnoreCase);

    /// <summary>The modules the runner needs of a state database besides the ones its host brings (<c>identity</c>, <c>trust</c>, ...): the engine's own.</summary>
    public static IReadOnlyList<StateModule> EngineModules { get; } =
    [
        RunnerModule.Module, RunsModule.Module, ProgressModule.Module, SchedulingModule.Module, DefinitionsModule.Module,
        PayloadStore.Module, AuditModule.Module, NotificationsModule.Module,
    ];
}
