using System.Net;
using System.Text.Json.Nodes;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// The proofs of plan 28c E12 that were "still proved only in pieces", with every program real (a runner, the engine in its own task process, SQL Server; no Sync server since plan 33 SC23d2): a bulk load of 20 segments whose drain
/// process is killed after the twelfth segment and is finished by the next drain. The load is the drain's <c>requests</c> input (or, while the drain is up, items of its queue), as the Sync API sent it.
/// </summary>
[Collection(SyncOnRunnerExtrasFixture.Name)]
public sealed class SyncOnRunnerLoadsTests(SyncOnRunnerExtrasFixture fixture) : SyncOnRunnerExtrasTest(fixture)
{
    [Fact]
    public async Task A_bulk_load_of_twenty_segments_whose_drain_process_is_killed_after_the_twelfth_is_finished_by_the_next_drain_and_every_row_arrives_once()
    {
        RequireEngines();
        const int segments = 20, perSegment = 25_000;
        await H.InsertAsync("bulk", 1, segments * perSegment);
        await RunNow("bulk");
        await WaitForRows("bulk", segments * perSegment, "loaded by the drain's first pass (a full initial load)");
        await H.ExecuteDwAsync("TRUNCATE TABLE dbo.bulk_tgt");   // the reload starts from an empty target, so the rows that arrive are the segments' rows
        H.Step("bulk: loaded, target emptied");

        // the reload: one request per segment, one batch, run one segment at a time (the segments are given, so nothing is divided against the source)
        var batch = Guid.NewGuid().ToString("N");
        var requests = Enumerable.Range(0, segments).Select(i =>
        {
            var (min, max) = ((i * perSegment + 1).ToString(), ((i + 1) * perSegment + 1).ToString());
            return new JsonObject
            {
                ["kind"] = "BulkLoad", ["mapping"] = "main", ["label"] = $"id [{min}, {max})",
                ["segmentJson"] = new JsonObject { ["mode"] = "range", ["column"] = "id", ["rangeMin"] = min, ["rangeMax"] = max }.ToJsonString(),
                ["readerKind"] = "MsSqlBatchReload", ["cacheKind"] = "MsSqlStagingTable", ["writerKind"] = "MsSqlMerge", ["oneAtATime"] = true, ["batchId"] = batch, ["batchSegments"] = segments,
            };
        }).ToArray();
        await H.SendAsync("bulk", requests);

        // the kill: the drain's task process (a process of the runner's own tree), once twelve segments' rows are in the target
        await H.WaitAsync(async () => (await H.TargetAsync("bulk")).Rows >= 12L * perSegment, "twelve segments of the reload in the target", 240);
        var taskProcesses = H.RunnerProcess.Descendants().Where(p => p.Command.Contains("DbDataSync.TaskRunner.dll", StringComparison.Ordinal)).ToList();
        Assert.True(taskProcesses.Count > 0, "no task process of the engine under the runner: " + string.Join(" | ", H.RunnerProcess.Descendants().Select(p => p.Command)));
        var killedAt = (await H.TargetAsync("bulk")).Rows;
        foreach (var (pid, _) in taskProcesses) System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
        H.Step($"bulk: drain process killed with {killedAt} rows in the target");
        Assert.True(killedAt < (long)segments * perSegment, $"the reload had finished ({killedAt} rows) before the process was killed");

        // the next drain (the schedule starts it) finds the segments queued on the runner and runs the rest
        await WaitForRows("bulk", (long)segments * perSegment, "the reload finished by the next drain");
        Assert.Equal(((long)segments * perSegment, (long)segments * perSegment), await H.TargetAsync("bulk"));

        var runs = await RunnerRuns("bulk");
        Assert.True(runs.Count >= 2, "the killed drain and the one that finished the load are two runs: " + string.Join(", ", runs.Select(r => (string?)r["outcome"])));
        Assert.Contains(runs, r => (string?)r["outcome"] is not ("succeeded" or "running"));   // the killed one did not succeed

        // the batch is one batch of 20 segments, all succeeded, in the drain's checkpoint `batch` on the runner (what the bulk-load card reads), and its rows are counted once: a segment is not counted twice
        IReadOnlyDictionary<string, string>? parts = null;
        await H.WaitAsync(async () =>
        {
            parts = (await H.CheckpointAsync("bulk", "batch")).FirstOrDefault(i => i.Key == batch)?.Parts;
            return parts is not null && parts.GetValueOrDefault("succeeded") == segments.ToString();
        }, "the batch of 20 segments to be completed on the runner", 120);
        Assert.Equal(segments.ToString(), parts!["segments"]);
        Assert.Equal("0", parts.GetValueOrDefault("failed") ?? "0");
        Assert.Equal((segments * perSegment).ToString(), parts["rows_written"]);
    }
}
