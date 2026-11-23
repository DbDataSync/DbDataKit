using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.Runner;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>What became of an activation the console asked for after a preflight: the HTTP status, a word for it (<c>activated</c>, <c>refused</c>, <c>waiting</c>, <c>already-served</c>), the version active now and the words.</summary>
internal sealed record PreflightActivation(int Status, string State, int? Active, string Message);

/// <summary>
/// The preflight before a version is served in production (Design: docs/publishing.md#preflight-before-activation). A deploy that serves a Runner package in <c>prod</c>, when the package declares the read-only manual task
/// <see cref="Preflight.TaskName"/>, stages the version held (the Runner does not activate it by itself), runs the preflight on that staged version as the person, reads its findings, and activates only when none is an error; an
/// error refuses the activation until the person confirms it, and the confirmation goes to the Runner with the activation, which keeps it in its record. A package that declares no preflight is served as before.
/// </summary>
internal static class ConsoleDeployPreflight
{
    /// <summary>The audit verbs: the activation of a held version, and a person's confirmation of a preflight's errors.</summary>
    public const string ActivateVerb = "build.activate";
    public const string ConfirmVerb = "build.preflight.confirm";

    /// <summary>Whether a deploy is one a preflight comes before: a Runner package (a build's package or a Store model's), served at once, in the production channel.</summary>
    public static bool AskedFor(string? channel, bool serve, string product, string route) =>
        serve && product == DeployProducts.Runner && route is "package" or "model-package" && string.Equals(channel?.Trim(), DefinitionStore.Production, StringComparison.Ordinal);

    /// <summary>What a held version says on the Runner it waits for.</summary>
    public static string HoldReason(RelayPerson person) => $"its preflight, then activation through the console for {person.Name ?? person.Id}";

    /// <summary>The console's address of a run on the Runner's Runs screen.</summary>
    public static string RunLink(string runnerInstance, string runId) => $"/servers/{Uri.EscapeDataString(runnerInstance)}/runs?run={Uri.EscapeDataString(runId)}";

    /// <summary>The run read until it ends or <paramref name="wait"/> runs out; the newest reading (null while the Runner has not started it).</summary>
    public static async Task<JsonObject?> WaitAsync(RunnerCalls calls, string package, string channel, Guid run, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + wait;
        JsonObject? last = null;
        while (true)
        {
            last = await calls.RunOfAsync(package, channel, run, Preflight.StepName, ct) ?? last;
            if (last?["finished"] is not null || DateTimeOffset.UtcNow >= until) return last;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>
    /// The preflight as the console reports it: the run and its link, whether it has ended, and its findings with the counts of errors and warnings. A run that ended without succeeding is an error finding of its own: what it would have
    /// found is not known.
    /// </summary>
    public static JsonObject Report(Guid run, JsonObject? read, string runnerInstance)
    {
        var findings = Preflight.Findings((read?["steps"] as JsonArray ?? []).Select(s => s?["result"])).ToList();
        var finished = read?["finished"] is not null;
        var outcome = (string?)read?["outcome"];
        if (finished && outcome != "succeeded")
            findings.Add(new PreflightFinding(Preflight.Error, $"The preflight run ended {outcome ?? "without an outcome"}{((string?)read?["failure"]?["message"] is { } m ? ": " + m : "")}; what it would have found is not known."));
        var id = run.ToString("D");
        return new JsonObject
        {
            ["run"] = id, ["link"] = RunLink(runnerInstance, id), ["state"] = finished ? "finished" : "running", ["outcome"] = outcome,
            ["errors"] = findings.Count(f => f.Severity == Preflight.Error), ["warnings"] = findings.Count(f => f.Severity == Preflight.Warning),
            ["findings"] = new JsonArray([.. findings.Select(f => (JsonNode?)new JsonObject { ["severity"] = f.Severity, ["message"] = f.Message, ["about"] = f.About })]),
        };
    }

    /// <summary>The errors of a report in words, at most three, for a refusal and for the Runner's record of a confirmation.</summary>
    public static string ErrorsInWords(JsonObject report)
    {
        var errors = (report["findings"] as JsonArray ?? []).OfType<JsonObject>().Where(f => (string?)f["severity"] == Preflight.Error).Select(f => ((string?)f["about"] is { Length: > 0 } about ? about + ": " : "") + (string?)f["message"]).ToList();
        return string.Join(" ", errors.Take(3)) + (errors.Count > 3 ? $" (and {errors.Count - 3} more)" : "");
    }

    /// <summary>
    /// Activates the held version after its preflight: refused while the run has not ended, and while it found an error the person has not confirmed; otherwise the Runner activates it with the reason (the confirmation, when there was
    /// one), which it keeps in its record of the activation.
    /// </summary>
    public static async Task<PreflightActivation> ActivateAsync(RunnerCalls calls, string package, string channel, int version, JsonObject report, bool confirmed, RelayPerson person, CancellationToken ct)
    {
        var who = person.Name ?? person.Id;
        var errors = (int?)report["errors"] ?? 0;
        if ((string?)report["state"] != "finished")
            return new(409, "waiting", null, $"The preflight of version {version} (run {report["run"]}) has not ended yet, so version {version} stays staged in {channel}. Activate it once the run has ended.");
        if (errors > 0 && !confirmed)
            return new(409, "refused", null, $"The preflight of version {version} found {errors} error{(errors == 1 ? "" : "s")}, so it was not activated in {channel}: {ErrorsInWords(report)} It stays staged. "
                + "To activate it anyway, confirm; the confirmation is kept in the Runner's record of the activation.");
        var reason = errors > 0
            ? $"{who} confirmed the activation through the console although preflight run {report["run"]} found {errors} error{(errors == 1 ? "" : "s")}: {ErrorsInWords(report)}"
            : $"preflight run {report["run"]} found no error ({report["warnings"]} warning(s)); activated through the console for {who}";
        try
        {
            var active = await calls.ActivateAsync(package, channel, reason, ct);
            return active is null
                ? new(202, "waiting", null, $"The Runner activates version {version} of {package} in {channel} once the runs in flight have ended.")
                : new(200, "activated", active, $"Version {active} of {package} is active in {channel}.");
        }
        catch (DeployException ex) when (ex.Status == 404)
        {
            // nothing is staged: the same content was served already, or someone activated it first
            return new(200, "already-served", version, $"Nothing of {package} is staged in {channel} any more ({ex.Message}); version {version} was served already.");
        }
    }
}
