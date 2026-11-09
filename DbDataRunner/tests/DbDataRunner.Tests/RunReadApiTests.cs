using System.Net;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 28c E13 over the management API of a real server: one run and its log (the run list, which a product reads for the runs of its own task) and the steps
/// a task's runs recorded (<c>.../steps/{name}</c>, where Sync's drains keep their pass records and the polling gate's checks). A viewer reads all of them, as it reads the run list.
/// </summary>
public sealed class RunReadApiTests
{
    private static string Text(System.Text.Json.Nodes.JsonNode? node, string name) => (string)node![name]!;

    [Fact]
    public async Task The_paired_client_lists_runs_and_reads_the_steps()
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
            var found = await client.StepsAsync(server, null, "sample", "dev", "ok", "pass-record");

            Assert.Equal(("succeeded", "ok"), (runs.Single(r => r.Id == runId).Outcome, runs.Single(r => r.Id == runId).Task));
            var step = Assert.Single(found);
            Assert.Equal("aaaa", step.Key);
            Assert.Contains("\"mapping\":\"orders\"", step.Result);
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
