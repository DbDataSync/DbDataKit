using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Runner;
using DbDataSync.State;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Owner decision 2026-11-14 (plan 28c E22): the row of a drain shows the rows its passes read, summed so far, as each pass finishes; it says it is still running (and the screen when it began, from its start); the count
/// is final once the drain ends. The run read by id says the same.
/// </summary>
public sealed class DrainRowsReadTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Page(List<TaskRunRecord> Runs, string? NextCursor, string? RunnerUnavailable);

    private static PassRecord Pass(string replication, Guid drain, DateTimeOffset started, long read) =>
        new(Guid.NewGuid(), drain, replication, nameof(RunKind.Primary), "orders", null, nameof(RunStatus.Succeeded), started, started.AddSeconds(1), read, read, null, null, null, null);

    private async Task<TaskRunRecord> RowAsync(string replication, Guid drain) =>
        Assert.Single((await _client.GetFromJsonAsync<Page>($"/api/replications/{replication}/runs", JsonOptions))!.Runs, r => r.RunId == drain);

    [Fact]
    public async Task A_running_drains_row_reads_the_sum_of_its_passes_so_far_and_it_grows_as_a_pass_finishes()
    {
        var name = "rows-" + Guid.NewGuid().ToString("N")[..10];
        factory.RunnerOwns(name);
        var task = RunnerAccess.TaskOf(name);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var drain = factory.Runner.AddRun(task, "running", started);
        factory.Runner.AddPass(task, Pass(name, drain.Id, started.AddSeconds(10), 40));
        factory.Runner.AddPass(task, Pass(name, Guid.NewGuid(), started.AddSeconds(11), 999));   // another drain's pass

        var first = await RowAsync(name, drain.Id);
        Assert.Equal((RunStatus.Running, 40L), (first.Status, first.RowsRead));
        Assert.Equal(started, first.StartedAtUtc!.Value, TimeSpan.FromMilliseconds(1));

        factory.Runner.AddPass(task, Pass(name, drain.Id, started.AddSeconds(20), 2));
        Assert.Equal(42L, (await RowAsync(name, drain.Id)).RowsRead);

        // the run read by id (what the live panel and the details ask) says the same
        var one = await _client.GetFromJsonAsync<TaskRunRecord>($"/api/runs/{drain.Id}", JsonOptions);
        Assert.Equal((RunStatus.Running, 42L), (one!.Status, one.RowsRead));
    }
}
