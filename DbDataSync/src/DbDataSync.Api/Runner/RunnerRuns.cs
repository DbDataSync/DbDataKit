using DbDataKit.Runner.Client;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>What the Sync API reads of a runner's runs of a replication's task (plan 28c E13): the runs, one run and its log, and the steps a task's runs recorded (the pass records of <see cref="PassRecord"/> and the polling checks).</summary>
public interface IRunnerRuns
{
    /// <summary>The runs of <paramref name="task"/>, or of every task of the link's package and channel when it is null, newest first.</summary>
    Task<IReadOnlyList<ClientRun>> RunsAsync(string? task, int limit, DateTimeOffset? before, string? outcome, CancellationToken ct);

    Task<ClientRun?> RunAsync(Guid id, CancellationToken ct);

    /// <summary>The lines of the run's log after <paramref name="after"/>; null when the runner has no such run.</summary>
    Task<IReadOnlyList<ClientLogLine>?> LogsAsync(Guid id, long? after, int limit, CancellationToken ct);

    Task<IReadOnlyList<ClientStep>> StepsAsync(string task, string step, int limit, DateTimeOffset? before, string? key, CancellationToken ct);

    /// <summary>Copies the file a completed step kept as its streamed result into <paramref name="destination"/> (plan 28c E06e); false when the runner has no such result any more.</summary>
    Task<bool> StepPayloadAsync(string task, string step, Guid payload, Stream destination, CancellationToken ct);
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

    /// <summary>The replications the runner owns, by the name of the task each one is on the runner.</summary>
    private IReadOnlyDictionary<string, string> OwnedTasks() =>
        runner.File.Read().Owners.Where(o => o.Value == DbDataKit.State.TaskOwners.Runner).ToDictionary(o => RunnerAccess.TaskOf(o.Key), o => o.Key, StringComparer.Ordinal);

    /// <summary>Whether any replication is the runner's (the live view and the other readers have nothing to ask a runner otherwise).</summary>
    public bool AnyOwned => runner.File.Read().Owners.Any(o => o.Value == DbDataKit.State.TaskOwners.Runner);

    /// <summary>The newest drains of the runner's replications, going or ended (one call: the runner lists the package's runs newest first), with the replication each one is of.</summary>
    public async Task<IReadOnlyList<(string Replication, ClientRun Run)>> RecentDrainsAsync(CancellationToken ct)
    {
        var owned = OwnedTasks();
        if (owned.Count == 0) return [];
        var recent = await Require().RunsAsync(null, 200, null, null, ct).ConfigureAwait(false);
        return [.. recent.Where(r => owned.ContainsKey(r.Task)).Select(r => (owned[r.Task], r))];
    }

    /// <summary>
    /// The lines of a run's log after line <paramref name="after"/>, in the order they were written, for a run id the runner has: a drain's own, or a pass's, whose lines are the drain's between the pass's start and end
    /// (the engine writes no pass id in a line, and passes of a drain overlap, so a pass shows what its drain said while it ran). Null when the runner has no run or pass of a replication it owns with that id.
    /// </summary>
    public async Task<IReadOnlyList<LogEntryRecord>?> LogLinesAsync(Guid runId, long? after, CancellationToken ct)
    {
        var owned = OwnedTasks();
        if (owned.Count == 0) return null;
        var runs = Require();
        var run = await runs.RunAsync(runId, ct).ConfigureAwait(false);
        var drain = run is not null && owned.ContainsKey(run.Task) ? runId : (Guid?)null;
        DateTimeOffset? from = null, to = null;
        if (drain is null)
        {
            foreach (var task in owned.Keys)
            {
                var step = (await runs.StepsAsync(task, PassRecordText.StepName, 1, null, runId.ToString("N"), ct).ConfigureAwait(false)).FirstOrDefault();
                if (PassRecordText.TryParse(step?.Result) is not { } pass) continue;
                (drain, from, to) = (pass.DrainRunId, pass.StartedAt.AddSeconds(-2), pass.EndedAt.AddSeconds(2));
                break;
            }
        }

        if (drain is null) return null;
        var lines = await runs.LogsAsync(drain.Value, after, 1000, ct).ConfigureAwait(false);
        if (lines is null) return null;
        return [.. lines.Where(l => (from is null || l.At >= from) && (to is null || l.At <= to)).Select(l => new LogEntryRecord(l.Id, runId, l.At, LevelOf(l.Level), l.Message))];
    }

    private static LogSeverity LevelOf(string level) => level.ToLowerInvariant() switch
    {
        "trace" => LogSeverity.Trace,
        "debug" => LogSeverity.Debug,
        "warning" => LogSeverity.Warning,
        "error" or "critical" => LogSeverity.Error,
        _ => LogSeverity.Info,
    };

    /// <summary>One run of any replication the runner owns, by id (a pass record's or a drain's), with the replication it is of; null when the runner has none.</summary>
    public async Task<TaskRunRecord?> FindAsync(Guid runId, CancellationToken ct)
    {
        var owned = OwnedTasks();
        if (owned.Count == 0) return null;
        var runs = Require();
        // a drain's id is what the live view asks about, every 750 ms: one call
        if (await runs.RunAsync(runId, ct).ConfigureAwait(false) is { } drain && owned.TryGetValue(drain.Task, out var of)) return ToRun(drain, of);
        foreach (var task in owned.Keys)
        {
            var step = (await runs.StepsAsync(task, PassRecordText.StepName, 1, null, runId.ToString("N"), ct).ConfigureAwait(false)).FirstOrDefault();
            if (PassRecordText.TryParse(step?.Result) is { } pass) return PassRecordText.ToRun(pass);
        }

        return null;
    }

    /// <summary>The runner's run as a row of the history: a drain that is going or did not succeed, unless a failed pass of it already says what went wrong.</summary>
    private static TaskRunRecord? DrainRow(ClientRun drain, string replication, IReadOnlyList<PassRecord> passes)
    {
        if (drain.Outcome == "succeeded") return null;
        if (drain.Outcome is "failed" or "completed_with_failures" && passes.Any(p => p.DrainRunId == drain.Id && p.Status == nameof(RunStatus.Failed))) return null;
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
