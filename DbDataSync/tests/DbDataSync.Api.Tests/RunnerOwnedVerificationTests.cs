using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DbDataSync.Api.Runner;
using DbDataSync.State;
using DbDataSync.Drivers.Generic;
using DbDataSync.Verification;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E06e over the Sync API: the verification results of a replication the runner owns are listed from the runner's steps and a page is read from the file the runner serves (no file in this server's data
/// directory); the same replication on the old path is unchanged; a result the runner's retention has removed answers "expired" naming the run; a runner that cannot be asked is a 503 saying so.
/// </summary>
public sealed class RunnerOwnedVerificationTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private static string NewName() => "verified-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<byte[]> ParquetAsync(int rows)
    {
        var path = Path.Combine(Path.GetTempPath(), "dbdatasync-test-" + Guid.NewGuid().ToString("N") + ".parquet");
        try
        {
            var written = Enumerable.Range(0, rows).Select(i => new VerificationRow(
                [$"g{i:D3}"], new Dictionary<string, double> { ["__rows"] = 10 }, new Dictionary<string, double> { ["__rows"] = i % 2 == 0 ? 9 : 10 },
                new Dictionary<string, double> { ["__rows"] = i % 2 == 0 ? -1 : 0 }, i % 2 == 0 ? VerificationRowStatus.Differs : VerificationRowStatus.Match)).ToList();
            await VerificationResultFile.WriteAsync(path, new VerificationResult("rows-by-region", ["region"], ["__rows"], 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, written), CancellationToken.None);
            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>What a drain records for one result: the file as a payload and the record naming it, as steps of the replication's task.</summary>
    private static (VerificationResultRecord Record, Guid Payload) Record(FakeRunnerRuns runner, string replication, string mapping, DateTimeOffset at, byte[]? file)
    {
        var payload = Guid.NewGuid();
        var record = new VerificationResultRecord(0, Guid.NewGuid(), replication, mapping, "rows-by-region", at, at.AddSeconds(-2), at.AddSeconds(-1), 10, 5, VerificationResultPayload.PathFor(payload));
        runner.AddStep(RunnerAccess.TaskOf(replication), VerificationResultPayload.RecordStepName, $"{mapping}/rows-by-region/{at.UtcTicks}",
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)), at);
        if (file is not null) runner.AddPayload(RunnerAccess.TaskOf(replication), VerificationResultPayload.FileStepName, payload, file);
        return (record, payload);
    }

    [Fact]
    public async Task A_result_on_the_runner_is_listed_and_paged_from_the_runners_file_and_nothing_is_written_beside_the_state()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        var (_, payload) = Record(factory.Runner, name, "orders", at, await ParquetAsync(10));
        Record(factory.Runner, name, "customers", at.AddMinutes(1), await ParquetAsync(2));

        var all = await _client.GetFromJsonAsync<List<VerificationResultRecord>>($"/api/replications/{name}/verification-results", JsonOptions);
        var orders = await _client.GetFromJsonAsync<List<VerificationResultRecord>>($"/api/replications/{name}/verification-results?mappingName=orders", JsonOptions);

        Assert.Equal(["customers", "orders"], all!.Select(r => r.MappingName));      // newest first
        var one = Assert.Single(orders!);
        Assert.Equal(VerificationResultPayload.RouteIdOf(payload), one.Id);
        Assert.Equal(name, one.TaskName);

        var page = await _client.GetFromJsonAsync<VerificationResultPage>($"/api/replications/{name}/verification-results/{one.Id}?limit=4", JsonOptions);
        Assert.Equal(4, page!.Rows.Count);
        Assert.Equal("g000", page.Rows[0].Group[0]);
        var differing = await _client.GetFromJsonAsync<VerificationResultPage>($"/api/replications/{name}/verification-results/{one.Id}?differingOnly=true", JsonOptions);
        Assert.All(differing!.Rows, r => Assert.Equal(VerificationRowStatus.Differs, r.Status));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "dbdatasync-verification-*.parquet"));   // the scratch copy of the read is deleted
    }

    [Fact]
    public async Task A_result_whose_file_the_retention_removed_answers_expired_naming_the_run_and_an_unknown_number_is_not_found()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        var (record, _) = Record(factory.Runner, name, "orders", DateTimeOffset.UtcNow.AddMinutes(-5), file: null);
        var listed = await _client.GetFromJsonAsync<List<VerificationResultRecord>>($"/api/replications/{name}/verification-results", JsonOptions);

        var expired = await _client.GetAsync($"/api/replications/{name}/verification-results/{listed![0].Id}");
        var unknown = await _client.GetAsync($"/api/replications/{name}/verification-results/12345");

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Contains($"{record.RunId}", await expired.Content.ReadAsStringAsync());
        Assert.Contains("expired", await expired.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_result_of_another_replication_is_not_found_under_this_ones_route_and_a_runner_result_is_not_deleted_here()
    {
        var mine = NewName();
        var other = NewName();
        factory.RunnerOwns(mine);
        factory.RunnerOwns(other);
        Record(factory.Runner, other, "orders", DateTimeOffset.UtcNow.AddMinutes(-5), await ParquetAsync(2));
        var theirs = (await _client.GetFromJsonAsync<List<VerificationResultRecord>>($"/api/replications/{other}/verification-results", JsonOptions))![0];

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/replications/{mine}/verification-results/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/replications/{other}/verification-results/{theirs.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_runner_that_cannot_be_asked_is_a_503_that_says_so_and_never_an_empty_list()
    {
        var name = NewName();
        factory.RunnerOwns(name);
        factory.Runner.Down = true;
        try
        {
            var response = await _client.GetAsync($"/api/replications/{name}/verification-results");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("could not be reached", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            factory.Runner.Down = false;
        }
    }

    [Fact]
    public async Task A_replication_on_the_old_path_lists_its_own_states_results_and_asks_no_runner()
    {
        var name = NewName();
        var before = factory.Runner.Calls;

        var list = await _client.GetFromJsonAsync<List<VerificationResultRecord>>($"/api/replications/{name}/verification-results", JsonOptions);

        Assert.Empty(list!);
        Assert.Equal(before, factory.Runner.Calls);
    }
}
