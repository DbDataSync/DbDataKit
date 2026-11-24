using System.Text.Json.Nodes;
using DbDataKit.Runner.Client;
using DbDataKit.State;

namespace DbDataKit.Deploy;

/// <summary>
/// The calls around a deploy to a Runner: a run of a staged or a served version, a run and the steps it recorded, and the Runner's test of a binding. Each goes through <c>link</c>, which says who is calling (a person through a control
/// service, or a server's publisher key); the person a run is for is named in it as <c>on_behalf_of</c>. A Runner's refusal comes back in its words, as a <see cref="DeployException"/> whose status says what kind of refusal it is.
/// </summary>
public sealed class RunnerCalls
{
    private readonly DeployTarget target;
    private readonly RunnerPublishClient client;

    public RunnerCalls(IDefinitionsLink link, DeployTarget target)
    {
        Require(target);
        this.target = target;
        client = RunnerPublishClient.ForLink(link);
    }

    /// <summary>Refuses, in words, a target that is not a Runner: only a Runner runs a task.</summary>
    public static void Require(DeployTarget target)
    {
        if (target.Product != DeployProducts.Runner) throw new DeployException($"'{target.Name}' is a {target.Product} ({target.Url}), not a Runner; only a Runner runs a task.", 409);
    }

    /// <summary>
    /// Runs the manual-only task <paramref name="task"/> of a version that is staged on the Runner (a draft's database setup applied before it is published): the version is held by the Runner and not served, so no schedule starts; the run is
    /// recorded for <paramref name="actor"/>, the person, and runs with the logins bound on the Runner's channel. A run of the task that is queued or running already is refused.
    /// </summary>
    public async Task<JsonObject> RunStagedTaskAsync(string package, string channel, string version, string task, IReadOnlyDictionary<string, string>? inputs, string actor, Action<string> step, CancellationToken ct = default)
    {
        try
        {
            var runId = await client.RunVersionAsync(target.Name, null, package, channel, task, version, inputs, actor, step, ct)
                ?? throw new DeployException($"{package}/{channel}/{task} is queued or running already on {target.Name}; wait for that run to end.", 409);
            step($"Started {task} of {package} version {version} on {channel}: run {runId}.");
            return new JsonObject { ["run_id"] = runId, ["task"] = task, ["package"] = package, ["channel"] = channel, ["version"] = version, ["target"] = target.Name, ["runner"] = target.Url, ["instance"] = target.InstanceId, ["on_behalf_of"] = actor };
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>
    /// Runs <paramref name="task"/> of the version the channel serves now, for <paramref name="actor"/>: a scheduled task too (a replication's drain, a refresh), which <see cref="RunStagedTaskAsync"/> refuses because a version that is not served
    /// starts nothing. It is the Runner's Run now, asked from where the project is, so nobody has to find the Runner first.
    /// </summary>
    public async Task<JsonObject> RunServedTaskAsync(string package, string channel, string task, IReadOnlyDictionary<string, string>? inputs, string actor, Action<string> step, CancellationToken ct = default)
    {
        try
        {
            var runId = await client.RunNowAsync(target.Name, null, package, channel, task, inputs, actor, step, ct)
                ?? throw new DeployException($"{package}/{channel}/{task} is queued or running already on {target.Name}; wait for that run to end (a request to a drain that is running is sent with its next run).", 409);
            step($"Started {task} of {package} as served on {channel}: run {runId}.");
            return new JsonObject { ["run_id"] = runId, ["task"] = task, ["package"] = package, ["channel"] = channel, ["version"] = "served", ["target"] = target.Name, ["runner"] = target.Url, ["instance"] = target.InstanceId, ["on_behalf_of"] = actor };
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>A run on the Runner by its id, with the steps named <paramref name="stepName"/> of its task recorded while it ran (their results as JSON); null when the Runner has no such run.</summary>
    public async Task<JsonObject?> RunOfAsync(string package, string channel, Guid runId, string? stepName, CancellationToken ct = default)
    {
        try
        {
            if (await client.RunAsync(target.Name, null, runId, null, ct) is not { } run) return null;
            var answer = new JsonObject
            {
                ["id"] = run.Id, ["task"] = run.Task, ["outcome"] = run.Outcome, ["started"] = run.Started, ["finished"] = run.Finished, ["version"] = run.PackageVersion,
                ["failure"] = run.FailureMessage is null ? null : new JsonObject { ["kind"] = run.FailureKind, ["message"] = run.FailureMessage },
            };
            if (stepName is not null)
            {
                // a step is keyed by what it is about, not by the run: the steps of this run are those that started while it ran
                var steps = await client.StepsAsync(target.Name, null, package, channel, run.Task, stepName, 20, null, null, null, ct);
                answer["steps"] = new JsonArray([.. steps.Where(s => s.Started >= run.Started && (run.Finished is null || s.Started <= run.Finished)).OrderBy(s => s.Started)
                    .Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["started"] = s.Started, ["completed"] = s.Completed, ["result"] = s.Result is null ? null : JsonNode.Parse(s.Result) })]);
            }
            return answer;
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>
    /// The answer of a run started for <paramref name="actor"/> (the rows of a preview, a strategy's candidates), taken once from the Runner's memory: the Runner's status and body (<c>state</c> <c>done</c> with <c>answer</c>,
    /// <c>running</c>, <c>failed</c> with <c>error</c>, or <c>gone</c>). The Runner hands it only to the person the run was started for, through the same console. Nothing of it is kept on the way.
    /// </summary>
    public async Task<(int Status, JsonObject Body)> AnswerOfAsync(Guid runId, string actor, CancellationToken ct = default)
    {
        try { return await client.RunAnswerAsync(target.Name, null, runId, actor, null, ct); }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>
    /// The newest run of <paramref name="task"/> of the package in the channel, whatever version it ran, with the steps named <paramref name="stepName"/> it recorded (as <see cref="RunOfAsync"/> answers it); null when the task never ran there.
    /// </summary>
    public async Task<JsonObject?> NewestRunAsync(string package, string channel, string task, string? stepName, CancellationToken ct = default)
    {
        IReadOnlyList<ClientRun> runs;
        try { runs = await client.RunsAsync(target.Name, null, package, channel, task, 1, null, null, null, ct); }
        catch (RunnerClientException ex) { throw Refused(ex); }
        return runs.Count == 0 ? null : await RunOfAsync(package, channel, runs[0].Id, stepName, ct);
    }

    /// <summary>
    /// The Runner's own test of a slot's binding: the slot is bound in the channel, the Runner has the connection it names, the login resolves and the connection opens, tried on the Runner the way a run would, so the login never leaves the
    /// Runner. The Runner asks for its administrator role and refuses a caller without it in its words. The answer is the Runner's (<c>slot, ok, step, message, connected</c>) with where it was tried; no value is in it.
    /// </summary>
    public async Task<JsonObject> TestBindingAsync(string package, string channel, string slot, string role, CancellationToken ct = default)
    {
        try
        {
            var check = await client.TestBindingAsync(target.Name, null, package, channel, slot, role, null, ct);
            return new JsonObject
            {
                ["slot"] = slot, ["ok"] = check.Ok, ["step"] = check.Step, ["message"] = check.Message, ["connected"] = check.Connected,
                ["package"] = package, ["channel"] = channel, ["target"] = target.Name, ["instance"] = target.InstanceId,
            };
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>
    /// Activates the version staged in <paramref name="channel"/>, the Runner's administrator call: a version the deploy staged with a hold waits for it. <paramref name="reason"/> is kept in the Runner's record of the activation (what the
    /// person confirmed). The answer is the version now active, or null while the Runner waits for runs in flight before it activates.
    /// </summary>
    public async Task<int?> ActivateAsync(string package, string channel, string? reason, CancellationToken ct = default)
    {
        try { return await client.ActivateAsync(target.Name, null, package, channel, reason, null, ct); }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>A Runner's refusal as a status: 502 when it could not be reached, 403 or 404 when it said so (404 only when <paramref name="notFound"/>), 409 otherwise.</summary>
    internal static DeployException Refused(RunnerClientException ex, bool notFound = true) =>
        new(ex.Message, ex.Message.Contains("could not be reached", StringComparison.Ordinal) ? 502 : ex.Message.Contains("(403)", StringComparison.Ordinal) ? 403 : notFound && ex.Message.Contains("(404)", StringComparison.Ordinal) ? 404 : 409);
}
