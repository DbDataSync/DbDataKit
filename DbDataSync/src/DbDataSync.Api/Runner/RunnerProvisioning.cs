using System.Text.Json;
using DbDataKit.Runner.Client;
using DbDataSync.Libraries;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>What the Sync API answers when it started the provisioning task on the runner: the run to follow (null when one was already queued or running).</summary>
public sealed record ProvisioningRunStarted(string? RunId, string Task, string OnBehalfOf);

/// <summary>One step of an apply on the runner, as the task reported it.</summary>
public sealed record ProvisioningRunStep(string Title, bool Succeeded, string? Error, string? Id = null, string? Outcome = null);

/// <summary>
/// A provisioning run of the runner as the Setup card and the replication-wide page follow it: its outcome (<c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c>...), the failure the runner recorded, and once it ended the steps the
/// task reported (<c>provision-applied</c>); <see cref="Steps"/> is null while the run has not reported them.
/// </summary>
public sealed record ProvisioningRunView(Guid RunId, string Outcome, DateTimeOffset Started, DateTimeOffset? Finished, string? Failure, IReadOnlyList<ProvisioningRunStep>? Steps);

/// <summary>
/// A person's Apply of a replication's database setup, run on the runner (plan 28c E21, Sync half step d; owner decision 2026-11-12: no write login in a Sync server). The Setup card's action of one mapping, or the chosen steps of the
/// replication-wide plan, become one run of the set's manual task <c>provision</c> (<see cref="RunnerTaskNames.Provision"/>) for the person the console vouched for; the DDL runs under the runner's run login and this server opens no
/// connection to apply it. The run is then read back from the runner.
/// </summary>
public sealed class RunnerProvisioning(RunnerAccess runner)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The step the task records its report as (<c>ProvisionSteps.Applied</c> in the Task Runner).</summary>
    public const string AppliedStep = "provision-applied";

    public Task<ProvisioningRunStarted> ApplyMappingAsync(string replication, string mapping, string action, string person, CancellationToken ct) =>
        StartAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["replication"] = replication, ["mapping"] = mapping, ["action"] = action, ["steps"] = "", ["repo_dir"] = DefinitionFiles.RepoDir }, person, ct);

    public Task<ProvisioningRunStarted> ApplyStepsAsync(string replication, IReadOnlyList<string> stepIds, string person, CancellationToken ct)
    {
        if (stepIds.Count == 0 || stepIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Contains(',', StringComparison.Ordinal)))
            throw new ArgumentException("Choose at least one step of the plan (a step id is never empty and has no comma).");
        return StartAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["replication"] = replication, ["mapping"] = "", ["action"] = "", ["steps"] = string.Join(',', stepIds), ["repo_dir"] = DefinitionFiles.RepoDir }, person, ct);
    }

    private async Task<ProvisioningRunStarted> StartAsync(Dictionary<string, string> inputs, string person, CancellationToken ct)
    {
        var sync = runner.Require();
        var runId = await sync.RunNowAsync(RunnerTaskNames.Provision, inputs, person, ct).ConfigureAwait(false);
        return new ProvisioningRunStarted(runId, RunnerTaskNames.Provision, person);
    }

    /// <summary>The run <paramref name="runId"/> of the provisioning task, or null when the runner has no such run (or it is a run of another task).</summary>
    public async Task<ProvisioningRunView?> RunAsync(Guid runId, CancellationToken ct)
    {
        if (runner.Require() is not IRunnerRuns runs)
            throw new OwnershipException("The linked runner cannot be asked for its runs.");
        var run = await runs.RunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.Task != RunnerTaskNames.Provision) return null;

        IReadOnlyList<ProvisioningRunStep>? steps = null;
        // the report is a step of the task keyed by the mapping (or the replication): the one this run recorded is the newest that started within the run
        var recorded = await runs.StepsAsync(RunnerTaskNames.Provision, AppliedStep, 20, null, null, ct).ConfigureAwait(false);
        if (recorded.Where(s => s.Started >= run.Started && (run.Finished is null || s.Started <= run.Finished)).OrderByDescending(s => s.Started).FirstOrDefault() is { Result: { } json })
        {
            var report = JsonSerializer.Deserialize<Report>(json, Web);
            steps = report?.Steps?.Select(s => new ProvisioningRunStep(s.Title ?? "", s.Succeeded, s.Error, s.Id, s.Outcome)).ToList();
        }

        return new ProvisioningRunView(run.Id, run.Outcome, run.Started, run.Finished, run.FailureMessage, steps);
    }

    private sealed record Report(string? Replication, string? Mapping, List<ReportStep>? Steps);

    private sealed record ReportStep(string? Title, bool Succeeded, string? Error, string? Id, string? Outcome);
}
