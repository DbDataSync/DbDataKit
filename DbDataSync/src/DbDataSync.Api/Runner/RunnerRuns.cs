using DbDataKit.Runner.Client;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>What the Sync API reads of a runner's runs of a replication's task (plan 28c E13): the runs, one run and its log, and the steps a task's runs recorded (the pass records of <see cref="PassRecord"/> and the polling checks).</summary>
public interface IRunnerRuns
{
    /// <summary>The runs of <paramref name="task"/>, or of every task of the link's package and channel when it is null, newest first.</summary>
    Task<IReadOnlyList<ClientRun>> RunsAsync(string? task, int limit, DateTimeOffset? before, string? outcome, CancellationToken ct);

    Task<IReadOnlyList<ClientStep>> StepsAsync(string task, string step, int limit, DateTimeOffset? before, string? key, CancellationToken ct);
}

/// <summary>The runner could not be asked (not linked, not reachable, or it refused): said as such, never as a history that is empty.</summary>
public sealed class RunnerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A page of a runner-owned replication's history and, when the runner could not be asked, why.</summary>
public sealed record RunnerRunPage(IReadOnlyList<TaskRunRecord> Runs, string? Unavailable)
{
    public static readonly RunnerRunPage None = new([], null);
}

/// <summary>
/// The history of a replication the runner owns, read from the runner (plan 28c E13). A drain is one run of the runner and each pass of it is a unit that wrote a <see cref="PassRecord"/>, so the rows are the pass records (what
/// <c>run_detail</c> holds for the API's own path) and, beside them, the runner's runs that explain themselves no other way: a drain that is still going (its log is what the live view shows) and a drain that ended badly with no
/// pass to say why (a crash, a time limit, a definition that would not load). A drain whose failure a failed pass record already names is not listed twice. The kinds are the passes' own; the runner's run is a <c>Primary</c> one.
/// </summary>
public sealed class RunnerRunSource(RunnerAccess runner) : IRunnerRunHistory
{
    bool IRunnerRunHistory.Owns(string jobId) => RunnerOwns(jobId);

    /// <summary>Whether the replication is the runner's (a file that does not read is refused naming the file, as everywhere the owner is read).</summary>
    public bool RunnerOwns(string replication) => runner.File.OwnerOf(replication) == DbDataKit.State.TaskOwners.Runner;

    private IRunnerRuns Require() => runner.OpenRuns() ?? throw new RunnerUnavailableException(runner.Link is null
        ? $"No runner is linked, so the runs of a replication it owns cannot be read ({runner.File.Path})."
        : "The linked runner cannot be asked for its runs.");

    /// <summary>The page of the replication's runs older than <paramref name="before"/> (by start), newest first, at most <paramref name="limit"/> of them; <see cref="RunnerRunPage.Unavailable"/> says why there are none to show when the runner could not be asked.</summary>
    public async Task<RunnerRunPage> PageAsync(string replication, int limit, DateTimeOffset? before, CancellationToken ct)
    {
        try
        {
            var runs = Require();
            var task = RunnerAccess.TaskOf(replication);
            var steps = await runs.StepsAsync(task, PassRecordText.StepName, limit, before, null, ct).ConfigureAwait(false);
            var passes = steps.Select(s => PassRecordText.TryParse(s.Result)).OfType<PassRecord>().ToList();
            var drains = await runs.RunsAsync(task, limit, before, null, ct).ConfigureAwait(false);
            var rows = passes.Select(PassRecordText.ToRun).ToList();
            foreach (var drain in drains)
                if (DrainRow(drain, replication, passes) is { } row)
                    rows.Add(row);
            return new RunnerRunPage([.. rows.OrderByDescending(r => r.StartedAtUtc).ThenByDescending(r => r.RunId).Take(limit)], null);
        }
        catch (RunnerClientException ex)
        {
            return new RunnerRunPage([], ex.Message);
        }
        catch (RunnerUnavailableException ex)
        {
            return new RunnerRunPage([], ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new RunnerRunPage([], ex.Message);
        }
    }

    /// <summary>The runner's run as a row of the history: a drain that is going or did not succeed, unless a failed pass of it already says what went wrong.</summary>
    private static TaskRunRecord? DrainRow(ClientRun drain, string replication, IReadOnlyList<PassRecord> passes)
    {
        if (drain.Outcome == "succeeded") return null;
        if (drain.Outcome == "failed" && passes.Any(p => p.DrainRunId == drain.Id && p.Status == nameof(RunStatus.Failed))) return null;
        return ToRun(drain, replication);
    }

    /// <summary>A drain as a run of Sync's: its status from the runner's outcome, the item it failed on as the mapping (the unit key is the mapping and a claim, <c>orders#1a2b3c4d</c>), and the runner's classification of the failure.</summary>
    public static TaskRunRecord ToRun(ClientRun drain, string replication) => new(
        drain.Id, replication, Pid: null,
        drain.Outcome switch
        {
            "running" => RunStatus.Running,
            "succeeded" => RunStatus.Succeeded,
            "stopped" => RunStatus.Cancelled,
            _ => RunStatus.Failed,
        },
        RunKind.Primary, PassRecordText.MappingOf(drain.FailedItem), SegmentLabel: null,
        EnqueuedAtUtc: drain.Started, ClaimedAtUtc: drain.Started, StartedAtUtc: drain.Started, EndedAtUtc: drain.Finished,
        RowsRead: 0, RowsWritten: drain.Rows ?? 0,
        ErrorSummary: drain.Outcome == "interrupted" ? "The runner's process went away before the drain ended." : drain.FailureMessage,
        FailureKind: drain.FailureKind);

    /// <summary>The newest ordinary pass of a runner-owned replication that has started, as the shared monitoring record (what <see cref="SyncRunHistory"/> asks).</summary>
    public async Task<DbDataKit.Monitoring.RunRecord?> LastRunAsync(string replication, CancellationToken ct)
    {
        var page = await PageAsync(replication, 20, null, ct).ConfigureAwait(false);
        if (page.Unavailable is not null) throw new RunnerUnavailableException(page.Unavailable);
        var last = page.Runs.Where(r => r.RunKind == RunKind.Primary && r.StartedAtUtc is not null).OrderByDescending(r => r.StartedAtUtc).FirstOrDefault();
        return last is null ? null : SyncRunHistory.ToRecord(last);
    }
}
