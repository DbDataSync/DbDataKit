using DbDataKit.Runner.Client;
using DbDataSync.State;
using DbDataSync.Verification;

namespace DbDataSync.Api.Runner;

/// <summary>What a read of one verification result of a runner-owned replication found (plan 28c E06e).</summary>
/// <param name="Page">The page, when the result was found and its file read.</param>
/// <param name="NotFound">No result of that number is among the replication's results on the runner.</param>
/// <param name="Error">The runner's record is there and its file is not: the run's retention removed it ("expired", naming the run).</param>
public sealed record RunnerVerificationPage(VerificationResultPage? Page, bool NotFound, string? Error)
{
    public static RunnerVerificationPage Missing { get; } = new(null, true, null);
}

/// <summary>
/// The verification results of a replication the runner owns (plan 28c E06e). A drain on the runner records a result as two steps of its run: the file (the streamed result of <see cref="VerificationResultPayload.FileStepName"/>, a
/// payload under the run's retention) and the record naming it (<see cref="VerificationResultPayload.RecordStepName"/>); nothing is written beside the Sync API's own state. The records are listed from the runner's steps and a
/// page is read from the file the runner serves, copied to a scratch file for the time of the read (the parquet reader wants a path) and deleted after.
/// <para>
/// The runner has no numbered index, so a result's number in the Sync API's routes is made of its payload's id (<see cref="VerificationResultPayload.RouteIdOf"/>); the replication in the route is part of the lookup, as the check
/// on the file's folder is for the supervisor's results.
/// </para>
/// </summary>
public sealed class RunnerVerificationResults(RunnerAccess runner)
{
    /// <summary>The most records looked at when one result is found by its number (the steps endpoint's own cap).</summary>
    private const int LookupLimit = 500;

    private IRunnerRuns Require() => runner.OpenRuns() ?? throw new RunnerUnavailableException(runner.Link is null
        ? $"No runner is linked, so the verification results of a replication it owns cannot be read ({runner.File.Path})."
        : "The linked runner cannot be asked for its verification results.");

    /// <summary>The results of the replication, newest first, those of one mapping when <paramref name="mappingName"/> is given; the runner's failure is thrown as <see cref="RunnerUnavailableException"/>, never answered as an empty list.</summary>
    public async Task<IReadOnlyList<VerificationResultRecord>> ListAsync(string replication, string? mappingName, int limit, CancellationToken ct)
    {
        var runs = Require();
        try
        {
            var steps = await runs.StepsAsync(RunnerAccess.TaskOf(replication), VerificationResultPayload.RecordStepName, Math.Clamp(limit, 1, LookupLimit), null, null, ct).ConfigureAwait(false);
            var records = new List<VerificationResultRecord>();
            foreach (var step in steps)
            {
                if (VerificationResultPayload.TryParse(step.Result) is not { } record) continue;
                if (VerificationResultPayload.IdOf(record.ResultPath) is not { } payload) continue;
                if (mappingName is not null && record.MappingName != mappingName) continue;
                records.Add(record with { Id = VerificationResultPayload.RouteIdOf(payload), TaskName = replication });
            }

            return [.. records.OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.Id).Take(limit)];
        }
        catch (RunnerClientException ex)
        {
            throw new RunnerUnavailableException(ex.Message, ex);
        }
    }

    /// <summary>The record of the result numbered <paramref name="id"/>, or null when the replication has none such on the runner.</summary>
    public async Task<VerificationResultRecord?> FindAsync(string replication, long id, CancellationToken ct) =>
        (await ListAsync(replication, null, LookupLimit, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == id);

    /// <summary>One page of the result numbered <paramref name="id"/>, read from the file the runner holds.</summary>
    public async Task<RunnerVerificationPage> ReadPageAsync(string replication, long id, int offset, int limit, bool differingOnly, CancellationToken ct)
    {
        if (await FindAsync(replication, id, ct).ConfigureAwait(false) is not { } record) return RunnerVerificationPage.Missing;
        var payload = VerificationResultPayload.IdOf(record.ResultPath)!.Value;
        var scratch = Path.Combine(Path.GetTempPath(), "dbdatasync-verification-" + Guid.NewGuid().ToString("N") + ".parquet");
        try
        {
            bool found;
            try
            {
                await using var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                found = await Require().StepPayloadAsync(RunnerAccess.TaskOf(replication), VerificationResultPayload.FileStepName, payload, file, ct).ConfigureAwait(false);
            }
            catch (RunnerClientException ex)
            {
                throw new RunnerUnavailableException(ex.Message, ex);
            }

            if (!found)
                return new RunnerVerificationPage(null, false, $"The result file of the run {record.RunId} has expired: the runner's retention removed it. Run the check again to produce a new one.");
            return new RunnerVerificationPage(await VerificationResultQuery.ReadPageAsync(scratch, offset, limit, differingOnly, ct).ConfigureAwait(false), false, null);
        }
        finally
        {
            try { File.Delete(scratch); }
            catch (IOException) { /* a temp file; the folder is the machine's */ }
        }
    }
}
