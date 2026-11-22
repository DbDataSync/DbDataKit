using System.Net;
using System.Text;
using System.Text.Json;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// A task that creates, alters or drops tables (the capability <c>ddl</c>) starts only on the control service's signed request, made after a person approved the exact statements. A start without it, from the Runner's own
/// screen or by a paired client that signed nothing, is refused and audited; a request for another input, another task or one used before is refused too.
/// </summary>
public sealed class DdlRunApprovalTests
{
    private const string Path = "/api/v1/tasks/sample/dev/ddl/run";

    private static readonly byte[] NoBody = [];

    private static PackageTask Ddl(string name = "ddl") => SamplePackage.Task(name) with { Capabilities = [TaskCapabilities.Ddl] };

    private static byte[] Body(Dictionary<string, string>? inputs = null) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { inputs = inputs ?? [] }));

    private static string Approval(RunnerHarness h, Dictionary<string, string>? inputs = null, string task = "sample/dev/ddl", string statements = "CREATE TABLE t (id int);")
    {
        TaskRef.TryParse(task, out var reference);
        return ControlRunApprovals.Sign(h.Console.Id, h.Console.Key,
            new ControlRunRequest(h.InstanceId, reference!, null, inputs ?? [], ControlRunApprovals.StatementsDigest(statements), "ada", "Ada"));
    }

    private static Dictionary<string, string> With(string approval) => new() { [ControlRunApprovals.Header] = approval, ["Content-Type"] = "application/json" };

    [Fact]
    public async Task A_run_without_the_control_services_approval_is_refused_and_audited_and_with_it_runs()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(Ddl());
        await h.StartAsync();

        // the Runner's own screen: an operator's token and nothing else
        var (refused, body) = await h.PostAsync(Path, ServerRole.Operator, new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Contains("only from the console", (string?)body!["error"], StringComparison.Ordinal);
        Assert.Equal(AuditEvent.Refused, Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 20))).Entry.Outcome);
        Assert.Empty(await h.RunsAsync());

        // the console after the person approved
        var (accepted, run) = await h.PostRawAsync(Path, Body(), With(Approval(h)), ServerRole.Operator);
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        Assert.NotNull(run!["run_id"]);
        var detail = h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 20)).Select(e => e.Entry.Detail).First(d => d!.Contains("approved_by", StringComparison.Ordinal));
        Assert.Contains("Ada (ada)", detail, StringComparison.Ordinal);
        Assert.Contains(ControlRunApprovals.StatementsDigest("CREATE TABLE t (id int);"), detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_holds_for_one_run_of_exactly_the_task_and_inputs_it_names()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(Ddl(), Ddl("other"));
        await h.StartAsync();

        var inputs = new Dictionary<string, string> { ["plan"] = "p1" };
        var approval = Approval(h, inputs);

        // other inputs than the ones approved
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostRawAsync(Path, Body(new() { ["plan"] = "p2" }), With(approval), ServerRole.Operator)).Status);
        // another task
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostRawAsync("/api/v1/tasks/sample/dev/other/run", Body(inputs), With(approval), ServerRole.Operator)).Status);
        // garbage and a signature of another key
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostRawAsync(Path, Body(inputs), With("a.b.c"), ServerRole.Operator)).Status);
        var forged = ControlRunApprovals.Sign(h.Console.Id, ConsoleSigningKey.Generate(), new ControlRunRequest(h.InstanceId, new TaskRef("sample", "dev", "ddl"), null, inputs, ControlRunApprovals.StatementsDigest("x"), "ada", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostRawAsync(Path, Body(inputs), With(forged), ServerRole.Operator)).Status);

        // the one it names runs, once
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostRawAsync(Path, Body(inputs), With(approval), ServerRole.Operator)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostRawAsync(Path, Body(inputs), With(approval), ServerRole.Operator)).Status);
    }

    [Fact]
    public async Task A_dry_run_of_a_ddl_task_needs_no_approval_and_an_ordinary_task_never_does()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(Ddl(), SamplePackage.Task("plain"));
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync(Path, ServerRole.Operator, new { dry_run = true })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/tasks/sample/dev/plain/run", ServerRole.Operator, new { })).Status);
    }

    [Fact]
    public async Task The_status_marks_the_ddl_tasks_so_a_screen_shows_no_run_button_for_them()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(Ddl(), SamplePackage.Task("plain"));
        await h.StartAsync();

        var (_, status) = await h.GetAsync("/api/v1/status");
        var tasks = status!["tasks"]!.AsArray().ToDictionary(t => (string)t!["task"]!, t => (bool)t!["ddl"]!);
        Assert.True(tasks["ddl"]);
        Assert.False(tasks["plain"]);
    }

    [Fact]
    public void A_ddl_task_with_a_schedule_or_a_trigger_other_than_by_hand_does_not_validate()
    {
        var scheduled = SamplePackage.Manifest(Ddl() with { Schedule = Schedule.Interval(TimeSpan.FromMinutes(5)) });
        Assert.Contains(PackageManifestValidator.Validate(scheduled, "package.yaml"), d => d.Message.Contains("only a person approving", StringComparison.Ordinal));
        var triggered = SamplePackage.Manifest(Ddl() with { Triggers = [TaskTrigger.OnWebhook("shop")] });
        Assert.Contains(PackageManifestValidator.Validate(triggered, "package.yaml"), d => d.Message.Contains("only a person approving", StringComparison.Ordinal));
        Assert.DoesNotContain(PackageManifestValidator.Validate(SamplePackage.Manifest(Ddl() with { Triggers = [TaskTrigger.ByHand()] }), "package.yaml"), d => d.Message.Contains("only a person approving", StringComparison.Ordinal));
    }
}
