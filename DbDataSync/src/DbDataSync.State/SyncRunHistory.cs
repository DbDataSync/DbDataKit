namespace DbDataSync.State;

/// <summary>
/// Sync's runs as the shared monitoring model (plan 13, M06): <see cref="IRunHistory"/> over the state database, so the health rules of <c>DbDataKit.Monitoring</c> apply to
/// a replication as they do to a Build refresh plan. The job is a replication; its last run is the latest ordinary (Primary) pass of any of its mappings that has started,
/// because that is what "the replication ran" means for a schedule. Reloads, verifications and delete sweeps are on demand and say nothing about being overdue.
/// </summary>
/// <param name="runs">Sync's run history.</param>
/// <param name="lag">How far behind its source a replication is, or null when its readers cannot say; Sync computes it from positions, so it stays Sync's to compute.</param>
public sealed class SyncRunHistory(TaskRunStore runs, Func<string, LagReading?>? lag = null) : IRunHistory
{
    /// <summary>How many recent runs are looked through for the latest one that started; queued runs have not and are skipped.</summary>
    private const int Look = 20;

    public Task<RunRecord?> LastRunAsync(string jobId, CancellationToken ct = default)
    {
        var latest = runs.GetRunHistory(jobId, RunKind.Primary, limit: Look)
            .Where(r => r.StartedAtUtc is not null)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest is null ? null : ToRecord(latest));
    }

    public Task<LagReading?> LagAsync(string jobId, CancellationToken ct = default) => Task.FromResult(lag?.Invoke(jobId));

    /// <summary>One of Sync's runs as the shared record. A run that has not ended and a run Sync has not finished settling are both Running; a cancelled run is Stopped.</summary>
    public static RunRecord ToRecord(TaskRunRecord run) => new(
        run.TaskName,
        run.RunId.ToString(),
        run.StartedAtUtc!.Value,
        run.EndedAtUtc,
        run.Status switch
        {
            RunStatus.Succeeded => RunOutcome.Succeeded,
            RunStatus.Failed => RunOutcome.Failed,
            RunStatus.Cancelled => RunOutcome.Stopped,
            _ => RunOutcome.Running,
        },
        run.RowsWritten,
        run.Status == RunStatus.Failed ? run.MappingName : null,
        run.Status == RunStatus.Failed ? new RunFailure(run.FailureKind ?? "Error", run.ErrorSummary ?? "The run failed.") : null);
}
