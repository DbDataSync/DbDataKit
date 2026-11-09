using System.Net;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E13 over the management API of a real server: one run and its log (<c>GET /api/v1/runs/{id}</c> and <c>.../logs</c>), which a product reads for the runs of its own task (the live view reads a run that is going), and the steps
/// a task's runs recorded (<c>.../steps/{name}</c>, where Sync's drains keep their pass records and the polling gate's checks). A viewer reads all of them, as it reads the run list.
/// </summary>
public sealed class RunReadApiTests
{
    private static string Text(System.Text.Json.Nodes.JsonNode? node, string name) => (string)node![name]!;

    [Fact]
    public async Task A_runs_log_is_read_in_order_by_a_viewer_a_page_at_a_time_and_an_unknown_run_is_not_found()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var (queued, started) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run");
        Assert.Equal(HttpStatusCode.Accepted, queued);
        var runId = Text(started, "run_id");
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&channel=dev&task=ok")).FirstOrDefault(r => Text(r, "id") == runId && r!["finished"] is not null), "the run to end");

        var (status, run) = await h.GetAsync($"/api/v1/runs/{runId}", ServerRole.Viewer);
        var (logStatus, log) = await h.GetAsync($"/api/v1/runs/{runId}/logs", ServerRole.Viewer);

        Assert.Equal((HttpStatusCode.OK, "succeeded", "ok", "sample"), (status, Text(run, "outcome"), Text(run, "task"), Text(run, "package")));
        Assert.Equal(HttpStatusCode.OK, logStatus);
        var lines = log!["lines"]!.AsArray();
        Assert.Contains(lines, l => Text(l, "message") == "hello from ok");
        var ids = lines.Select(l => (long)l!["id"]!).ToList();
        Assert.Equal(ids.Order(), ids);                   // in the order they were written
        Assert.All(lines, l => Assert.False(string.IsNullOrEmpty(Text(l, "level"))));

        // a page at a time: after the first line's id, the rest; with a limit, that many
        var (_, after) = await h.GetAsync($"/api/v1/runs/{runId}/logs?after={ids[0]}", ServerRole.Viewer);
        Assert.Equal(ids.Skip(1), after!["lines"]!.AsArray().Select(l => (long)l!["id"]!));
        var (_, one) = await h.GetAsync($"/api/v1/runs/{runId}/logs?limit=1", ServerRole.Viewer);
        Assert.Single(one!["lines"]!.AsArray());

        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync($"/api/v1/runs/{Guid.NewGuid()}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/logs")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync($"/api/v1/runs/{runId}/logs?after=x")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync($"/api/v1/runs/{runId}/logs?limit=0")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Get, $"/api/v1/runs/{runId}/logs", ServerRole.Viewer, token: false)).Status);
    }

    [Fact]
    public async Task The_lines_of_a_run_that_is_still_going_are_there_while_it_goes_and_a_client_that_follows_gets_each_once()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("waits"));   // prints "ready" and waits for the stop request
        await h.StartAsync();
        var (_, started) = await h.PostAsync("/api/v1/tasks/sample/dev/waits/run");
        var runId = Text(started, "run_id");

        // the first line is readable while the run is still running, and the run says so
        var first = await h.WaitAsync(async () =>
        {
            var (code, log) = await h.GetAsync($"/api/v1/runs/{runId}/logs", ServerRole.Viewer);   // 404 until the runner has started the run
            return code == HttpStatusCode.OK ? log!["lines"]!.AsArray().FirstOrDefault(l => Text(l, "message") == "ready") : null;
        }, "the line the run prints while it goes");
        var (_, going) = await h.GetAsync($"/api/v1/runs/{runId}", ServerRole.Viewer);
        Assert.Equal(("running", null), (Text(going, "outcome"), (string?)going!["finished"]));

        // a client that follows reads after the last id it has, and gets nothing twice
        var last = (long)first["id"]!;
        var (_, nothing) = await h.GetAsync($"/api/v1/runs/{runId}/logs?after={last}", ServerRole.Viewer);
        Assert.DoesNotContain(nothing!["lines"]!.AsArray(), l => (long)l!["id"]! <= last);
    }

    [Fact]
    public async Task The_paired_client_lists_runs_reads_a_run_and_its_log_and_the_steps()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var (_, started) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run");
        var runId = Guid.Parse(Text(started, "run_id"));
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&channel=dev&task=ok")).FirstOrDefault(r => Text(r, "id") == runId.ToString() && r!["finished"] is not null), "the run to end");
        var steps = new StepStore(h.State, new ProgressKey("sample", "dev", "ok"));
        steps.Begin("chain-1", "pass-record", "aaaa", 1);
        steps.CompleteValue("chain-1", "pass-record", "aaaa", 1, "{\"mapping\":\"orders\",\"status\":\"Failed\"}");
        var directory = Directory.CreateTempSubdirectory("dbdr-runs-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Viewer);

            var runs = await client.RunsAsync(server, code, "sample", "dev", "ok");
            var run = await client.RunAsync(server, null, runId);
            var lines = await client.RunLogsAsync(server, null, runId);
            var found = await client.StepsAsync(server, null, "sample", "dev", "ok", "pass-record");

            Assert.Equal(("succeeded", "ok"), (runs.Single(r => r.Id == runId).Outcome, runs.Single(r => r.Id == runId).Task));
            Assert.Equal("succeeded", run!.Outcome);
            Assert.Contains(lines!, l => l.Message == "hello from ok");
            var step = Assert.Single(found);
            Assert.Equal("aaaa", step.Key);
            Assert.Contains("\"mapping\":\"orders\"", step.Result);
            Assert.Null(await client.RunAsync(server, null, Guid.NewGuid()));
            Assert.Null(await client.RunLogsAsync(server, null, Guid.NewGuid()));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* temp */ }
        }
    }

    [Fact]
    public async Task The_steps_of_a_task_are_read_across_its_runs_newest_first_by_key_and_by_time_and_only_the_completed_ones()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var steps = new StepStore(h.State, new ProgressKey("sample", "dev", "ok"));
        foreach (var (chain, key) in new[] { ("chain-1", "k1"), ("chain-1", "k2"), ("chain-2", "k3") })
        {
            steps.Begin(chain, "pass-record", key, 1);
            steps.CompleteValue(chain, "pass-record", key, 1, $"{{\"key\":\"{key}\"}}");
            await Task.Delay(30);
        }

        steps.Begin("chain-2", "pass-record", "unfinished", 1);   // started and never completed: not a result
        steps.Begin("chain-2", "other-step", "k9", 1);
        steps.CompleteValue("chain-2", "other-step", "k9", 1, "{}");
        const string Base = "/api/v1/tasks/sample/dev/ok/steps/";

        var (status, all) = await h.GetAsync(Base + "pass-record", ServerRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["k3", "k2", "k1"], all!["steps"]!.AsArray().Select(s => Text(s, "key")));
        Assert.Equal("k3", Text(all["steps"]![0]!["result"], "key"));   // the result is the JSON the step stored
        Assert.Equal(["k2"], (await h.GetAsync(Base + "pass-record?key=k2", ServerRole.Viewer)).Body!["steps"]!.AsArray().Select(s => Text(s, "key")));
        Assert.Equal(["k3"], (await h.GetAsync(Base + "pass-record?limit=1", ServerRole.Viewer)).Body!["steps"]!.AsArray().Select(s => Text(s, "key")));
        var before = Text(all["steps"]![0], "started");
        Assert.Equal(["k2", "k1"], (await h.GetAsync(Base + $"pass-record?before={Uri.EscapeDataString(before)}", ServerRole.Viewer)).Body!["steps"]!.AsArray().Select(s => Text(s, "key")));
        Assert.Empty((await h.GetAsync(Base + "no-such-step", ServerRole.Viewer)).Body!["steps"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync(Base + "Bad%20Name", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync(Base + "pass-record?limit=0", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/v1/tasks/Sample/dev/ok/steps/pass-record", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Get, Base + "pass-record", ServerRole.Viewer, token: false)).Status);
    }
}
