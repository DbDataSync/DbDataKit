using System.Net;
using System.Text.Json.Nodes;

namespace DbDataConsole.Servers.Tests;

/// <summary>
/// Plan 28c E12e, the proof that a replication really is owned by a runner: SQL Server to PostgreSQL (a continuous replication and a periodic one) and SQL Server to SQL Server (<c>inc</c>, the pair that has positions and a merge writer, because
/// PostgreSQL has no upsert writer for an incremental pass), each moved from Sync's own scheduler and workers to a runner's drain and back, with every program real (see <see cref="SyncOnRunner"/>).
/// One story, because setting it up (two servers, the engine's runtime package, the set published with its libraries) is most of the cost.
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

    private async Task<JsonNode> Move(string replication, string owner, int waitSeconds = 300)
    {
        var moved = await Ok(h.Sync(HttpMethod.Put, $"/api/replications/{replication}/owner", new { owner, waitSeconds }), $"{replication} -> {owner}");
        h.Step($"{replication} -> {owner}");
        return moved;
    }

    private Task<JsonNode> RunNow(string replication) => Ok(h.Sync(HttpMethod.Post, $"/api/replications/{replication}/runs"), $"run now {replication}", HttpStatusCode.Accepted);

    /// <summary>The runs Sync's own history holds for the replication (every kind): what this API's scheduler and workers made.</summary>
    /// <summary>The runs this server's own workers made (they carry the worker's process id); the history of a replication the runner owns also lists the runner's passes and drains (plan 28c E13a), which have none.</summary>
    private async Task<List<JsonNode>> SyncRuns(string replication) =>
        [.. (await Ok(h.Sync(HttpMethod.Get, $"/api/replications/{replication}/runs?limit=200"), $"runs of {replication}"))["runs"]!.AsArray().Select(r => r!).Where(r => r["pid"] is not null)];

    private async Task<bool> WorkerRunning(string replication) => (bool)(await Ok(h.Sync(HttpMethod.Get, $"/api/replications/{replication}/status"), $"status of {replication}"))["running"]!;

    /// <summary>The runner's runs of the replication's task, newest first.</summary>
    private async Task<List<JsonNode>> RunnerRuns(string replication) =>
        [.. (await Ok(h.Runner(HttpMethod.Get, $"/runs?package={SyncOnRunner.Package}&channel={SyncOnRunner.Channel}&task={replication}&limit=100"), $"runner runs of {replication}"))["runs"]!.AsArray().Select(r => r!)];

    private Task WaitForRows(string table, long expected, string what) =>
        h.WaitAsync(async () => await h.TargetAsync(table) is var (rows, _) && rows == expected, $"{table}: {expected} rows in the target ({what})");

    [Fact]
    public async Task A_continuous_and_a_periodic_replication_are_moved_to_a_runner_run_there_and_come_back()
    {
        // ---- 1. Sync's own scheduler and workers run all three (owner old): the control for everything below
        await h.InsertAsync("cont", 1, 1000);
        await h.InsertAsync("per", 1, 1000);
        await h.InsertAsync("inc", 1, 200);
        foreach (var name in new[] { "cont", "per", "inc" }) await RunNow(name);
        await WaitForRows("cont", 1000, "loaded by the supervisor's worker");
        await WaitForRows("per", 1000, "loaded by the supervisor's worker");
        await WaitForRows("inc", 200, "loaded by the supervisor's worker");
        var oldRuns = new Dictionary<string, List<string>>();
        foreach (var name in new[] { "cont", "per", "inc" }) oldRuns[name] = [.. (await SyncRuns(name)).Select(r => Text(r, "runId"))];
        Assert.All(oldRuns.Values, ids => Assert.NotEmpty(ids));

        // ---- 2. observe, then a cutover while the worker is busy: it waits for the worker, and nothing of Sync runs the replication afterwards
        var observed = await Move("inc", "observe");
        Assert.Equal("observe", Text(observed, "to"));
        Assert.Equal("observe", Text(await Ok(h.Sync(HttpMethod.Get, "/api/replications/inc/owner"), "owner of inc"), "owner"));

        await h.InsertAsync("inc", 201, 100_000);
        await RunNow("inc");
        await h.WaitAsync(async () => await WorkerRunning("inc"), "the supervisor's worker of inc to be running", 60);
        var cutover = await Move("inc", "runner");
        Assert.True((bool)cutover["waited"]!, $"the cutover did not wait for the worker: {cutover}");
        Assert.True((int)cutover["positionsHandedOver"]! >= 1, $"no position was handed over: {cutover}");
        Assert.Equal("runner", Text(await Ok(h.Sync(HttpMethod.Get, "/api/replications/inc/owner"), "owner of inc"), "owner"));
        Assert.False(await WorkerRunning("inc"));
        var syncRunsAtCutover = (await SyncRuns("inc")).Count;

        // ---- 3. rows flow on the runner: new rows arrive, the drain of the continuous replication stays up between them, and Sync starts and records nothing
        await h.InsertAsync("inc", 100_201, 50);
        var total = await h.SourceCountAsync("inc");
        await WaitForRows("inc", total, "moved by the runner's drain");
        var drain = (await RunnerRuns("inc")).FirstOrDefault(r => Text(r, "outcome") == "running");
        Assert.True(drain is not null, "the continuous replication has no drain running on the runner: " + string.Join(", ", (await RunnerRuns("inc")).Select(r => Text(r, "outcome"))));
        await h.InsertAsync("inc", 100_251, 20);
        await WaitForRows("inc", total + 20, "picked up by the same drain");
        Assert.Equal(Text(drain, "id"), Text((await RunnerRuns("inc")).First(r => Text(r, "outcome") == "running"), "id"));
        Assert.False(await WorkerRunning("inc"));
        Assert.Equal(syncRunsAtCutover, (await SyncRuns("inc")).Count);
        Assert.Equal(total + 20, (await h.TargetAsync("inc")).Distinct);
        h.Step("inc runs on the runner");

        // ---- 4. the periodic replication to PostgreSQL: cut over (no worker to wait for), a run now is a run of its drain on the runner
        var periodic = await Move("per", "runner");
        Assert.False((bool)periodic["waited"]!);
        var syncPerRuns = (await SyncRuns("per")).Count;
        await h.InsertAsync("per", 1001, 500);
        await RunNow("per");
        await WaitForRows("per", 1500, "moved by a drain of the runner");
        await h.WaitAsync(async () => (await RunnerRuns("per")).Any(r => Text(r, "outcome") == "succeeded"), "a finished drain of per on the runner");
        Assert.Equal(syncPerRuns, (await SyncRuns("per")).Count);
        Assert.False(await WorkerRunning("per"));

        // ---- 5. the continuous replication to PostgreSQL
        await Move("cont", "runner");
        var syncContRuns = (await SyncRuns("cont")).Count;
        await h.InsertAsync("cont", 1001, 300);
        await WaitForRows("cont", 1300, "moved by the runner's drain");
        Assert.Equal(syncContRuns, (await SyncRuns("cont")).Count);
        Assert.Contains(await RunnerRuns("cont"), r => Text(r, "outcome") == "running");

        // ---- 6. rollback: the supervisor resumes, the positions the runner made are copied back (the next pass reads only what is new), Sync's history is as it was
        var rollback = await Move("inc", "old");
        Assert.True((int)rollback["positionsHandedOver"]! >= 1, $"no position was copied back: {rollback}");
        Assert.Equal("old", Text(await Ok(h.Sync(HttpMethod.Get, "/api/replications/inc/owner"), "owner of inc"), "owner"));
        var before = (await SyncRuns("inc")).Select(r => Text(r, "runId")).ToHashSet();
        await h.InsertAsync("inc", 100_271, 10);
        total = await h.SourceCountAsync("inc");
        // A continuous run records its counts when it ends (after its idle timeout), not while it runs: the target has the rows before the run says it read them. Wait for the runs added since to end.
        List<JsonNode> added = [];
        await h.WaitAsync(async () =>
        {
            added = [.. (await SyncRuns("inc")).Where(r => !before.Contains(Text(r, "runId")))];
            return added.Count > 0 && added.All(r => Text(r, "status") != "Running");
        }, "the runs of the supervisor's worker since the rollback have ended");
        var after = await SyncRuns("inc");
        Assert.Equal(10, added.Sum(r => (long?)r["rowsRead"] ?? 0));   // not the 100,000 rows the runner moved: its positions came back
        Assert.True(10 == added.Sum(r => (long?)r["rowsRead"] ?? 0), "DIAG added=" + string.Join(" ; ", added.Select(r => r.ToJsonString())) + " beforeCount=" + before.Count);
        foreach (var id in oldRuns["inc"]) Assert.Contains(after, r => Text(r, "runId") == id);   // the history is intact
        var (rows, distinct) = await h.TargetAsync("inc");
        Assert.Equal((total, total), (rows, distinct));
        Assert.DoesNotContain((await RunnerRuns("inc")), r => Text(r, "outcome") == "running");

        await Move("per", "old");
        await h.InsertAsync("per", 1501, 100);
        await RunNow("per");
        await WaitForRows("per", 1600, "moved by the supervisor's worker again");
        Assert.True((await SyncRuns("per")).Count > syncPerRuns);

        await Move("cont", "old");
        await h.InsertAsync("cont", 1301, 100);
        await WaitForRows("cont", 1400, "moved by the supervisor's worker again");
        Assert.True((await SyncRuns("cont")).Count > syncContRuns);
        foreach (var name in new[] { "cont", "per" }) foreach (var id in oldRuns[name]) Assert.Contains(await SyncRuns(name), r => Text(r, "runId") == id);
    }
}
