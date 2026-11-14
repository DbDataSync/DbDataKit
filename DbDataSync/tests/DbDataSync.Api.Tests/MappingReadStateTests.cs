using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataKit.Config.Git;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The endpoints an operator sets a mapping's read intent and hold through (phase 100). Since plan 28c E22 they are the runner's (its checkpoint, which the drain
/// reads); what is left to test here without a runner is that this server answers for none of it itself.
/// </summary>
public sealed class MappingReadStateTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(string Replication, string Mapping)> SetUpAsync(ReadIntent? replicationDefault = null)
    {
        var replicationName = $"read-state-{Guid.NewGuid():N}";

        // A real connection, never opened — this endpoint only needs to know which engine spelled the
        // watermark key, the same reasoning ResyncService and ChangeSourceResolver already rely on.
        // Saved directly through ConfigRepository rather than PUT /api/connections/{name}: that
        // endpoint's Negotiate authentication handler throws under TestServer in this sandbox
        // (`NotSupportedException: ... requires a server that supports IConnectionItemsFeature like
        // Kestrel`), a pre-existing environment defect confirmed on a clean checkout and unrelated to
        // phase 100 — ResyncTests.cs hits the identical failure calling the same endpoint.
        factory.Services.GetRequiredService<ConfigRepository>().SaveConnection(new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, Author);

        (await _client.PutAsJsonAsync($"/api/replications/{replicationName}", new ReplicationTaskConfig
        {
            Name = replicationName,
            Endpoints = new TaskEndpoints
            {
                Source = new EndpointRef { ConnectionName = "src", Database = "App" },
                Target = new EndpointRef { ConnectionName = "tgt", Database = "DW" },
            },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
            DefaultReadIntent = replicationDefault,
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PutAsJsonAsync(
            $"/api/replications/{replicationName}/table-mappings/orders", new TableMappingConfig
            {
                Name = "orders",
                Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
                Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
            }, JsonOptions)).EnsureSuccessStatusCode();

        return (replicationName, "orders");
    }

    private Task<HttpResponseMessage> GetReadStateAsync(string replication, string mapping) =>
        _client.GetAsync($"/api/replications/{replication}/table-mappings/{mapping}/read-state");

    private Task<HttpResponseMessage> SetReadStateAsync(
        string replication, string mapping, ReadIntent intent, ReadHold hold) =>
        _client.PostAsJsonAsync(
            $"/api/replications/{replication}/table-mappings/{mapping}/read-state",
            new { intent, hold }, JsonOptions);

    [Fact]
    public async Task GetReadState_ForAMissingReplication_IsNotFound()
    {
        var response = await GetReadStateAsync("no-such-replication", "orders");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReadState_ForAMissingMapping_IsNotFound()
    {
        var (replication, _) = await SetUpAsync();
        var response = await GetReadStateAsync(replication, "no-such-mapping");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(string Replication, string Mapping, ChangeWatermarkStore Store, string Key)> WithThreePositionsAsync()
    {
        var (replication, mapping) = await SetUpAsync();
        var store = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var key = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }, DbDataSync.Core.Sql.MsSqlDialect.Instance);
        foreach (var position in new[] { "100", "200", "300" }) store.SetWatermark(replication, mapping, key, position);
        return (replication, mapping, store, key);
    }

    [Fact]
    public async Task The_position_history_and_its_rewind_answer_409_naming_the_runners_checkpoints_and_change_nothing()
    {
        // plan 28c E22: the Runner is Sync's only scheduler and keeps every position, so this server keeps none to show or rewind
        var (replication, mapping, store, key) = await WithThreePositionsAsync();
        var url = $"/api/replications/{replication}/table-mappings/{mapping}";

        var history = await _client.GetAsync($"{url}/position-history");
        var rewind = await _client.PostAsJsonAsync($"{url}/position-rewind", new { historyId = 1 }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, history.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rewind.StatusCode);
        Assert.Contains("Checkpoints", await rewind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("300", store.GetWatermark(replication, mapping, key));
    }

    [Fact]
    public async Task The_read_state_is_the_runners_and_with_no_runner_to_ask_is_a_503_that_changes_nothing()
    {
        var (replication, mapping, store, key) = await WithThreePositionsAsync();
        var before = store.GetReadState(replication, mapping, key);

        var set = await SetReadStateAsync(replication, mapping, ReadIntent.InitialLoad, ReadHold.Paused);
        var get = await GetReadStateAsync(replication, mapping);

        // the runner's own behaviour is RunnerOwnedReadStateTests'; this factory has no runner to ask (plan 28c E12g, E22)
        Assert.True(set.StatusCode is HttpStatusCode.ServiceUnavailable, await set.Content.ReadAsStringAsync());
        Assert.True(get.StatusCode is HttpStatusCode.ServiceUnavailable, await get.Content.ReadAsStringAsync());
        Assert.Equal(before, store.GetReadState(replication, mapping, key));
    }
}
