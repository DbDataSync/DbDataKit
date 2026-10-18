using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataBuild.Planning;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Cli.Web;

/// <summary>
/// What the page can do besides reading: write the answers a person gave to the questions of `plan`, and apply a plan they have read. Neither is a general command: the page cannot send an argument line.
/// Applying needs `dbdatabuild ui web --allow-apply` (the operator's decision when the server starts), the write login in the server's environment, and, per request, the person's own confirmation: the plan's
/// id and target typed back, and each allowance (risky steps, each object with a destructive step) named. The server checks them against the plan file it reads itself, and refuses an allowance the plan
/// does not need. An apply is a background job the page follows and can ask to stop between steps (the same hook the terminal interface uses).
/// </summary>
internal sealed class WebActions(string projectRoot, ICommandHost host, SemaphoreSlim oneCommandAtATime, bool allowApply)
{
    public const string AnswersFile = ".dbdatabuild/web-answers.yml";

    private static readonly Regex QuestionId = new(@"^[A-Za-z0-9._:\-]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex ChoiceKey = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public bool AllowApply => allowApply;

    // ---- answers ---------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the answers file the CLI reads (`--answers`). Each answer is a choice a person made; nothing is filled in here.</summary>
    public (int, string, string) SaveAnswers(JsonObject? body)
    {
        if (body?["answers"] is not JsonArray list || list.Count is 0 or > 500) return Refuse(400, "A list of answers (1 to 500).");
        var set = new AnswerSet();
        foreach (var item in list)
        {
            var id = item?["question_id"] is JsonValue a && a.TryGetValue<string>(out var s1) ? s1 : null;
            var choice = item?["choice"] is JsonValue b && b.TryGetValue<string>(out var s2) ? s2 : null;
            if (id == null || !QuestionId.IsMatch(id) || choice == null || !ChoiceKey.IsMatch(choice)) return Refuse(400, "Each answer needs a question_id and a choice (an option key).");
            var value = item?["value"] is JsonValue c && c.TryGetValue<string>(out var s3) ? s3 : null;
            var note = item?["note"] is JsonValue d && d.TryGetValue<string>(out var s4) ? s4 : null;
            if ((value?.Length ?? 0) > 2000 || (note?.Length ?? 0) > 2000) return Refuse(400, "A value or note is too long.");
            set.Add(id, choice, value, note);
        }
        var target = Path.Combine(projectRoot, AnswersFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, new System.Text.UTF8Encoding(false).GetBytes(set.ToYaml()));      // its own file: the terminal interface keeps tui-answers.yml
        return (200, "application/json; charset=utf-8", new JsonObject { ["path"] = AnswersFile, ["count"] = set.Count }.ToJsonString());
    }

    // ---- apply -----------------------------------------------------------------------------------------------------------------------------------------

    private sealed class Job
    {
        public required string Id { get; init; }
        public readonly List<string> Lines = [];
        public volatile bool Done, StopAsked;
        public int Exit;
        public string? Output, Errors;
        public bool DryRun;
        public required string Plan { get; init; }
    }

    private readonly ConcurrentDictionary<string, Job> jobs = new();

    /// <summary>Starts applying (or checking, with dry_run) a plan the server read itself. Returns the id of the job to follow.</summary>
    public (int, string, string) StartApply(JsonObject? body)
    {
        if (!allowApply) return Refuse(403, "This server was not started with --allow-apply: it can read plans but not apply them. Start `dbdatabuild ui web --allow-apply` to apply from this page.");
        var planFile = body?["plan"] is JsonValue p && p.TryGetValue<string>(out var s) ? s : null;
        if (planFile == null || Path.IsPathRooted(planFile) || planFile.Replace('\\', '/').Split('/') is not ["plans", _, ..] parts || parts.Any(x => x is ".." or "." or "") || !planFile.EndsWith(".plan.yml", StringComparison.Ordinal))
            return Refuse(400, "A plan file under plans/.");
        var full = Path.GetFullPath(Path.Combine(projectRoot, planFile));
        if (!File.Exists(full)) return Refuse(404, "No such plan.");
        var (browser, _) = PlanBrowser.Load(full);
        if (browser == null) return Refuse(409, "This plan does not parse or its hash does not match: it was edited or damaged. Make a new plan.");

        var dryRun = body?["dry_run"] is JsonValue d && d.TryGetValue<bool>(out var dry) && dry;
        var confirm = body?["confirm"] as JsonObject;
        var typedTarget = confirm?["connection"] is JsonValue t && t.TryGetValue<string>(out var tt) ? tt : null;
        var planId = confirm?["plan_id"] is JsonValue i && i.TryGetValue<string>(out var ii) ? ii : null;
        if (!dryRun && (typedTarget != browser.Plan.Connection || planId != browser.Plan.Id))
            return Refuse(400, $"To apply, type the plan's id and its target ({browser.Plan.Connection}) as shown. Nothing was run.");

        var allowRisky = body?["allow_risky"] is JsonValue r && r.TryGetValue<bool>(out var rr) && rr;
        var destructive = (body?["allow_destructive"] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var o) ? o : "").ToList() ?? [];
        if (allowRisky && browser.Risky == 0) return Refuse(400, "This plan has no risky step: that allowance is not needed and not given.");
        var extra = destructive.Except(browser.DestructiveObjects, StringComparer.Ordinal).ToList();
        if (extra.Count > 0) return Refuse(400, $"This plan has no destructive step for {string.Join(", ", extra)}: that allowance is not needed and not given.");

        if (jobs.Values.Any(j => !j.Done)) return Refuse(409, "An apply is already running.");
        var job = new Job { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), DryRun = dryRun, Plan = planFile };
        jobs[job.Id] = job;
        var argv = new List<string> { "connection", "deploy", "--apply-plan", full, "--project", projectRoot };
        if (dryRun) argv.Add("--dry-run");
        if (allowRisky) argv.Add("--allow-risky");
        foreach (var o in destructive.Distinct(StringComparer.Ordinal)) { argv.Add("--allow-destructive"); argv.Add(o); }

        _ = Task.Run(() =>
        {
            oneCommandAtATime.Wait();
            try
            {
                var result = host.Run(argv, new RunHooks(Progress: line => { lock (job.Lines) job.Lines.Add(line); }, StopRequested: () => job.StopAsked));
                job.Exit = result.Exit; job.Output = result.Out; job.Errors = result.Err;
            }
            catch (Exception ex) { job.Exit = CliApp.ExitInternal; job.Errors = $"The apply failed with {ex.GetType().Name}. Statements already sent to the target are in the statement log."; }
            finally { job.Done = true; oneCommandAtATime.Release(); }
        });
        return (202, "application/json; charset=utf-8", new JsonObject { ["job"] = job.Id }.ToJsonString());
    }

    public (int, string, string) JobStatus(string? id, int from)
    {
        if (id == null || !jobs.TryGetValue(id, out var job)) return Refuse(404, "No such job.");
        List<string> lines;
        lock (job.Lines) lines = job.Lines.Skip(Math.Max(from, 0)).ToList();
        var reply = new JsonObject
        {
            ["job"] = job.Id, ["plan"] = job.Plan, ["dry_run"] = job.DryRun, ["done"] = job.Done, ["stop_asked"] = job.StopAsked,
            ["next"] = Math.Max(from, 0) + lines.Count, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        };
        if (job.Done)
        {
            reply["exit"] = job.Exit;
            JsonNode? doc = null;
            try { doc = job.Output is { Length: > 0 } ? JsonNode.Parse(job.Output) : null; } catch (System.Text.Json.JsonException) { }
            reply["document"] = doc;
            reply["text"] = doc == null ? (job.Errors ?? job.Output) : null;
        }
        return (200, "application/json; charset=utf-8", reply.ToJsonString());
    }

    /// <summary>Asks a running apply to stop between its steps. What it already did stays done, and running the same plan again at a terminal continues it.</summary>
    public (int, string, string) StopJob(string? id)
    {
        if (id == null || !jobs.TryGetValue(id, out var job)) return Refuse(404, "No such job.");
        job.StopAsked = true;
        return (200, "application/json; charset=utf-8", new JsonObject { ["job"] = job.Id, ["stop_asked"] = true }.ToJsonString());
    }

    private static (int, string, string) Refuse(int status, string message) => (status, "text/plain; charset=utf-8", message);
}
