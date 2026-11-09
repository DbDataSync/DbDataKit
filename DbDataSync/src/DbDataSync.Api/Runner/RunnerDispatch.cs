using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataSync.Api.Services;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>
/// What the API does in place of starting a worker for a replication the runner owns (plan 28c E12): every request that put rows in Sync's work queue and started a process becomes one <c>RunNow</c> of the replication's drain, with
/// what was asked as the <c>requests</c> input (<see cref="DrainRequest"/>: the segments already divided by the API, which can reach the source) and <c>run_all</c> for "Run now". The runner has one drain of a replication at a time: when one
/// is queued or running, a plain run now is answered as done (that drain is the one that runs it), and a request that carries work is refused naming why, because the runner's API cannot hand work to a drain that is already up.
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

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runAll) inputs["run_all"] = "true";
        if (requests.Count > 0) inputs["requests"] = DrainRequestText.Serialize(requests);
        try
        {
            var runId = await sync.RunNowAsync(RunnerAccess.TaskOf(replication), inputs, ct).ConfigureAwait(false);
            if (runId is not null && Guid.TryParse(runId, out var id))
                return TriggerResult.Started([id]);
            return requests.Count == 0
                ? TriggerResult.Started([])
                : TriggerResult.FailedToStart($"A drain of '{replication}' is queued or running on the runner already, and the runner takes work for a drain only when it starts one: ask again when it has ended (a continuous replication's drain ends after its idle timeout).");
        }
        catch (RunnerClientException ex)
        {
            return TriggerResult.FailedToStart($"The runner refused: {ex.Message}");
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
