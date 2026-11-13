using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataSync.Api.Services;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>
/// What the API does in place of starting a worker for a replication the runner owns (plan 28c E12): every request that put rows in Sync's work queue and started a process becomes one <c>RunNow</c> of the replication's drain, with
/// what was asked as the <c>requests</c> input (<see cref="DrainRequest"/>: the segments already divided by the API, which can reach the source) and <c>run_all</c> for "Run now". The runner has one drain of a replication at a time: when one
/// is queued or running, a plain run now is answered as done (that drain is the one that runs it), and a request that carries work is put in the task's own queue as an item of kind <c>Request</c>, which that drain takes in its next sweep
/// and applies as it would the input (and a drain that has just ended leaves it for the next one). Every request has an id, and a drain applies the id once.
/// </summary>
public sealed class RunnerDispatch(RunnerAccess runner)
{
    /// <summary>The replication's drain is the runner's: nothing of the API's own processes it.</summary>
    public bool RunnerOwns(string replication) => runner.File.OwnerOf(replication) == TaskOwners.Runner;

    public async Task<TriggerResult> SendAsync(string replication, IReadOnlyList<DrainRequest> requests, bool runAll, CancellationToken ct)
    {
        IRunnerSync sync;
        try { sync = runner.Require(); }
        catch (OwnershipException ex) { return TriggerResult.FailedToStart(ex.Message); }

        requests = [.. requests.Select(r => r.Id is { Length: > 0 } ? r : r with { Id = Guid.NewGuid().ToString("N") })];
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runAll) inputs["run_all"] = "true";
        if (requests.Count > 0) inputs["requests"] = DrainRequestText.Serialize(requests);
        try
        {
            var runId = await sync.RunNowAsync(RunnerAccess.TaskOf(replication), inputs, ct).ConfigureAwait(false);
            if (runId is not null && Guid.TryParse(runId, out var id))
                return TriggerResult.Started([id]);
            // a drain is up (or queued) already: it takes the work from the task's own queue
            foreach (var request in requests)
                await sync.EnqueueWorkAsync(RunnerAccess.TaskOf(replication), DrainRequestText.ItemKind, request.Mapping, request.Id!, DrainRequestText.SerializeOne(request), ct).ConfigureAwait(false);
            return TriggerResult.Started([]);
        }
        catch (RunnerClientException ex)
        {
            return TriggerResult.FailedToStart($"The runner refused: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops a run of the runner (plan 28c E12g): the Sync API's cancel asks its own supervisor first and, for a run that is not its own, the linked runner. False when no runner is linked or the runner has no such run (or it ended); a runner
    /// that cannot be reached is thrown as <see cref="RunnerUnavailableException"/>, never answered as "no such run".
    /// </summary>
    public async Task<bool> CancelRunAsync(Guid runId, CancellationToken ct)
    {
        if (runner.Open() is not { } sync) return false;
        try { return await sync.CancelRunAsync(runId, ct).ConfigureAwait(false); }
        catch (RunnerClientException ex) { throw new RunnerUnavailableException(ex.Message, ex); }
    }

    /// <summary>The runner's row of the replication's drain for the status card; never throws: a runner that cannot be asked is <see cref="RunnerTaskState.Unreachable"/>.</summary>
    public async Task<RunnerTaskState> StateOfAsync(string replication, CancellationToken ct)
    {
        if (runner.Open() is not { } sync) return RunnerTaskState.Unreachable($"No runner is linked ({runner.File.Path}).");
        try
        {
            return await sync.StatusAsync(RunnerAccess.TaskOf(replication), ct).ConfigureAwait(false) is { } status ? RunnerTaskState.From(status) : RunnerTaskState.NoTask;
        }
        catch (RunnerClientException ex)
        {
            return RunnerTaskState.Unreachable(ex.Message);
        }
    }

    /// <summary>Pause or resume on the runner: what stops, or lets go of, the next start of the drain (a drain that is up sees it and plans nothing new).</summary>
    public async Task SetPausedAsync(string replication, bool paused, CancellationToken ct)
    {
        var sync = runner.Require();
        if (paused) await sync.PauseAsync(RunnerAccess.TaskOf(replication), ct).ConfigureAwait(false);
        else await sync.ResumeAsync(RunnerAccess.TaskOf(replication), ct).ConfigureAwait(false);
    }
}
