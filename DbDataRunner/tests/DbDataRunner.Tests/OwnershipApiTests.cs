using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E09 over the management API of a real server: who schedules a task (<c>.../schedule</c>, <c>.../owner</c>, <c>.../decisions</c>), the roles those calls need, the audit of a change, and the paired client a Build server
/// uses (<see cref="RunnerPublishClient"/>). The scheduling itself, with a fake clock, is in the kit's <c>OwnershipTests</c>.
/// </summary>
public sealed class OwnershipApiTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private const string Schedule = "/api/v1/tasks/sample/dev/ok/schedule";
    private const string Owner = "/api/v1/tasks/sample/dev/ok/owner";

    [Theory, MemberData(nameof(Engines))]
    public async Task An_operator_changes_the_owner_with_the_position_and_it_is_read_back_audited_and_in_the_status(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var (_, before) = await h.GetAsync(Schedule);
        Assert.Equal(("runner", null, "1"), ((string)before!["owner"]!, (string?)before["owner_set_by"], (string?)before["version"]));   // nobody handed it over: the runner's

        var (put, _) = await h.CallAsync(HttpMethod.Put, Owner, ServerRole.Operator, new { owner = "runner", reason = "cutover", handled_up_to = "2026-11-07T12:00:00Z", paused_by = "bob", paused_at = "2026-11-07T11:00:00Z" }, "ada");
        Assert.Equal(HttpStatusCode.OK, put);

        var (_, after) = await h.GetAsync(Schedule);
        Assert.Equal(("runner", "Ada", "cutover", "2026-11-07T12:00:00.0000000Z", true, "bob"),
            ((string)after!["owner"]!, (string?)after["owner_set_by"], (string?)after["reason"], (string?)after["handled_up_to"], (bool)after["paused"]!, (string?)after["paused_by"]));
        var task = (await h.GetAsync("/api/v1/status")).Body!["tasks"]![0]!;
        Assert.Equal("runner", (string)task["owner"]!);

        var entry = h.Audit.Read(new AuditQuery(VerbPrefix: RunnerControl.OwnerVerb)).Single();
        Assert.Equal(("ada", "sample/dev/ok", AuditEvent.Ok), (entry.Entry.ActorId, entry.Entry.TargetId, entry.Entry.Outcome));
        var detail = JsonNode.Parse(entry.Entry.Detail!)!;       // a JSON column may reformat the text, so it is read as JSON
        Assert.Equal(("runner", "runner", "cutover", "bob"), ((string)detail["from"]!, (string)detail["to"]!, (string)detail["reason"]!, (string)detail["paused_by"]!));
    }

    [Fact]
    public async Task A_viewer_reads_but_cannot_change_and_a_bad_body_is_refused_with_the_reason()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();

        var (viewerPut, _) = await h.CallAsync(HttpMethod.Put, Owner, ServerRole.Viewer, new { owner = "old" });
        var (viewerGet, _) = await h.CallAsync(HttpMethod.Get, Schedule, ServerRole.Viewer);
        var (viewerReport, _) = await h.CallAsync(HttpMethod.Post, "/api/v1/tasks/sample/dev/ok/decisions", ServerRole.Viewer, new { at = "2026-11-07T12:00:00Z" });
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.OK, HttpStatusCode.Forbidden), (viewerPut, viewerGet, viewerReport));
        Assert.Equal(AuditEvent.Refused, h.Audit.Read(new AuditQuery(VerbPrefix: RunnerControl.OwnerVerb)).First().Entry.Outcome);

        var (bad, badBody) = await h.CallAsync(HttpMethod.Put, Owner, ServerRole.Operator, new { owner = "both" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("old, observe, runner", (string)badBody!["error"]!);
        var (orphanPause, pauseBody) = await h.CallAsync(HttpMethod.Put, Owner, ServerRole.Operator, new { owner = "runner", paused_by = "bob" });
        Assert.Equal(HttpStatusCode.BadRequest, orphanPause);
        Assert.Contains("handled_up_to", (string)pauseBody!["error"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, Owner, ServerRole.Operator, new { owner = "runner", handled_up_to = "tomorrow-ish" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.CallAsync(HttpMethod.Put, "/api/v1/tasks/Sample/dev/ok/owner", ServerRole.Operator, new { owner = "old" })).Status);   // not a name
        Assert.DoesNotContain(h.Audit.Read(new AuditQuery(VerbPrefix: RunnerControl.OwnerVerb)), e => e.Entry.Outcome == AuditEvent.Ok);
    }

    [Fact]
    public async Task A_task_can_be_named_before_it_has_a_version_and_says_so()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();

        var (put, _) = await h.CallAsync(HttpMethod.Put, "/api/v1/tasks/future/dev/job/owner", ServerRole.Operator, new { owner = "old", reason = "published by project publish" });
        var (_, state) = await h.GetAsync("/api/v1/tasks/future/dev/job/schedule");

        Assert.Equal(HttpStatusCode.OK, put);
        Assert.Equal(("old", null), ((string)state!["owner"]!, (string?)state["version"]));      // the owner is recorded, and nothing is active there yet
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_paired_client_a_build_server_uses_reads_changes_and_reports_and_a_viewer_pairing_is_refused_in_words(string engine)
    {
        await using var h = new RunnerHarness(engine);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var directory = Directory.CreateTempSubdirectory("dbdr-own-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Operator);

            var first = await client.ScheduleOfAsync(server, code, "sample", "dev", "ok");
            Assert.Equal(("runner", "1"), (first.Owner, first.Version));

            var handed = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
            await client.SetOwnerAsync(server, null, "sample", "dev", "ok", "observe", "to compare", new ClientSeed(handed, "bob", handed.AddHours(-1)));
            await client.ReportDecisionAsync(server, null, "sample", "dev", "ok", handed.AddMinutes(15), 0, false);
            var decisions = await client.DecisionsAsync(server, null, "sample", "dev", "ok", handed);
            Assert.Equal([("old", handed.AddMinutes(15), false)], decisions.Select(d => (d.Source, d.At, d.Paused)));

            await client.SetOwnerAsync(server, null, "sample", "dev", "ok", "runner", "cutover", new ClientSeed(handed, null, null));
            var now = await client.ScheduleOfAsync(server, null, "sample", "dev", "ok");
            Assert.Equal(("runner", handed, false, 0, 0), (now.Owner, now.HandledUpTo, now.Paused, now.Running, now.Queued));
            Assert.NotNull(now.OwnerSetBy);

            // a pairing that may only read: the runner's own words
            var viewerDirectory = Directory.CreateTempSubdirectory("dbdr-own-viewer-").FullName;
            try
            {
                var viewer = new RunnerPublishClient(new RunnerClientStore(viewerDirectory), _ => null);
                await viewer.ScheduleOfAsync(server, h.NewCode(ServerRole.Viewer), "sample", "dev", "ok");
                var refused = await Assert.ThrowsAsync<RunnerClientException>(() => viewer.SetOwnerAsync(server, null, "sample", "dev", "ok", "old", null, null));
                Assert.Contains("operator role", refused.Message);
            }
            finally { try { Directory.Delete(viewerDirectory, true); } catch (IOException) { } }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
