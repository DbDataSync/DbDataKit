using System.Net;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using DbDataKit.Tracking.Build;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>The runner's management API over its own copy of Build's tracking (plan 28c E18, owner decision 2026-11-11): the document, and the resolve of a divergence by an administrator under the Build server's rules, audited.</summary>
public sealed class RunnerTrackingApiTests
{
    private const string Identity = "buildtracking:mssql://sql01/wh.dbdatabuild.tracking@sales";

    private static async Task<(RunnerHarness Harness, long Id)> WithDivergenceAsync()
    {
        var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var store = new BuildTrackingStore(h.State);
        var stream = BuildStreams.Get("run_log").Stream;
        await store.WriteAsync(Identity, stream, [new TrackingRow("a|1", 10, "{\"status\":\"ok\"}"), new TrackingRow("a|2", 20, "{\"status\":\"ok\"}"), new TrackingRow("a|3", 30, "{\"status\":\"ok\"}")], default);
        var divergence = new StateTrackingDivergences(h.State).Mark(Identity, "run_log", "the target has fewer rows than the copy", new TrackingPosition(10, "a|1"), new TrackingPosition(15, "a|x"), new TrackingPosition(30, "a|3"));
        return (h, divergence.Id);
    }

    [Fact]
    public async Task A_viewer_reads_the_copy_and_its_divergence_with_the_common_point()
    {
        var (h, id) = await WithDivergenceAsync();
        await using var _ = h;
        var (status, body) = await h.GetAsync("/api/v1/tracking");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("runner", (string?)body!["holder"]);
        var divergence = Assert.Single(body["divergences"]!.AsArray());
        Assert.Equal(id, (long)divergence!["id"]!);
        Assert.True((bool)divergence["open"]!);
        Assert.Equal("a|1", (string?)divergence["common"]!["key"]);
        var copy = Assert.Single(body["copies"]!.AsArray());
        Assert.Equal(Identity, (string?)copy!["identity"]);
        Assert.Equal(3, (int)copy["streams"]!.AsArray().Single(s => (string?)s!["stream"] == "run_log")!["rows"]!);
    }

    [Fact]
    public async Task An_administrator_archives_the_later_rows_and_it_is_audited_and_a_second_resolve_is_refused()
    {
        var (h, id) = await WithDivergenceAsync();
        await using var _ = h;
        var (status, body) = await h.PostAsync($"/api/v1/tracking/divergences/{id}/resolve", ServerRole.Admin, new { resolution = "archive" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body!["resolved"]!);
        Assert.False(new StateTrackingDivergences(h.State).List().Single().IsOpen);
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: "runner.tracking.")), r => r.Entry.Verb == "runner.tracking.resolve" && r.Entry.Outcome == AuditEvent.Ok);

        var (again, _) = await h.PostAsync($"/api/v1/tracking/divergences/{id}/resolve", ServerRole.Admin, new { resolution = "reset" });
        Assert.Equal(HttpStatusCode.Conflict, again);
        var (missing, _) = await h.PostAsync("/api/v1/tracking/divergences/999/resolve", ServerRole.Admin, new { resolution = "reset" });
        Assert.Equal(HttpStatusCode.NotFound, missing);
        var (bad, _) = await h.PostAsync($"/api/v1/tracking/divergences/{id}/resolve", ServerRole.Admin, new { resolution = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    [Fact]
    public async Task An_operator_cannot_resolve_and_the_refusal_is_audited_and_reset_deletes_the_later_rows()
    {
        var (h, id) = await WithDivergenceAsync();
        await using var _ = h;
        var (refused, _) = await h.PostAsync($"/api/v1/tracking/divergences/{id}/resolve", ServerRole.Operator, new { resolution = "reset" });
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.True(new StateTrackingDivergences(h.State).List().Single().IsOpen);
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: "runner.tracking.")), r => r.Entry.Verb == "runner.tracking.resolve" && r.Entry.Outcome == AuditEvent.Refused);

        var (status, _) = await h.PostAsync($"/api/v1/tracking/divergences/{id}/resolve", ServerRole.Admin, new { resolution = "reset" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, new BuildTrackingStore(h.State).Count(Identity, "run_log"));
    }
}
