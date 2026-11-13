using System.Net;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E12g over the management API of a real server: <c>POST .../checkpoints/{name}/set</c> changes some parts of one checkpoint item by hand (a product's recovery actions), only from the version the caller read, as a new version
/// under the caller's name, audited with before and after, for an operator; the paired client's <c>SetCheckpointAsync</c> says a moved item as null.
/// </summary>
public sealed class CheckpointSetApiTests
{
    private const string Base = "/api/v1/tasks/sample/dev/ok";

    private static object Body(long version, object? set = null, string[]? remove = null, string key = "k1") => new { key, version, set, remove };

    [Fact]
    public async Task Parts_are_set_and_removed_from_the_version_read_the_rest_stay_and_the_change_is_audited_with_before_and_after()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var progress = new ProgressStore(h.State, new ProgressKey("sample", "dev", "ok"));
        Assert.True(progress.Advance("watermark", new Dictionary<string, ProgressValue>
        {
            ["position"] = ProgressValue.OfText("0x9F"), ["hold"] = ProgressValue.OfText("Loading"), ["pending_position"] = ProgressValue.OfText("0xAA"),
        }, 0, itemKey: "k1"));

        var (status, body) = await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator,
            Body(1, new { hold = new { type = "text", value = "None" }, position_time = new { type = "time", value = "2026-11-10T08:00:00Z" } }, ["pending_position"]));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, (long)body!["version"]!);
        var now = progress.GetCheckpoint("watermark", "k1")!;
        Assert.Equal(("0x9F", "None"), (now.Parts["position"].AsText, now.Parts["hold"].AsText));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero), now.Parts["position_time"].AsTime);
        Assert.DoesNotContain("pending_position", now.Parts.Keys);
        Assert.Equal("Ada", now.UpdatedBy);
        var audit = h.Audit.Read(new AuditQuery(Verb: RunnerControl.SetCheckpointVerb, Limit: 20));
        Assert.Single(audit, r => r.Entry.Outcome == AuditEvent.Ok && r.Entry.TargetId == "sample/dev/ok" && r.Entry.Detail!.Contains("pending_position") && r.Entry.Detail.Contains("Loading"));
    }

    [Fact]
    public async Task A_stale_version_is_a_409_that_changes_nothing_an_item_that_does_not_exist_is_made_from_version_0_and_a_viewer_is_refused()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var progress = new ProgressStore(h.State, new ProgressKey("sample", "dev", "ok"));
        Assert.True(progress.Advance("watermark", new Dictionary<string, ProgressValue> { ["hold"] = ProgressValue.OfText("None") }, 0, itemKey: "k1"));
        var set = new { hold = new { type = "text", value = "Paused" } };

        var stale = await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, Body(0, set));
        var made = await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, Body(0, set, key: "k2"));
        var viewer = await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Viewer, Body(1, set));

        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("None", progress.GetCheckpoint("watermark", "k1")!.Parts["hold"].AsText);
        Assert.Equal(HttpStatusCode.OK, made.Status);
        Assert.Equal("Paused", progress.GetCheckpoint("watermark", "k2")!.Parts["hold"].AsText);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.Status);
        Assert.Contains(h.Audit.Read(new AuditQuery(Verb: RunnerControl.SetCheckpointVerb, Limit: 20)), r => r.Entry.Outcome == AuditEvent.Failed);
    }

    [Fact]
    public async Task A_body_that_is_not_the_shape_is_refused_in_words()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, new { key = "k1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, Body(0, new { hold = new { type = "int", value = "x" } }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, Body(0, new Dictionary<string, object> { ["bad part"] = new { type = "text", value = "x" } }))).Status);   // not a part name
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/watermark/set", ServerRole.Operator, Body(0))).Status);   // nothing would be left
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Post, Base + "/checkpoints/Bad%20Name/set", ServerRole.Operator, Body(0))).Status);
    }

    [Fact]
    public async Task The_paired_client_changes_parts_and_is_told_null_when_the_item_moved()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        new ProgressStore(h.State, new ProgressKey("sample", "dev", "ok")).Advance("watermark", new Dictionary<string, ProgressValue> { ["position"] = ProgressValue.OfText("17") }, 0, itemKey: "k1");
        var directory = Directory.CreateTempSubdirectory("dbdr-set-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Operator);
            var set = new Dictionary<string, ClientPartValue> { ["hold"] = new("text", "Paused") };

            var moved = await client.SetCheckpointAsync(server, code, "sample", "dev", "ok", "watermark", "k1", 7, set, []);
            var done = await client.SetCheckpointAsync(server, null, "sample", "dev", "ok", "watermark", "k1", 1, set, []);

            Assert.Null(moved);
            Assert.Equal(2, done);
            var items = await client.CheckpointAsync(server, null, "sample", "dev", "ok", "watermark");
            Assert.Equal(("17", "Paused"), (items.Single().Parts["position"], items.Single().Parts["hold"]));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* temp */ }
        }
    }
}
