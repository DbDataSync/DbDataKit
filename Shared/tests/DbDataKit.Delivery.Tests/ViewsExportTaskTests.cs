using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.State;
using DbDataKit.State.Tests;

namespace DbDataKit.Delivery.Tests;

/// <summary>Plan 32 DBR18: the system package <code>views-export</code> and its task <code>subscriptions</code>.</summary>
public sealed class ViewsExportTaskTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static readonly TaskRef Subscriptions = new(ViewsExportTasks.Package, "prod", ViewsExportTasks.Task);

    private static TaskRunRequest Request(Dictionary<string, string>? inputs = null) => new()
    {
        Task = Subscriptions, PackageDirectory = "/tmp", PackageVersion = "1", Launch = new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"), Chain = Guid.NewGuid().ToString("N"), Inputs = inputs ?? [],
    };

    private static SubscriptionFields Fields(string name) =>
        new(name, "report", "summary", new JsonObject { ["region"] = new JsonArray("east") }, "print", "pdf", null, "mail", null, "0 7 * * *", "UTC");

    private static (ViewsExportTasks Tasks, SubscriptionStore Store, RunStore Runs, FakeRenderer Renderer) Build(DeliveryHarness h, Func<SubscriptionRender, RenderResult> render)
    {
        var channel = new ScriptedChannel("mail", _ => DeliveryResult.Sent());
        var renderer = new FakeRenderer(render);
        var store = new SubscriptionStore(h.Database);
        var service = new SubscriptionService(store, h.Service(new ChannelSet(channel), "sender"), renderer, new FakeDirectory(), h.Clock);
        return (new ViewsExportTasks(service, h.Database), store, new RunStore(h.Database, "runner"), renderer);
    }

    private static RenderResult.Rendered Ok(string text) => new(System.Text.Encoding.UTF8.GetBytes(text), "application/pdf", "summary-print.pdf", "Quarterly summary", "The summary is attached.", null, []);

    [Fact]
    public void The_manifest_is_a_valid_package_with_one_task_a_schedule_and_the_subscription_input()
    {
        var manifest = PackageManifestReader.Load(ViewsExportTasks.Manifest(new ViewsExportSettings(TimeSpan.FromMinutes(2)))).ThrowIfInvalid();
        Assert.Equal("views-export", manifest.Name);
        var task = Assert.Single(manifest.Tasks);
        Assert.Equal(("subscriptions", TimeSpan.FromMinutes(2)), (task.Name, task.Schedule.Every));
        var input = Assert.Single(task.Inputs);
        Assert.Equal(("subscription", false), (input.Name, input.Required));
        Assert.Equal(TimeSpan.FromMinutes(1), PackageManifestReader.Load(ViewsExportTasks.Manifest()).ThrowIfInvalid().Tasks[0].Schedule.Every);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_task_takes_the_due_subscriptions_renders_each_as_its_subscriber_and_the_outbox_holds_what_was_sent(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (tasks, store, runs, renderer) = Build(h, r => Ok($"rows for {r.Subscriber}"));
        store.Create(Fields("Summary (Ana)"), "ana", "Ana Lopez", "ana", DeliveryHarness.Start);
        store.Create(Fields("Summary (Bo)"), "bo", "Bo Chen", "bo", DeliveryHarness.Start);

        var result = await tasks.RunAsync(Request(), default);
        Assert.Equal(TaskEndState.Succeeded, result.End.State);
        Assert.Equal(2, result.End.Processed);
        Assert.Equal(["ana", "bo"], renderer.Asked.Select(a => a.Subscriber).Order());
        Assert.All(renderer.Asked, a => Assert.Equal("print", a.Layout));
        var run = runs.Get(result.RunId)!;
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Succeeded, run.Run.Outcome);
        Assert.Equal(ViewsExportTasks.Package, run.Package);
        Assert.Equal(2, h.Store.List().Count(m => m.Source!.StartsWith("subscription:")));
        Assert.All(store.List(), s => Assert.Equal(result.RunId.ToString("D"), store.Runs(s.Id).Single().RunId));       // each run row names the runner's run

        var again = await tasks.RunAsync(Request(), default);                                                                // nothing is due now
        Assert.Equal((TaskEndState.Succeeded, 0), (again.End.State, again.End.Processed));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_input_sends_one_subscription_now_and_an_unknown_one_fails_the_run(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (tasks, store, _, renderer) = Build(h, r => Ok("rows"));
        var id = store.Create(Fields("Summary"), "ana", "Ana Lopez", "ana", DeliveryHarness.Start.AddDays(5));            // not due for days

        var now = await tasks.RunAsync(Request(new() { ["subscription"] = id.ToString("D") }), default);
        Assert.Equal((TaskEndState.Succeeded, 1), (now.End.State, now.End.Processed));
        Assert.Single(renderer.Asked);
        Assert.Equal(DeliveryHarness.Start.AddDays(5), store.Find(id)!.NextRunAt);                                           // the schedule did not move

        var unknown = await tasks.RunAsync(Request(new() { ["subscription"] = Guid.NewGuid().ToString("D") }), default);
        Assert.Equal(TaskEndState.Failed, unknown.End.State);
        Assert.Contains("There is no subscription", unknown.End.Failure!.Message);
        var bad = await tasks.RunAsync(Request(new() { ["subscription"] = "not-an-id" }), default);
        Assert.Contains("is a subscription's id", bad.End.Failure!.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_subscriber_who_lost_access_is_a_result_and_a_failing_render_completes_with_failures(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var (tasks, store, _, _) = Build(h, r => r.Subscriber switch
        {
            "bo" => new RenderResult.AccessLost("Bo Chen is no longer in the group."),
            "ana" => new RenderResult.Failed("export_timeout", "the export did not finish in 60 seconds"),
            _ => Ok("rows"),
        });
        store.Create(Fields("Ana's"), "ana", "Ana Lopez", "ana", DeliveryHarness.Start);
        store.Create(Fields("Bo's"), "bo", "Bo Chen", "admin", DeliveryHarness.Start);

        var result = await tasks.RunAsync(Request(), default);
        Assert.Equal(TaskEndState.CompletedWithFailures, result.End.State);
        Assert.Equal((2, 1), (result.End.Processed, result.End.FailedItems));
        Assert.Contains("Ana's", result.End.FirstFailedItem);
        Assert.Equal("stopped", store.List().Single(s => s.Fields.Name == "Bo's").Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_host_without_a_browser_fails_the_task_with_the_setting_named(string engine)
    {
        using var h = new DeliveryHarness(engine);
        var words = "Export of the report 'summary' is unavailable: export unavailable: it is off on this host (the views.export setting is not set).";
        var (tasks, store, runs, _) = Build(h, _ => new RenderResult.Failed("unavailable", words));
        store.Create(Fields("Summary"), "ana", "Ana Lopez", "ana", DeliveryHarness.Start);

        var result = await tasks.RunAsync(Request(), default);
        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.Permanent, result.End.Failure!.Kind);
        Assert.Contains("This host cannot export", result.End.Failure.Message);
        Assert.Contains("the views.export setting is not set", result.End.Failure.Message);
        Assert.Equal(DbDataKit.Monitoring.RunOutcome.Failed, runs.Get(result.RunId)!.Run.Outcome);
        Assert.Contains("the views.export setting is not set", store.List().Single().StatusDetail);                           // the subscription shows the same problem
    }
}
