using DbDataKit.Runner.Api;
using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 27a IX16 with a real server and real task processes: a dry run reaches the task as a dry run, keeps nothing of what the run keeps and says so in the log; a version is promoted to production only after a sandbox run when the administrator
/// requires one, and a dry run is not one.
/// </summary>
public sealed class DryRunAndSandboxTests
{
    private static string Text(JsonNode? node, string name) => (string)node![name]!;

    // the job `tally` prints what its checkpoint held and moves it by one; the checkpoint is what a dry run must not move
    private static readonly ProgressDeclaration Tally = new([new DeclaredCheckpoint("seen", new Dictionary<string, DeclaredPart> { ["value"] = new(ProgressType.Int) })], []);

    private static PackageTask TallyTask() => SamplePackage.Task("tally") with { Progress = Tally };

    private static async Task<string> RunAsync(RunnerHarness h, bool dry, string channel = "dev", string task = "tally")
    {
        var before = (await h.RunsAsync($"?package=sample&channel={channel}&task={task}")).Count;
        var status = HttpStatusCode.Conflict;
        JsonNode? body = null;
        for (var attempt = 0; attempt < 100 && status == HttpStatusCode.Conflict; attempt++)
        {
            (status, body) = await h.PostAsync($"/api/v1/tasks/sample/{channel}/{task}/run", ServerRole.Operator, dry ? new { dry_run = true } : null);
            if (status == HttpStatusCode.Conflict) await Task.Delay(100);
        }

        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = Text(body, "run_id");
        await h.WaitAsync(async () => (await h.RunsAsync($"?package=sample&channel={channel}&task={task}")).FirstOrDefault(r => Text(r, "id") == id && r!["finished"] is not null), $"the run {id} (dry: {dry}) to end; runs: " + (await h.RunsAsync($"?package=sample&channel={channel}&task={task}")).ToJsonString(), seconds: 60);
        // the run record is written a moment before the scheduler lets go of the task's run lock: a start in that moment waits for the next tick (as RunTallyAsync of the staging tests also allows for)
        await Task.Delay(1500);
        Assert.True((await h.RunsAsync($"?package=sample&channel={channel}&task={task}")).Count > before);
        return id;
    }

    private static async Task<List<string>> LogOfAsync(RunnerHarness h, string runId) =>
        [.. (await h.GetAsync($"/api/v1/runs/{runId}/logs?limit=500")).Body!["lines"]!.AsArray().Select(l => Text(l, "message"))];

    [Fact]
    public async Task A_dry_run_reaches_the_task_changes_nothing_it_keeps_and_says_so_in_its_log()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(TallyTask());
        await h.StartAsync();

        var dry = await RunAsync(h, dry: true);
        var real = await RunAsync(h, dry: false);
        var next = await RunAsync(h, dry: false);

        var dryLog = await LogOfAsync(h, dry);
        Assert.Contains(dryLog, m => m.StartsWith("This is a dry run", StringComparison.Ordinal));
        Assert.Contains("tally saw 0", dryLog);
        // the dry run moved the checkpoint in memory and kept nothing: the next real run still sees 0, and only that run's own save counts
        Assert.Contains("tally saw 0", await LogOfAsync(h, real));
        Assert.Contains("tally saw 1", await LogOfAsync(h, next));
        Assert.DoesNotContain(await LogOfAsync(h, real), m => m.StartsWith("This is a dry run", StringComparison.Ordinal));
        Assert.Equal("succeeded", Text((await h.GetAsync($"/api/v1/runs/{dry}")).Body, "outcome"));
        // the audit says it was a dry run
        var run = h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 20)).Select(r => r.Entry.Detail).ToList();
        Assert.Contains(run, d => d is not null && d.Contains("\"dry_run\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dry_run_asked_for_with_a_bad_value_is_refused()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(TallyTask());
        await h.StartAsync();
        var (status, _) = await h.PostAsync("/api/v1/tasks/sample/dev/tally/run", ServerRole.Operator, new { dry_run = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_version_reaches_production_only_after_a_sandbox_run_when_the_administrator_requires_one_and_a_dry_run_is_not_one()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        var installed = h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        Assert.False((bool)(await h.GetAsync("/api/v1/settings")).Body!["require_sandbox_run"]!);

        // not required: any version may be promoted (this is the default, and what the runner did before)
        // required: refused naming the version, until a sandbox run succeeded
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_sandbox_run", ServerRole.Admin, new { value = true })).Status);
        Assert.True((bool)(await h.GetAsync("/api/v1/settings")).Body!["require_sandbox_run"]!);
        var (refused, refusal) = await h.PostAsync("/api/v1/definitions/integration/sample/switch", ServerRole.Admin, new { channel = "prod", version = installed.Version });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("no sandbox run", Text(refusal, "error"));
        Assert.Contains($"Version {installed.Version}", Text(refusal, "error"));

        // a dry run in dev does not count
        await RunAsync(h, dry: true, task: "ok");
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync("/api/v1/definitions/integration/sample/switch", ServerRole.Admin, new { channel = "prod", version = installed.Version })).Status);

        // a real run that succeeded in dev does
        await RunAsync(h, dry: false, task: "ok");
        var (allowed, _) = await h.PostAsync("/api/v1/definitions/integration/sample/switch", ServerRole.Admin, new { channel = "prod", version = installed.Version });
        Assert.Equal(HttpStatusCode.OK, allowed);

        // the audit holds the setting and the refusals
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: RunnerPublishing.SettingVerb, Limit: 20)), r => r.Entry.TargetId == "require_sandbox_run");
        Assert.Equal(2, h.Audit.Read(new AuditQuery(Verb: RunnerPublishing.SwitchVerb, Limit: 20)).Count(r => r.Entry.Outcome == AuditEvent.Refused));
    }

    [Fact]
    public async Task Only_an_administrator_sets_the_requirement()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_sandbox_run", ServerRole.Operator, new { value = true })).Status);
    }
}
