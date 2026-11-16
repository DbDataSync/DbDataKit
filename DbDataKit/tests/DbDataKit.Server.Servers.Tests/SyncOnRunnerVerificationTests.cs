using System.Text.Json.Nodes;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 28c E06d (the verification payload), with every program real: a runner, the engine in its own task process, SQL Server; no Sync server since plan 33 SC23d2. A verification of a replication is a request of its drain (the
/// <c>requests</c> input, or an item of its queue while it is up); the result is two steps of the runner's run: the record (<c>verification-result</c>) and the file it names, a parquet payload under the run's retention
/// (<c>verification-file</c>). No file of it is left in the repository or in the runner's directory.
/// </summary>
[Collection(SyncOnRunnerExtrasFixture.Name)]
public sealed class SyncOnRunnerVerificationTests(SyncOnRunnerExtrasFixture fixture) : SyncOnRunnerExtrasTest(fixture)
{
    private const string RecordStep = "verification-result";
    private const string FileStep = "verification-file";

    /// <summary>The records the drains of <c>ver</c> made, newest first.</summary>
    private async Task<List<JsonNode>> Records() =>
        [.. (await H.StepsAsync("ver", RecordStep)).Where(s => s.Result is not null).OrderByDescending(s => s.Started).Select(s => JsonNode.Parse(s.Result!)!)];

    [Fact]
    public async Task A_verification_of_a_replication_is_a_request_of_its_drain_whose_record_and_file_are_steps_of_the_runners_run_and_no_file_is_left_behind()
    {
        RequireEngines();
        await H.InsertAsync("ver", 1, 200);
        await RunNow("ver");
        await WaitForRows("ver", 200, "loaded by the first drain on the runner");

        // the first drain has ended (a request put in the queue of a drain that is about to end would wait for the next one, which is not before 3 o'clock)
        await H.WaitAsync(async () => !(await RunnerRuns("ver")).Any(r => (string?)r["outcome"] == "running"), "the first drain of ver to end");
        await H.SendAsync("ver", new JsonObject { ["kind"] = "Verification", ["mapping"] = "main" });

        List<JsonNode> records = [];
        await H.WaitAsync(async () => (records = await Records()).Count > 0, "the verification record of ver among the runner's steps", 120);
        var record = records[0];
        Assert.Equal("main", (string?)record["mappingName"]);
        Assert.Equal("counts", (string?)record["checkName"]);
        Assert.Equal(200, (int)record["groupsCompared"]!);
        Assert.Equal(0, (int)record["differingGroups"]!);

        // the file the record names is the run's payload, served by the runner: a parquet file
        var path = (string)record["resultPath"]!;
        Assert.StartsWith("payload:", path);
        var file = await H.StepPayloadAsync("ver", FileStep, Guid.ParseExact(path["payload:".Length..], "N"));
        Assert.True(file is { Length: > 8 }, "the runner does not serve the result file " + path);
        Assert.Equal("PAR1"u8.ToArray(), file![..4]);

        // nothing of the check is a file in the repository or in the runner's directory: the result is a payload of the run and its scratch is gone
        Assert.Empty(Directory.GetFiles(H.SyncDirectory, "*.parquet", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(H.RunnerDirectory, "*.parquet", SearchOption.AllDirectories));

        // a difference is found: five rows the target lacks
        await H.ExecuteDwAsync("DELETE FROM dbo.ver_tgt WHERE id <= 5");
        await H.WaitAsync(async () => !(await RunnerRuns("ver")).Any(r => (string?)r["outcome"] == "running"), "the drain of the first check to end");
        await H.SendAsync("ver", new JsonObject { ["kind"] = "Verification", ["mapping"] = "main" });
        await H.WaitAsync(async () => (records = await Records()).Count > 1, "the second verification record", 120);
        Assert.Equal(5, (int)records[0]["differingGroups"]!);
    }
}
