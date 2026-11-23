using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 28c E22 (owner decision S2 of 2026-11-14): the Runner is Sync's only scheduler, and since plan 33 SC23d2 there is no Sync server in the story at all (see <see cref="SyncOnRunner"/>). SQL Server to PostgreSQL (a continuous
/// replication and a periodic one) and SQL Server to SQL Server (<c>inc</c>, the pair that has positions and a merge writer), written as files, written as a package with <c>dbdatasync package publish --out</c>, sent once and run by the runner's drains:
/// run now, a drain that stays up, a pause and a resume, a resync and the set's <c>provision</c> task, each a run of the runner with its inputs. One story, because setting it up (a runner, the engine's runtime package, the set
/// published with its libraries) is most of the cost.
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

    private async Task<JsonNode> Ok(Task<(HttpStatusCode Status, JsonNode? Body)> call, string what, params HttpStatusCode[] expected)
    {
        expected = expected.Length == 0 ? [HttpStatusCode.OK] : expected;
        var (status, body) = await call;
        Assert.True(expected.Contains(status), $"{what}: {status} {body}\n{string.Join('\n', h.Steps)}\n{(expected.Contains(status) ? "" : await h.Diagnose())}");
        return body ?? new JsonObject();
    }

    /// <summary>Run now of a replication, as the Runner's screen asks it: a drain that runs everything that is due (<c>run_all</c>); a drain queued or running already is the one that runs it (409).</summary>
    private Task<JsonNode> RunNow(string replication) =>
        Ok(h.RunNow(replication, new Dictionary<string, string> { ["run_all"] = "true" }), $"run now {replication}", HttpStatusCode.Accepted, HttpStatusCode.Conflict);

    /// <summary>The runner's runs of a task of the set, newest first.</summary>
    private async Task<List<JsonNode>> RunnerRuns(string task) =>
        [.. (await Ok(h.Runner(HttpMethod.Get, $"/runs?package={SyncOnRunner.Package}&channel={SyncOnRunner.Channel}&task={task}&limit=100"), $"runner runs of {task}"))["runs"]!.AsArray().Select(r => r!)];

    private Task WaitForRows(string table, long expected, string what) =>
        h.WaitAsync(async () => await h.TargetAsync(table) is var (rows, _) && rows == expected, $"{table}: {expected} rows in the target ({what})", task: table);

    [Fact]
    public async Task Replications_written_as_files_and_published_are_run_paused_resynced_and_provisioned_on_the_runner_with_no_Sync_server()
    {
        // ---- 1. Run now of each is a run of its drain on the runner
        await h.InsertAsync("cont", 1, 1000);
        await h.InsertAsync("per", 1, 1000);
        await h.InsertAsync("inc", 1, 200);
        foreach (var name in new[] { "cont", "per", "inc" }) await RunNow(name);
        await WaitForRows("cont", 1000, "loaded by the runner's drain");
        await WaitForRows("per", 1000, "loaded by a drain of the runner");
        await WaitForRows("inc", 200, "loaded by the runner's drain");
        foreach (var name in new[] { "cont", "per", "inc" }) Assert.NotEmpty(await RunnerRuns(name));

        // ---- 2. the continuous replication's drain stays up between rows, and the runner's status says it runs
        var total = await h.SourceCountAsync("inc");
        await h.InsertAsync("inc", 201, 50);
        await WaitForRows("inc", total + 50, "picked up by the drain");
        var drain = (await RunnerRuns("inc")).FirstOrDefault(r => Text(r, "outcome") == "running");
        Assert.True(drain is not null, "the continuous replication has no drain running on the runner: " + string.Join(", ", (await RunnerRuns("inc")).Select(r => Text(r, "outcome"))));
        await h.WaitAsync(async () => (int?)(await h.TaskStatus("inc"))?["running"] > 0, "the runner's status to say the drain of inc runs", 60);
        await h.InsertAsync("inc", 251, 20);
        await WaitForRows("inc", total + 70, "picked up by the same drain");
        Assert.Equal(Text(drain, "id"), Text((await RunnerRuns("inc")).First(r => Text(r, "outcome") == "running"), "id"));
        Assert.Equal(total + 70, (await h.TargetAsync("inc")).Distinct);
        h.Step("inc runs on the runner");

        // ---- 3. the periodic replication: a run now is a drain that ends, and the runner's history lists it
        await h.InsertAsync("per", 1001, 500);
        await RunNow("per");
        await WaitForRows("per", 1500, "moved by a drain of the runner");
        await h.WaitAsync(async () => (await RunnerRuns("per")).Any(r => Text(r, "outcome") == "succeeded"), "a finished drain of per on the runner");

        // ---- 4. a pause holds the drain on the runner, and a resume lets it go
        await Ok(h.Pause("cont"), "pause cont");
        Assert.True((bool?)(await h.TaskStatus("cont"))?["paused"] == true, (await h.TaskStatus("cont"))?.ToJsonString());
        await Ok(h.Resume("cont"), "resume cont");
        Assert.True((bool?)(await h.TaskStatus("cont"))?["paused"] != true, (await h.TaskStatus("cont"))?.ToJsonString());
        await h.InsertAsync("cont", 1001, 300);
        await RunNow("cont");
        await WaitForRows("cont", 1300, "moved by the runner's drain after the resume");

        // ---- 5. a resync: the mapping's next pass is an initial load, so a target emptied behind the drain's back is filled again (the source key is the one the drain keeps its position under)
        var position = (await h.CheckpointAsync("inc", "watermark")).Select(i => i.Key).FirstOrDefault(k => k.StartsWith("main|", StringComparison.Ordinal));
        Assert.True(position is not null, "inc has no position of its mapping main on the runner: " + string.Join(", ", (await h.CheckpointAsync("inc", "watermark")).Select(i => i.Key)));
        var sourceCount = await h.SourceCountAsync("inc");
        await h.ExecuteDwAsync("TRUNCATE TABLE dbo.inc_tgt");
        await h.SendAsync("inc", new JsonObject { ["kind"] = "Resync", ["mapping"] = "main", ["sourceKey"] = position!["main|".Length..] });
        await WaitForRows("inc", sourceCount, "an initial load after the resync");
        Assert.Equal(sourceCount, (await h.TargetAsync("inc")).Distinct);
        h.Step("inc resynced");

        // ---- 6. the set's provision task creates a target table that does not exist (a drain of prov that ran before it failed naming the missing table, as it should; a load that failed stays failed until a person forces a
        // fresh attempt, which is not this story's)
        Assert.False(await DwTableExistsAsync("prov_tgt"));
        var started = await Ok(h.RunNow("provision", new Dictionary<string, string> { ["replication"] = SyncOnRunner.Provisioned, ["mapping"] = "main", ["action"] = "createTargetTable", ["steps"] = "", ["repo_dir"] = "repo" }),
            "run provision", HttpStatusCode.Accepted);
        var runId = Text(started, "run_id");
        await h.WaitAsync(async () => (await RunnerRuns("provision")).FirstOrDefault(r => Text(r, "id") == runId) is { } run && Text(run, "outcome") is not ("queued" or "running"), "the provision run to end");
        Assert.Equal("succeeded", Text((await RunnerRuns("provision")).First(r => Text(r, "id") == runId), "outcome"));
        Assert.True(await DwTableExistsAsync("prov_tgt"), "the provision run succeeded and dbo.prov_tgt is not there");
        var applied = Assert.Single(await h.StepsAsync("provision", "provision-applied"));
        Assert.Contains("\"succeeded\":true", applied.Result, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> DwTableExistsAsync(string table)
    {
        await using var dw = new SqlConnection(h.DwConnectionString);
        await dw.OpenAsync();
        await using var command = dw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @name AND schema_id = SCHEMA_ID('dbo')";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }
}
