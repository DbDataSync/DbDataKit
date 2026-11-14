using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Runner.Client;
using DbDataSync.Api.Models;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E12 and the E22 precondition over the Sync API: the batches a drain keeps as the checkpoint <c>batch</c> are the replication's bulk loads (the Monitoring card and the history) while the runner owns it, merged with
/// what this server holds from before the move; a runner that cannot be asked is a 503 and never an empty list; a replication on the old path asks nobody.
/// </summary>
public sealed class RunnerOwnedBulkLoadsTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewName() => "loads-" + Guid.NewGuid().ToString("N")[..10];

    private static ClientCheckpointItem Batch(string id, string mapping, int segments, int succeeded, int failed, long read, long written, DateTimeOffset created, long? estimate = null, string? caveat = null)
    {
        var parts = new Dictionary<string, string>
        {
            ["mapping"] = mapping, ["segments"] = segments.ToString(), ["succeeded"] = succeeded.ToString(), ["failed"] = failed.ToString(),
            ["rows_read"] = read.ToString(), ["rows_written"] = written.ToString(), ["created_at"] = created.ToString("O"),
        };
        if (estimate is not null) parts["estimated_rows"] = estimate.Value.ToString();
        if (caveat is not null) parts["estimate_caveat"] = caveat;
        return new ClientCheckpointItem(id, 1, parts);
    }

    [Fact]
    public async Task The_batches_of_the_runner_are_the_replications_bulk_loads_newest_first_with_their_counts_and_state()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var now = DateTimeOffset.UtcNow;
        factory.Runner.Checkpoint.Clear();
        factory.Runner.Checkpoint.AddRange([
            Batch("b-old", "orders", 20, 20, 0, 500_000, 500_000, now.AddHours(-2), estimate: 480_000),
            Batch("b-new", "orders", 20, 12, 1, 300_000, 275_000, now.AddMinutes(-5), caveat: "an estimate from statistics"),
            Batch("b-done-with-failures", "customers", 4, 3, 1, 10, 9, now.AddHours(-1)),
            new ClientCheckpointItem("not-a-batch", 1, new Dictionary<string, string> { ["mapping"] = "orders" }),
        ]);

        var list = await _client.GetFromJsonAsync<List<BulkLoadBatchProgress>>($"/api/replications/{name}/bulk-loads?limit=5", JsonOptions);

        Assert.Equal(["b-new", "b-done-with-failures", "b-old"], list!.Select(b => b.BatchId));
        var running = list[0];
        Assert.Equal((20, 12, 1, 275_000L), (running.SegmentCount, running.SegmentsSucceeded, running.SegmentsFailed, running.RowsCopied));
        Assert.Equal(BulkLoadState.Running, running.State);
        Assert.Equal("an estimate from statistics", running.EstimateCaveat);
        Assert.Equal(BulkLoadState.CompletedWithFailures, list[1].State);
        Assert.Equal(BulkLoadState.Completed, list[2].State);
        Assert.Equal(480_000, list[2].EstimatedRows);

        var onlyCustomers = await _client.GetFromJsonAsync<BulkLoadHistoryResponse>($"/api/replications/{name}/bulk-loads/history?mappingName=customers", JsonOptions);
        Assert.Equal(["b-done-with-failures"], onlyCustomers!.Batches.Select(b => b.BatchId));
    }

    [Fact]
    public async Task The_history_pages_the_runners_batches_with_the_same_keyset()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var now = DateTimeOffset.UtcNow;
        factory.Runner.Checkpoint.Clear();
        factory.Runner.Checkpoint.AddRange(Enumerable.Range(0, 5).Select(i => Batch($"b{i}", "orders", 2, 2, 0, 10, 10, now.AddMinutes(-i))));

        var first = await _client.GetFromJsonAsync<BulkLoadHistoryResponse>($"/api/replications/{name}/bulk-loads/history?limit=2", JsonOptions);
        var second = await _client.GetFromJsonAsync<BulkLoadHistoryResponse>($"/api/replications/{name}/bulk-loads/history?limit=2&cursor={Uri.EscapeDataString(first!.NextCursor!)}", JsonOptions);
        var third = await _client.GetFromJsonAsync<BulkLoadHistoryResponse>($"/api/replications/{name}/bulk-loads/history?limit=2&cursor={Uri.EscapeDataString(second!.NextCursor!)}", JsonOptions);

        Assert.Equal(["b0", "b1"], first.Batches.Select(b => b.BatchId));
        Assert.Equal(["b2", "b3"], second.Batches.Select(b => b.BatchId));
        Assert.Equal(["b4"], third!.Batches.Select(b => b.BatchId));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public async Task A_runner_that_cannot_be_asked_is_a_503_that_says_so()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        factory.Runner.Down = true;
        try
        {
            var card = await _client.GetAsync($"/api/replications/{name}/bulk-loads");
            var history = await _client.GetAsync($"/api/replications/{name}/bulk-loads/history");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, card.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, history.StatusCode);
            Assert.Contains("could not be reached", await card.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Runner.Down = false;
        }
    }

}
