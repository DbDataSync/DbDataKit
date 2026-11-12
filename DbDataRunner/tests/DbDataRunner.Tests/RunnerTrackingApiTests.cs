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

    [Fact]
    public async Task The_copy_of_a_runner_owned_replications_units_is_listed_beside_builds_and_resolved_the_same_way()
    {
        var (h, buildId) = await WithDivergenceAsync();
        await using var _ = h;
        const string unitIdentity = "syncunit:postgres://pg01/wh/orders-sync@warehouse";
        var rows = Enumerable.Range(1, 3).Select(n => new TrackingRow($"0000000{n}-0000-0000-0000-000000000000", n, $"{{\"unit_seq\":{n},\"committed_at\":\"2026-11-12T10:00:0{n}.0000000+00:00\"}}")).ToArray();
        await new StateTrackingStore(h.State).WriteAsync(unitIdentity, SyncUnitCopy.Stream, rows, default);
        var unit = new StateTrackingDivergences(h.State).Mark(unitIdentity, SyncUnitCopy.Name, "the copy holds rows of 'unit' that the primary does not", new TrackingPosition(1, rows[0].Key), new TrackingPosition(2, "x"), new TrackingPosition(3, rows[2].Key));

        var (status, body) = await h.GetAsync("/api/v1/tracking");
        Assert.Equal(HttpStatusCode.OK, status);
        var copies = body!["copies"]!.AsArray();
        Assert.Equal(2, copies.Count);
        var copy = copies.Single(c => (string?)c!["identity"] == unitIdentity)!;
        Assert.Equal("orders-sync", (string?)copy["replication"]);
        Assert.Equal(3, (int)copy["streams"]![0]!["rows"]!);
        Assert.True((bool)copy["streams"]![0]!["diverged"]!);
        Assert.Equal(2, body["divergences"]!.AsArray().Count);
        Assert.Equal(unit.Id > buildId ? unit.Id : buildId, (long)body["divergences"]![0]!["id"]!);   // newest first across both kinds
        Assert.Equal("2026-11-12T10:00:01.0000000+00:00", (string?)body["divergences"]!.AsArray().Single(d => (long)d!["id"]! == unit.Id)!["common"]!["time"]);

        var (resolved, answer) = await h.PostAsync($"/api/v1/tracking/divergences/{unit.Id}/resolve", ServerRole.Admin, new { resolution = "reset" });
        Assert.Equal(HttpStatusCode.OK, resolved);
        Assert.Equal(unit.Id, (long)answer!["divergence"]!["id"]!);
        Assert.Equal(1, new StateTrackingStore(h.State).Count(unitIdentity, SyncUnitCopy.Name));
        Assert.Contains(h.Audit.Read(new AuditQuery(VerbPrefix: "runner.tracking.")), r => r.Entry.Verb == "runner.tracking.resolve" && r.Entry.Outcome == AuditEvent.Ok);
        Assert.True(new StateTrackingDivergences(h.State).List().Single(d => d.Id == buildId).IsOpen);   // Build's own is untouched
    }
}
