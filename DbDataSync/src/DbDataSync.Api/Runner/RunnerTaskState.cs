using DbDataKit.Runner.Client;

namespace DbDataSync.Api.Runner;

/// <summary>
/// The runner's own row of a replication's drain, as the status card shows it next to this server's worker (plan 28c E12g): whether a drain is up, how many are queued, whether the runner holds the task paused, when it is next due and its
/// last run. <see cref="Reachable"/> false (with <see cref="Error"/>) says the runner could not be asked, which is not the same as "nothing is running there".
/// </summary>
public sealed record RunnerTaskState(
    bool Reachable,
    bool Running,
    int RunningCount,
    int Queued,
    bool Paused,
    string? PausedBy,
    DateTimeOffset? NextDueUtc,
    ClientLastRun? LastRun,
    string? Error)
{
    public static RunnerTaskState From(ClientTaskStatus status) =>
        new(true, status.Running > 0, status.Running, status.Queued, status.Paused, status.PausedBy, status.NextDue, status.LastRun, null);

    /// <summary>The runner has no row of the task (nothing published for it yet).</summary>
    public static RunnerTaskState NoTask { get; } = new(true, false, 0, 0, false, null, null, null, null);

    public static RunnerTaskState Unreachable(string error) => new(false, false, 0, 0, false, null, null, null, error);
}
