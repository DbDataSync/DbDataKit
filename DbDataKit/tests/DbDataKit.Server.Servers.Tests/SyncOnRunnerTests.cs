using System.Net;
using System.Text.Json.Nodes;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 28c E22 (owner decision S2 of 2026-11-14): the Runner is Sync's only scheduler. SQL Server to PostgreSQL (a continuous replication and a periodic one) and SQL Server to SQL Server (<c>inc</c>, the pair
/// that has positions and a merge writer), published once and run by the runner's drains from the start, with every program real (see <see cref="SyncOnRunner"/>): the Sync server starts no process and
/// records no run of its own, and its status, Run now, pause and history all speak of the runner. One story, because setting it up (two servers, the engine's runtime package, the set published with its
/// libraries) is most of the cost.
/// </summary>
public sealed class SyncOnRunnerTests : IAsyncLifetime
{
    private SyncOnRunner h = null!;

    public async ValueTask InitializeAsync()
    {
        if (SyncOnRunner.EnginesMissing is { } missing) Assert.Skip(missing);
        h = await SyncOnRunner.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (h is not null) await h.DisposeAsync();
    }

    private static string Text(JsonNode? node, string name) => (string?)node?[name] ?? "";

    private async Task<JsonNode> Ok(Task<(HttpStatusCode Status, JsonNode? Body)> call, string what, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, body) = await call;
        Assert.True(status == expected, $"{what}: {status} {body}\n{string.Join('\n', h.Steps)}\n{(status == expected ? "" : await h.Diagnose())}");
        return body ?? new JsonObject();
    }

    private Task<JsonNode> RunNow(string replication) => Ok(h.Sync(HttpMethod.Post, $"/api/replications/{replication}/runs"), $"run now {replication}", HttpStatusCode.Accepted);

    /// <summary>The runs a worker of the Sync server would have made (they carry the worker's process id); the history lists the runner's passes and drains too (plan 28c E13a), which have none.</summary>
    private async Task<List<JsonNode>> SyncRuns(string replication) =>
        [.. (await Ok(h.Sync(HttpMethod.Get, $"/api/replications/{replication}/runs?limit=200"), $"runs of {replication}"))["runs"]!.AsArray().Select(r => r!).Where(r => r["pid"] is not null)];

    private async Task<bool> WorkerRunning(string replication) => (bool)(await Ok(h.Sync(HttpMethod.Get, $"/api/replications/{replication}/status"), $"status of {replication}"))["running"]!;

    /// <summary>The runner's runs of the replication's task, newest first.</summary>
    private async Task<List<JsonNode>> RunnerRuns(string replication) =>
        [.. (await Ok(h.Runner(HttpMethod.Get, $"/runs?package={SyncOnRunner.Package}&channel={SyncOnRunner.Channel}&task={replication}&limit=100"), $"runner runs of {replication}"))["runs"]!.AsArray().Select(r => r!)];

    private Task WaitForRows(string table, long expected, string what) =>
        h.WaitAsync(async () => await h.TargetAsync(table) is var (rows, _) && rows == expected, $"{table}: {expected} rows in the target ({what})");

    [Fact]
    public async Task Published_replications_are_run_by_the_runner_alone_and_the_Sync_server_runs_nothing_itself()
    {
        // ---- 1. Run now of each is a run of its drain on the runner; nothing of the Sync server's own runs
        await h.InsertAsync("cont", 1, 1000);
        await h.InsertAsync("per", 1, 1000);
        await h.InsertAsync("inc", 1, 200);
        foreach (var name in new[] { "cont", "per", "inc" }) await RunNow(name);
        await WaitForRows("cont", 1000, "loaded by the runner's drain");
        await WaitForRows("per", 1000, "loaded by a drain of the runner");
        await WaitForRows("inc", 200, "loaded by the runner's drain");
        foreach (var name in new[] { "cont", "per", "inc" })
        {
            Assert.Empty(await SyncRuns(name));   // no worker of the Sync server ever ran
            Assert.NotEmpty(await RunnerRuns(name));
        }

        // ---- 2. the continuous replication's drain stays up between rows, and the status the Sync API answers is the drain's
        var total = await h.SourceCountAsync("inc");
        await h.InsertAsync("inc", 201, 50);
        await WaitForRows("inc", total + 50, "picked up by the drain");
        var drain = (await RunnerRuns("inc")).FirstOrDefault(r => Text(r, "outcome") == "running");
        Assert.True(drain is not null, "the continuous replication has no drain running on the runner: " + string.Join(", ", (await RunnerRuns("inc")).Select(r => Text(r, "outcome"))));
        await h.WaitAsync(async () => await WorkerRunning("inc"), "the Sync API to say the drain of inc runs", 60);
        await h.InsertAsync("inc", 251, 20);
        await WaitForRows("inc", total + 70, "picked up by the same drain");
        Assert.Equal(Text(drain, "id"), Text((await RunnerRuns("inc")).First(r => Text(r, "outcome") == "running"), "id"));
        Assert.Equal(total + 70, (await h.TargetAsync("inc")).Distinct);
        h.Step("inc runs on the runner");

        // ---- 3. the periodic replication: a run now is a drain that ends, and the history the Sync API serves lists the runner's passes
        await h.InsertAsync("per", 1001, 500);
        await RunNow("per");
        await WaitForRows("per", 1500, "moved by a drain of the runner");
        await h.WaitAsync(async () => (await RunnerRuns("per")).Any(r => Text(r, "outcome") == "succeeded"), "a finished drain of per on the runner");
        var history = (await Ok(h.Sync(HttpMethod.Get, "/api/replications/per/runs?limit=50"), "runs of per"))["runs"]!.AsArray();
        Assert.NotEmpty(history);
        Assert.Empty(await SyncRuns("per"));

        // ---- 4. a pause on the Sync API holds the drain on the runner, and a resume lets it go
        await Ok(h.Sync(HttpMethod.Put, "/api/replications/cont/paused", new { paused = true, note = "held for the proof" }), "pause cont");
        var row = await Ok(h.Sync(HttpMethod.Get, "/api/replications/cont/runner"), "runner row of cont");
        Assert.True((bool)row["runner"]!["paused"]!, row.ToJsonString());
        await Ok(h.Sync(HttpMethod.Put, "/api/replications/cont/paused", new { paused = false }), "resume cont");
        await h.InsertAsync("cont", 1001, 300);
        await RunNow("cont");
        await WaitForRows("cont", 1300, "moved by the runner's drain after the resume");
        Assert.Empty(await SyncRuns("cont"));
    }
}
