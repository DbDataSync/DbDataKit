using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Web;
using DbDataBuild.Planning;
using DbDataBuild.State;
using DbDataBuild.Tui.Model;

namespace DbDataBuild.Tests.Unit;

/// <summary>What the page can do besides read: save answers, and apply a plan a person confirmed. The host is a stand-in that records what it was asked to run: no database is involved.</summary>
public sealed class WebActionsTests : IDisposable
{
    private sealed class RecordingHost : ICommandHost
    {
        public readonly List<List<string>> Calls = [];
        public bool WaitForStop;
        public readonly ManualResetEventSlim Started = new();
        public bool SawStop;
        public IReadOnlyList<CommandInfo> Commands => new TuiCommand.CliHost(_ => null).Commands;

        public CommandResult Run(IReadOnlyList<string> args, RunHooks? hooks = null)
        {
            lock (Calls) Calls.Add([.. args]);
            if (!args.Contains("--apply-plan")) return new(0, "{\"command\":\"" + string.Join(' ', args.TakeWhile(a => !a.StartsWith('-'))) + "\"}", "");
            hooks?.Progress?.Invoke("step 1 of 2");
            Started.Set();
            if (WaitForStop) { var until = DateTime.UtcNow.AddSeconds(20); while (DateTime.UtcNow < until && hooks?.StopRequested?.Invoke() != true) Thread.Sleep(10); SawStop = hooks?.StopRequested?.Invoke() == true; }
            hooks?.Progress?.Invoke("step 2 of 2");
            return new(0, "{\"command\":\"connection deploy\",\"ok\":true,\"exit_code\":0}", "");
        }
    }

    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-act-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly HttpClient http = new();
    private readonly List<WebServer> servers = [];
    private readonly RecordingHost host = new();

    public WebActionsTests() { Directory.CreateDirectory(Path.Combine(dir, "plans", "postgres")); }

    public void Dispose() { foreach (var s in servers) s.Dispose(); http.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    private WebServer Start(bool allowApply)
    {
        var s = new WebServer(dir, host, 0, allowApply);
        s.StartAsync();
        servers.Add(s);
        return s;
    }

    private static Plan Make(string id) => new(id, "postgres", "4af31c2", false, "0.1.0", [new ObjectBase("marts.fct", ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
    [
        new PlanStep("1", StepType.Ddl, "marts.fct", "drop column x", "ALTER TABLE marts.fct DROP COLUMN x;", RiskClass.Destructive, ["col.dropped"], new string('b', 64), []),
        new PlanStep("2", StepType.Load, "marts.fct", "backfill", "INSERT INTO t SELECT 1;", RiskClass.Risky, ["backfill"], null, []),
    ], []);

    private string WritePlan(string id = "2026-10-05-eeee0005")
    {
        File.WriteAllText(Path.Combine(dir, "plans", "postgres", id + ".plan.yml"), PlanDocument.Serialize(Make(id)));
        return $"plans/postgres/{id}.plan.yml";
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(WebServer s, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{s.Port}{path}") { Content = new StringContent(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(body))!.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-DDB-Token", s.Token);
        var r = await http.SendAsync(request);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private async Task<JsonNode> Job(WebServer s, string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{s.Port}/api/job?id={id}");
        request.Headers.Add("X-DDB-Token", s.Token);
        return JsonNode.Parse(await (await http.SendAsync(request)).Content.ReadAsStringAsync())!;
    }

    private async Task<JsonNode> Finished(WebServer s, string id)
    {
        for (var i = 0; i < 400; i++) { var j = await Job(s, id); if ((bool)j["done"]!) return j; await Task.Delay(25); }
        throw new TimeoutException("The job did not finish.");
    }

    private object Request(string plan, string? target = "postgres", string? planId = "2026-10-05-eeee0005", bool risky = true, string[]? destructive = null, bool dryRun = false) =>
        new { plan, dry_run = dryRun, allow_risky = risky, allow_destructive = destructive ?? ["marts.fct"], confirm = new { connection = target, plan_id = planId } };

    [Fact]
    public async Task Without_allow_apply_a_plan_cannot_be_applied_and_nothing_is_run()
    {
        var plan = WritePlan();
        var s = Start(allowApply: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s, "/api/apply", Request(plan))).Status);
        Assert.Empty(host.Calls);
        var caps = JsonNode.Parse(await (await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{s.Port}/api/capabilities") { Headers = { { "X-DDB-Token", s.Token } } })).Content.ReadAsStringAsync())!;
        Assert.False((bool)caps["apply"]!);
    }

    [Fact]
    public async Task An_apply_needs_the_persons_confirmation_and_only_the_allowances_the_plan_needs()
    {
        var plan = WritePlan();
        var s = Start(allowApply: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request(plan, target: null))).Status);                      // nothing typed
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request(plan, target: "sqlserver"))).Status);               // the wrong target
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request(plan, planId: "other"))).Status);                   // the wrong plan id
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request(plan, destructive: ["marts.other"]))).Status);      // an allowance the plan does not need
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request("../x.plan.yml"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/apply", Request("models/a.plan.yml"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(s, "/api/apply", Request("plans/postgres/none.plan.yml"))).Status);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task An_edited_plan_is_never_applied()
    {
        var plan = WritePlan();
        var full = Path.Combine(dir, plan);
        File.WriteAllText(full, File.ReadAllText(full).Replace("DROP COLUMN x", "DROP COLUMN y"));
        var s = Start(allowApply: true);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s, "/api/apply", Request(plan))).Status);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task A_confirmed_apply_runs_exactly_the_command_the_plan_calls_for_and_reports_progress()
    {
        var plan = WritePlan();
        var s = Start(allowApply: true);
        var (status, body) = await Post(s, "/api/apply", Request(plan));
        Assert.Equal(HttpStatusCode.Accepted, status);
        var done = await Finished(s, (string)JsonNode.Parse(body)!["job"]!);
        Assert.Equal(0, (int)done["exit"]!);
        Assert.Equal(["step 1 of 2", "step 2 of 2"], done["lines"]!.AsArray().Select(l => (string)l!));
        Assert.True((bool)done["document"]!["ok"]!);
        var call = Assert.Single(host.Calls);
        Assert.Equal(["connection", "deploy", "--apply-plan", Path.GetFullPath(Path.Combine(dir, plan)), "--project", Path.GetFullPath(dir), "--allow-risky", "--allow-destructive", "marts.fct"], call);
    }

    [Fact]
    public async Task A_dry_run_needs_no_confirmation_and_is_run_as_one()
    {
        var plan = WritePlan();
        var s = Start(allowApply: true);
        var (status, body) = await Post(s, "/api/apply", new { plan, dry_run = true });
        Assert.Equal(HttpStatusCode.Accepted, status);
        await Finished(s, (string)JsonNode.Parse(body)!["job"]!);
        Assert.Contains("--dry-run", Assert.Single(host.Calls));
        Assert.DoesNotContain("--allow-risky", host.Calls[0]);
    }

    [Fact]
    public async Task A_running_apply_blocks_a_second_and_can_be_asked_to_stop()
    {
        host.WaitForStop = true;
        var plan = WritePlan();
        var s = Start(allowApply: true);
        var (_, body) = await Post(s, "/api/apply", Request(plan));
        var id = (string)JsonNode.Parse(body)!["job"]!;
        Assert.True(host.Started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s, "/api/apply", Request(plan))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(s, "/api/job/stop?id=" + id, new { })).Status);
        var done = await Finished(s, id);
        Assert.True(host.SawStop);
        Assert.True((bool)done["stop_asked"]!);
    }

    [Fact]
    public async Task Answers_are_written_as_the_answers_file_the_command_reads_without_touching_the_terminals()
    {
        Directory.CreateDirectory(Path.Combine(dir, ".dbdatabuild"));
        File.WriteAllText(Path.Combine(dir, ".dbdatabuild", "tui-answers.yml"), "answers: []\n");
        var s = Start(allowApply: false);
        var (status, _) = await Post(s, "/api/answers", new { answers = new object[] { new { question_id = "Q-history-marts.fct.blank", choice = "not_backfilled" }, new { question_id = "Q-rename-marts.fct.a", choice = "rename_to", value = "b", note = "checked" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        var text = File.ReadAllText(Path.Combine(dir, ".dbdatabuild", "web-answers.yml"));
        Assert.Contains("Q-rename-marts.fct.a", text);
        Assert.Contains("rename_to", text);
        Assert.Equal("answers: []\n", File.ReadAllText(Path.Combine(dir, ".dbdatabuild", "tui-answers.yml")));
        foreach (var bad in new object[] { new { answers = new object[0] }, new { answers = new object[] { new { question_id = "x y", choice = "a" } } }, new { answers = new object[] { new { question_id = "Q", choice = "Not A Key" } } }, new { nothing = 1 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/answers", bad)).Status);
    }

    [Fact]
    public async Task Plan_can_be_run_from_the_page_but_without_accepting_the_proposals_for_the_person()
    {
        var s = Start(allowApply: false);
        Assert.Equal(HttpStatusCode.OK, (await Post(s, "/api/run", new { command = "connection_deploy", arguments = new { answers = ".dbdatabuild/web-answers.yml", write_plan = true } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s, "/api/run", new { command = "connection_deploy", arguments = new { accept_inferred = true } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(s, "/api/run", new { command = "connection_refresh", arguments = new { } })).Status);
    }
}
