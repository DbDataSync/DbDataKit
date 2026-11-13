using System.Text.Json.Nodes;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 28c E06d (the API side of the verification payload), with every program real: a Sync server, a runner, the engine in its own task process, SQL Server. A replication the runner owns is checked through the Sync API; the result is a payload
/// of the runner's run, listed and paged by the Sync API from the runner, and no file of it is left beside Sync's state or in the runner's directory.
/// </summary>
[Collection(SyncOnRunnerExtrasFixture.Name)]
public sealed class SyncOnRunnerVerificationTests(SyncOnRunnerExtrasFixture fixture) : SyncOnRunnerExtrasTest(fixture)
{
    [Fact]
    public async Task A_verification_of_a_replication_the_runner_owns_is_listed_and_paged_by_the_Sync_API_from_the_runs_payload_and_no_file_is_left_beside_either_state()
    {
        RequireEngines();
        await H.InsertAsync("ver", 1, 200);
        await Move("ver", "runner");
        await RunNow("ver");
        await WaitForRows("ver", 200, "loaded by the first drain on the runner");

        // the first drain has ended (a request put in the queue of a drain that is about to end would wait for the next one, which is not before 3 o'clock)
        await H.WaitAsync(async () => !(await RunnerRuns("ver")).Any(r => (string?)r["outcome"] == "running"), "the first drain of ver to end");
        await Ok(H.Sync(HttpMethod.Post, "/api/replications/ver/mappings/main/verify"), "verify ver", System.Net.HttpStatusCode.Accepted);

        JsonArray results = [];
        await H.WaitAsync(async () =>
        {
            results = (await Ok(H.Sync(HttpMethod.Get, "/api/replications/ver/verification-results"), "verification results of ver")).AsArray();
            return results.Count > 0;
        }, "the verification result of ver to be listed from the runner's steps", 120);

        var record = results[0]!;
        Assert.Equal("main", (string?)record["mappingName"]);
        Assert.Equal("counts", (string?)record["checkName"]);
        Assert.Equal(200, (int)record["groupsCompared"]!);
        Assert.Equal(0, (int)record["differingGroups"]!);
        var page = await Ok(H.Sync(HttpMethod.Get, $"/api/replications/ver/verification-results/{(long)record["id"]!}?limit=500"), "a page of the result");
        Assert.Equal(200, (int)page["totalRows"]!);
        Assert.Equal(0, (int)page["differingRows"]!);
        Assert.Equal(200, page["rows"]!.AsArray().Count);

        // nothing of the check is a file beside Sync's state or in the runner's directory: the result is a payload of the run and its scratch is gone
        Assert.Empty(Directory.GetFiles(H.SyncDirectory, "*.parquet", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(H.RunnerDirectory, "*.parquet", SearchOption.AllDirectories));

        // a difference is found: five rows the target lacks
        await H.ExecuteDwAsync("DELETE FROM dbo.ver_tgt WHERE id <= 5");
        await H.WaitAsync(async () => !(await RunnerRuns("ver")).Any(r => (string?)r["outcome"] == "running"), "the drain of the first check to end");
        await Ok(H.Sync(HttpMethod.Post, "/api/replications/ver/mappings/main/verify"), "verify ver again", System.Net.HttpStatusCode.Accepted);
        JsonNode? newest = null;
        await H.WaitAsync(async () =>
        {
            var list = (await Ok(H.Sync(HttpMethod.Get, "/api/replications/ver/verification-results"), "verification results of ver")).AsArray();
            newest = list.Count > 1 ? list[0] : null;
            return newest is not null;
        }, "the second verification result", 120);
        Assert.Equal(5, (int)newest!["differingGroups"]!);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await H.Sync(HttpMethod.Delete, $"/api/replications/ver/verification-results/{(long)newest["id"]!}")).Status);   // the runner's retention owns it
    }
}
