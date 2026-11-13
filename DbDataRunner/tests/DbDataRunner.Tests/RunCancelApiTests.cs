using System.Net;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E12g over the management API of a real server: <c>POST /api/v1/runs/{id}/cancel</c> stops one run of a task process that is really running (the run ends as stopped and is not retried), needs the operator role, answers 404 for a run
/// the queue does not know and 409 for one that ended, and is on the audit; and the paired client's <c>CancelRunAsync</c> says the same in booleans.
/// </summary>
public sealed class RunCancelApiTests
{
    private static string Text(System.Text.Json.Nodes.JsonNode? node, string key) => (string)node![key]!;

    [Fact]
    public async Task A_run_in_flight_is_stopped_by_an_operator_is_not_retried_and_is_on_the_audit()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("waits", limits: new DbDataKit.Runner.TaskLimits(Attempts: 3)));
        await h.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/tasks/sample/dev/waits/run")).Status);
        var runId = Guid.Parse(Text(await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&outcome=running")).FirstOrDefault(), "the run to start"), "id"));
        await h.WaitAsync(() => Task.FromResult(h.Runs.Logs(runId).Any(l => l.Message == "ready") ? "ready" : null), "the task to be ready");

        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostAsync($"/api/v1/runs/{runId}/cancel", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync($"/api/v1/runs/{Guid.NewGuid()}/cancel")).Status);
        var (status, body) = await h.PostAsync($"/api/v1/runs/{runId}/cancel");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(("sample/dev/waits", true), (Text(body, "task"), (bool)body!["cancelled"]!));
        await h.WaitAsync(() => Task.FromResult(h.Runs.Get(runId)?.Run.Outcome == RunOutcome.Stopped ? "stopped" : null), "the run to stop");
        await Task.Delay(1500);   // a retry would have queued a second attempt by now
        Assert.Empty(await h.RunsAsync("?package=sample&outcome=running"));
        Assert.Single(await h.RunsAsync("?package=sample&channel=dev&task=waits"));
        var again = await h.PostAsync($"/api/v1/runs/{runId}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        var audit = h.Audit.Read(new AuditQuery(Verb: RunnerControl.CancelVerb, Limit: 20));
        Assert.Contains(audit, r => r.Entry.Outcome == AuditEvent.Ok && r.Entry.TargetId == "sample/dev/waits" && r.Entry.Detail!.Contains(runId.ToString()));
    }

    [Fact]
    public async Task The_paired_client_cancels_a_run_and_is_told_false_for_a_run_that_is_not_there()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("waits"));
        await h.StartAsync();
        var directory = Directory.CreateTempSubdirectory("dbdr-cancel-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Operator);
            var runId = Guid.Parse((await client.RunNowAsync(server, code, "sample", "dev", "waits"))!);
            await h.WaitAsync(() => Task.FromResult(h.Runs.Logs(runId).Any(l => l.Message == "ready") ? "ready" : null), "the task to be ready");

            Assert.True(await client.CancelRunAsync(server, null, runId));
            Assert.False(await client.CancelRunAsync(server, null, Guid.NewGuid()));
            await h.WaitAsync(() => Task.FromResult(h.Runs.Get(runId)?.Run.Outcome == RunOutcome.Stopped ? "stopped" : null), "the run to stop");
            Assert.False(await client.CancelRunAsync(server, null, runId));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* temp */ }
        }
    }
}
