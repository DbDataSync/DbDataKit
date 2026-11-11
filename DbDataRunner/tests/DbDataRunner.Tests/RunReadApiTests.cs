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
    public async Task A_run_that_is_finished_has_all_its_lines_and_a_page_is_never_longer_than_its_limit()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var (_, started) = await h.PostAsync("/api/v1/tasks/sample/dev/ok/run");
        var runId = Text(started, "run_id");
        await h.WaitAsync(async () => (await h.RunsAsync("?package=sample&channel=dev&task=ok")).FirstOrDefault(r => Text(r, "id") == runId && r!["finished"] is not null), "the run to end");

        // the moment the run is finished its log is complete: the runner's note on the process's exit, which it writes after the process has gone, is there, and nothing is added later
        var (_, first) = await h.GetAsync($"/api/v1/runs/{runId}/logs", ServerRole.Viewer);
        var ids = first!["lines"]!.AsArray().Select(l => (long)l!["id"]!).ToList();
        Assert.Contains(first["lines"]!.AsArray(), l => Text(l, "message").StartsWith("The process exited with code", StringComparison.Ordinal));
        await Task.Delay(1500);
        var (_, later) = await h.GetAsync($"/api/v1/runs/{runId}/logs", ServerRole.Viewer);
        Assert.Equal(ids, later!["lines"]!.AsArray().Select(l => (long)l!["id"]!).ToList());

        // a page is at most `limit` lines, the next page starts after the last id of this one, and the pages together are the whole log
        var seen = new List<long>();
        long? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var (_, got) = await h.GetAsync($"/api/v1/runs/{runId}/logs?limit=2" + (cursor is { } c ? $"&after={c}" : ""), ServerRole.Viewer);
            var pageIds = got!["lines"]!.AsArray().Select(l => (long)l!["id"]!).ToList();
            Assert.InRange(pageIds.Count, 0, 2);
            if (pageIds.Count == 0) break;
            seen.AddRange(pageIds);
            cursor = pageIds[^1];
        }

        Assert.Equal(ids, seen);
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

    [Fact]
    public async Task The_file_a_step_kept_as_its_result_is_served_to_a_viewer_only_for_that_task_and_that_step_and_a_missing_one_is_not_found()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        h.Install(SamplePackage.Task("ok"));
        await h.StartAsync();
        var steps = new StepStore(h.State, new ProgressKey("sample", "dev", "ok"));
        var begin = (StepBegin.Run)steps.Begin("chain-1", "verification-file", "orders/check/1", 1);
        var content = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        Guid payload;
        await using (var writer = steps.OpenStream(begin, "verification-file", "application/vnd.apache.parquet"))
        {
            writer.Write(content);
            writer.Complete();
            payload = writer.Id;
        }

        steps.CompleteStream("chain-1", "verification-file", "orders/check/1", 1, payload);
        var unfinished = (StepBegin.Run)steps.Begin("chain-1", "verification-file", "orders/check/2", 1);
        Guid half;
        await using (var writer = steps.OpenStream(unfinished, "verification-file"))
        {
            writer.Write(content);
            half = writer.Id;      // never completed
        }

        var directory = Directory.CreateTempSubdirectory("dbdr-payload-client-").FullName;
        try
        {
            var client = new RunnerPublishClient(new RunnerClientStore(directory), _ => null);
            var server = h.Url.ToString();
            var code = h.NewCode(ServerRole.Viewer);

            using var copy = new MemoryStream();
            var found = await client.StepPayloadAsync(server, code, "sample", "dev", "ok", "verification-file", payload, copy);
            using var wrongStep = new MemoryStream();
            var otherStep = await client.StepPayloadAsync(server, null, "sample", "dev", "ok", "pass-record", payload, wrongStep);
            var otherTask = await client.StepPayloadAsync(server, null, "sample", "dev", "other", "verification-file", payload, new MemoryStream());
            var incomplete = await client.StepPayloadAsync(server, null, "sample", "dev", "ok", "verification-file", half, new MemoryStream());
            var unknown = await client.StepPayloadAsync(server, null, "sample", "dev", "ok", "verification-file", Guid.NewGuid(), new MemoryStream());

            Assert.True(found);
            Assert.Equal(content, copy.ToArray());
            Assert.False(otherStep);
            Assert.Equal(0, wrongStep.Length);
            Assert.False(otherTask);
            Assert.False(incomplete);
            Assert.False(unknown);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* temp */ }
        }

        var path = $"/api/v1/tasks/sample/dev/ok/steps/verification-file/payloads/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync(path, ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync($"/api/v1/tasks/sample/dev/ok/steps/Bad%20Name/payloads/{payload}", ServerRole.Viewer)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.CallAsync(HttpMethod.Get, path, ServerRole.Viewer, token: false)).Status);
    }
}
