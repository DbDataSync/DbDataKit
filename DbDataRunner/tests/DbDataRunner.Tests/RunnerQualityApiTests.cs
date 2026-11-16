using System.Net;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 35 I3 and I3a: the runner answers the results of the checks its tasks evaluated in the shape the console's Quality page joins (<c>/api/v1/quality</c>), the history of one check, and an administrator's release of a block, audited.</summary>
public sealed class RunnerQualityApiTests
{
    private const string Dataset = "mssql://sql01/wh.dbo.orders";

    private static readonly DateTimeOffset At = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static async Task<RunnerHarness> WithResultsAsync()
    {
        var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var store = new QualityStore(h.State);
        store.Record(new CheckResultRecord("orders_id_unique", Dataset, CheckOutcome.Fail, At, 3, 0, ["order_id=7"], "0197f0e0-0000-7000-8000-0000000000aa", "block"));
        store.Record(new CheckResultRecord("orders_fresh", Dataset, CheckOutcome.Pass, At, 60, 3600, RunId: "0197f0e0-0000-7000-8000-0000000000aa"));
        return h;
    }

    [Fact]
    public async Task A_viewer_reads_every_check_with_its_latest_result_and_block_and_the_history_of_one()
    {
        await using var h = await WithResultsAsync();
        var (status, body) = await h.GetAsync("/api/v1/quality");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body!["can_run"]!);                                                   // plan 35 I3a: a re-check starts the package's `check` task
        Assert.True((bool)body["can_release"]!);
        Assert.Contains("`check` task", (string?)body["run_note"], StringComparison.Ordinal);
        var dataset = Assert.Single(body["datasets"]!.AsArray());
        Assert.Equal(Dataset, (string?)dataset!["dataset"]);
        Assert.Equal(1, (int)dataset["blocking"]!);
        Assert.Equal(["orders_fresh", "orders_id_unique"], dataset["checks"]!.AsArray().Select(c => (string?)c!["check_id"]));

        var (read, history) = await h.GetAsync($"/api/v1/quality/history?dataset={Uri.EscapeDataString(Dataset)}&check=orders_id_unique");
        Assert.Equal(HttpStatusCode.OK, read);
        var result = Assert.Single(history!["results"]!.AsArray());
        Assert.Equal("fail", (string?)result!["status"]);
        Assert.Equal("0197f0e0-0000-7000-8000-0000000000aa", (string?)result["run_id"]);
        Assert.Equal("order_id=7", (string?)result["sample_keys"]![0]);

        var (missing, _) = await h.GetAsync("/api/v1/quality/history?dataset=x");
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        var (nobody, _) = await h.GetAsync("/api/v1/quality", ServerRole.None);
        Assert.True(nobody is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_recheck_queues_the_check_task_of_the_package_whose_run_recorded_the_results()
    {
        var h = new RunnerHarness(StateEngineIds.Sqlite);
        await using var _ = h;
        h.Install(SamplePackage.Task("ok"), SamplePackage.Task("check") with { Inputs = [new TaskInput("dataset", Default: "")] });
        await h.StartAsync();
        var store = new QualityStore(h.State);
        var loaded = h.Runs.Start("sample/dev/ok", package: "sample", channel: "dev");
        store.Record(new CheckResultRecord("orders_id_unique", Dataset, CheckOutcome.Fail, At, 3, 0, ["order_id=7"], loaded.ToString("D"), "block"));
        var stranger = h.Runs.Start("other/dev/load", package: "other", channel: "dev");
        store.Record(new CheckResultRecord("x_present", "mssql://sql01/wh.dbo.other", CheckOutcome.Fail, At, 1, 0, null, stranger.ToString("D"), "block"));

        var (viewer, _) = await h.PostAsync("/api/v1/quality/run", ServerRole.Viewer, new { dataset = Dataset });
        Assert.Equal(HttpStatusCode.Forbidden, viewer);
        Assert.Equal(AuditEvent.Refused, Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerQualityApi.RunVerb))).Entry.Outcome);

        var (accepted, body) = await h.PostAsync("/api/v1/quality/run", ServerRole.Operator, new { dataset = Dataset, on_behalf_of = "Ada via Main console" });
        Assert.Equal(HttpStatusCode.Accepted, accepted);
        Assert.Equal("sample/dev/check", (string?)body!["task"]);
        var detail = Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 20))).Entry.Detail;
        Assert.Contains("\"dataset\"", detail, StringComparison.Ordinal);                     // the input's name, never its value
        Assert.DoesNotContain("sql01", detail, StringComparison.Ordinal);

        var (again, _) = await h.PostAsync("/api/v1/quality/run", ServerRole.Operator, new { dataset = Dataset });
        Assert.Equal(HttpStatusCode.Conflict, again);                                          // queued already
        var (none, _) = await h.PostAsync("/api/v1/quality/run", ServerRole.Operator, new { dataset = "mssql://sql01/wh.dbo.unknown" });
        Assert.Equal(HttpStatusCode.NotFound, none);
        var (noTask, refusal) = await h.PostAsync("/api/v1/quality/run", ServerRole.Operator, new { dataset = "mssql://sql01/wh.dbo.other" });
        Assert.Equal(HttpStatusCode.Conflict, noTask);                                         // the package that recorded it has no `check`
        Assert.Contains("other", (string?)refusal!["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_releases_a_block_and_it_is_audited_and_an_operator_is_refused()
    {
        await using var h = await WithResultsAsync();
        var (refused, _) = await h.PostAsync("/api/v1/quality/release", ServerRole.Operator, new { dataset = Dataset, check = "orders_id_unique" });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.True(new QualityStore(h.State).Status(Dataset, "orders_id_unique")!.Blocking);

        var (status, body) = await h.PostAsync("/api/v1/quality/release", ServerRole.Admin, new { dataset = Dataset, check = "orders_id_unique" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body!["released"]!);
        var after = new QualityStore(h.State).Status(Dataset, "orders_id_unique")!;
        Assert.False(after.Blocking);
        Assert.NotNull(after.ReleasedSince);

        var (again, _) = await h.PostAsync("/api/v1/quality/release", ServerRole.Admin, new { dataset = Dataset, check = "orders_id_unique" });
        Assert.Equal(HttpStatusCode.Conflict, again);
        var audit = h.Audit.Read(new AuditQuery(VerbPrefix: "runner.quality.")).Select(r => r.Entry.Outcome).ToList();
        Assert.Equal(2, audit.Count(o => o == AuditEvent.Refused));
        Assert.Equal(1, audit.Count(o => o == AuditEvent.Ok));
    }
}
