using System.Net;
using DbDataKit.Runner.Client;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E12c over the management API of a real server: a task's checkpoint as the runner holds it (<c>.../checkpoints/{name}</c>, what a product reads to take a position back when it schedules the task again)
/// and an item for the task's own queue (<c>.../work</c>, how a product hands work to a run that is up already), the roles they need, and the paired client's calls.
/// </summary>
public sealed class TaskWorkAndCheckpointApiTests
{
    private const string Base = "/api/v1/tasks/sample/dev/ok";

    [Fact]
    public async Task A_checkpoint_is_read_as_text_by_an_operator_and_not_by_a_viewer()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var progress = new ProgressStore(h.State, new ProgressKey("sample", "dev", "ok"));
        var at = new DateTimeOffset(2026, 11, 9, 11, 30, 0, TimeSpan.Zero);
        Assert.True(progress.Advance("watermark", new Dictionary<string, ProgressValue>
        {
            ["position"] = ProgressValue.OfText("0x9F"), ["position_time"] = ProgressValue.OfTime(at), ["hold"] = ProgressValue.OfText("None"),
        }, expectedVersion: 0, itemKey: "src.db.dbo.orders"));

        var (status, body) = await h.CallAsync(HttpMethod.Get, Base + "/checkpoints/watermark", ServerRole.Operator);

        Assert.Equal(HttpStatusCode.OK, status);
        var item = Assert.Single(body!["items"]!.AsArray())!;
        Assert.Equal("src.db.dbo.orders", (string)item["key"]!);
        Assert.Equal(("0x9F", "2026-11-09T11:30:00.0000000Z", "None"), ((string)item["parts"]!["position"]!, (string)item["parts"]!["position_time"]!, (string)item["parts"]!["hold"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Get, Base + "/checkpoints/watermark", ServerRole.Viewer)).Status);
        Assert.Empty((await h.CallAsync(HttpMethod.Get, Base + "/checkpoints/nothing", ServerRole.Operator)).Body!["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Get, Base + "/checkpoints/Bad%20Name", ServerRole.Operator)).Status);
    }

    [Fact]
    public async Task Work_for_the_tasks_queue_is_queued_by_an_operator_under_the_tasks_own_owner_and_refused_when_it_is_not_one()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var (status, body) = await h.CallAsync(HttpMethod.Post, Base + "/work", ServerRole.Operator, new { kind = "Request", subject = "orders", part = "r1", payload = "{\"kind\":\"Verification\"}" });

        Assert.Equal(HttpStatusCode.Accepted, status);
        var items = new ModuleWorkQueueStore(h.State, "runner.work").ListAll("sample/dev/ok");
        var item = Assert.Single(items);
        Assert.Equal(("Request", "orders", "r1", "{\"kind\":\"Verification\"}", WorkItemStatus.Pending), (item.Kind, item.Subject, item.Part, item.PayloadJson, item.Status));
        Assert.Equal(item.RunId.ToString(), (string)body!["run_id"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.CallAsync(HttpMethod.Post, Base + "/work", ServerRole.Viewer, new { kind = "Request", subject = "orders" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/work", ServerRole.Operator, new { kind = "Request" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, "/api/v1/tasks/Sample/dev/ok/work", ServerRole.Operator, new { kind = "Request", subject = "x" })).Status);
        Assert.Single(new ModuleWorkQueueStore(h.State, "runner.work").ListAll("sample/dev/ok"));
    }

    [Fact]
    public async Task The_paired_client_reads_a_checkpoint_and_queues_work()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        new ProgressStore(h.State, new ProgressKey("sample", "dev", "ok")).Advance("watermark", new Dictionary<string, ProgressValue> { ["position"] = ProgressValue.OfText("17") }, 0, itemKey: "k1");
        var directory = Directory.CreateTempSubdirectory("dbdr-work-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Operator);

            var items = await client.CheckpointAsync(server, code, "sample", "dev", "ok", "watermark");
            await client.EnqueueWorkAsync(server, null, "sample", "dev", "ok", "Request", "orders", "r2", "{}");

            var one = Assert.Single(items);
            Assert.Equal(("k1", "17"), (one.Key, one.Parts["position"]));
            Assert.Equal("r2", Assert.Single(new ModuleWorkQueueStore(h.State, "runner.work").ListAll("sample/dev/ok")).Part);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* temp */ }
        }
    }
}
