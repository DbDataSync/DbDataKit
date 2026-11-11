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
/// The endpoints an operator sets a mapping's read intent and hold through — beside
/// <c>refresh-metadata</c>, and only the storage half of the design: nothing here changes what a pass
/// actually does, because nothing consumes the intent yet. See phase 100.
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

    /// <summary>A mapping that has never run reports the application default, not an absence a client
    /// has to resolve itself — the guarantee that a full load always traces to somebody's choice
    /// depends on this being visible before a first pass, not only after one.</summary>
    [Fact]
    public async Task ANewMapping_ReportsTheApplicationDefault()
    {
        var (replication, mapping) = await SetUpAsync();

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.InitialLoad, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Null(state.Watermark);
    }

    [Fact]
    public async Task ANewMapping_ReportsTheReplicationsConfiguredDefault()
    {
        var (replication, mapping) = await SetUpAsync(replicationDefault: ReadIntent.ChangesFromLatest);

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.ChangesFromLatest, state!.Intent);
    }

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        var (replication, mapping) = await SetUpAsync();

        var setResponse = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);
        setResponse.EnsureSuccessStatusCode();

        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);

        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
    }

    /// <summary>
    /// A first-pass initial load that fails leaves the mapping in <c>Loading</c> with its batch never to
    /// complete — and nothing else clears it. Choosing a new intent with the hold cleared is the way out, and
    /// it must also forget the abandoned batch so that batch cannot later promote its old position.
    /// </summary>
    [Fact]
    public async Task LeavingLoadingByHand_ClearsTheHold_AndForgetsThePendingLoad()
    {
        var (replication, mapping) = await SetUpAsync();
        var key = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" },
            DbDataSync.Core.Sql.MsSqlDialect.Instance);
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        watermarks.SetPendingLoad(replication, mapping, key, "ABC123", null, "failed-batch");
        Assert.Equal(ReadHold.Loading, watermarks.GetReadState(replication, mapping, key)!.Hold);

        (await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None))
            .EnsureSuccessStatusCode();

        var state = watermarks.GetReadState(replication, mapping, key)!;
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.Equal(ReadIntent.ChangesFromEarliest, state.Intent);

        // The abandoned batch no longer promotes anything if it ever completes.
        Assert.False(watermarks.PromotePendingLoad("failed-batch"));
        Assert.Null(watermarks.GetReadState(replication, mapping, key)!.Watermark);
    }

    /// <summary>Continuing from the captured position is what a completed load would have done: the position
    /// captured before the load becomes the live one, the intent is Changes, and the hold clears.</summary>
    [Fact]
    public async Task UsingTheCapturedPosition_PromotesIt_AsACompletedLoadWould()
    {
        var (replication, mapping) = await SetUpAsync();
        var key = WatermarkKey.Build(
            new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" },
            DbDataSync.Core.Sql.MsSqlDialect.Instance);
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        watermarks.SetPendingLoad(replication, mapping, key, "ABC123", null, "failed-batch");

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/table-mappings/{mapping}/read-state",
            new { intent = ReadIntent.InitialLoad, hold = ReadHold.Loading, useCapturedPosition = true }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var state = watermarks.GetReadState(replication, mapping, key)!;
        Assert.Equal("ABC123", state.Watermark);
        Assert.Equal(ReadIntent.Changes, state.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
        Assert.False(watermarks.PromotePendingLoad("failed-batch"));
    }

    [Fact]
    public async Task UsingTheCapturedPosition_OnAMappingThatIsNotLoading_IsRefused()
    {
        var (replication, mapping) = await SetUpAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{replication}/table-mappings/{mapping}/read-state",
            new { intent = ReadIntent.Changes, hold = ReadHold.None, useCapturedPosition = true }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no captured position", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The one call that moves both at once: an operator recovering from a hold sets a fresh
    /// intent and clears the hold together, never in a request that could leave one half done.</summary>
    [Fact]
    public async Task SettingAnIntentAndClearingAHold_HappensInOneCall()
    {
        var (replication, mapping) = await SetUpAsync();
        (await SetReadStateAsync(replication, mapping, ReadIntent.InitialLoad, ReadHold.PositionExpired))
            .EnsureSuccessStatusCode();

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
        Assert.Equal(ReadIntent.ChangesFromEarliest, state!.Intent);
        Assert.Equal(ReadHold.None, state.Hold);
    }

    /// <summary>An intent cannot move under a pass that is already acting on it.</summary>
    [Fact]
    public async Task SetReadState_WhileTheRunLockIsHeld_IsRefused()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.Primary, mapping, Guid.NewGuid()));

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // And nothing moved: a refused request must not have partially applied.
        var state = await (await GetReadStateAsync(replication, mapping))
            .Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
        Assert.Equal(ReadIntent.InitialLoad, state!.Intent);
    }

    [Fact]
    public async Task SetReadState_OnceTheLockIsReleased_IsAllowedAgain()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.Primary, mapping, Guid.NewGuid()));
        runLocks.Release(replication, RunKind.Primary, mapping);

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>A BulkLoad lock is refused on the same footing as a Primary one — a reload in progress
    /// is still a pass acting on this mapping.</summary>
    [Fact]
    public async Task SetReadState_WhileABulkLoadLockIsHeld_IsRefused()
    {
        var (replication, mapping) = await SetUpAsync();
        var runLocks = factory.Services.GetRequiredService<RunLockStore>();
        Assert.True(runLocks.TryAcquire(replication, RunKind.BulkLoad, mapping, Guid.NewGuid()));

        var response = await SetReadStateAsync(replication, mapping, ReadIntent.ChangesFromEarliest, ReadHold.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

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

    private sealed record MappingReadStateDto(ReadIntent Intent, ReadHold Hold, string? Watermark, DateTimeOffset? WatermarkTimeUtc);

    // ---- plan 28c E15b: the position history and the rewind

    private sealed record PositionChange(long Id, long Version, string Part, string? Old, string? New, string? Actor, string? RunId, DateTimeOffset At);

    private async Task<(string Replication, string Mapping, ChangeWatermarkStore Store, string Key)> WithThreePositionsAsync()
    {
        var (replication, mapping) = await SetUpAsync();
        var store = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        var key = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }, DbDataSync.Core.Sql.MsSqlDialect.Instance);
        foreach (var position in new[] { "100", "200", "300" }) store.SetWatermark(replication, mapping, key, position);
        return (replication, mapping, store, key);
    }

    [Fact]
    public async Task The_position_history_lists_the_changes_newest_first_and_a_rewind_moves_the_mapping_back_under_the_administrators_name()
    {
        var (replication, mapping, store, key) = await WithThreePositionsAsync();
        var url = $"/api/replications/{replication}/table-mappings/{mapping}";

        var history = (await _client.GetFromJsonAsync<List<PositionChange>>($"{url}/position-history", JsonOptions))!;
        var toThreeHundred = history.First(c => c is { Part: "position", New: "300" });
        Assert.Equal("200", toThreeHundred.Old);

        var rewound = await _client.PostAsJsonAsync($"{url}/position-rewind", new { historyId = toThreeHundred.Id }, JsonOptions);
        rewound.EnsureSuccessStatusCode();
        var state = (await rewound.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions))!;
        Assert.Equal("200", state.Watermark);
        Assert.Equal("200", store.GetWatermark(replication, mapping, key));

        var after = (await _client.GetFromJsonAsync<List<PositionChange>>($"{url}/position-history", JsonOptions))!;
        Assert.NotNull(after.First().Actor);
        Assert.NotEqual("dbdatasync", after.First().Actor);       // a person's rewind names the person, not the engine

        // the next pass returns to the same final position
        store.SetWatermark(replication, mapping, key, "300");
        Assert.Equal("300", store.GetWatermark(replication, mapping, key));
    }

    [Fact]
    public async Task A_rewind_to_a_change_that_is_not_in_the_history_changes_nothing()
    {
        var (replication, mapping, store, key) = await WithThreePositionsAsync();

        var response = await _client.PostAsJsonAsync($"/api/replications/{replication}/table-mappings/{mapping}/position-rewind", new { historyId = 987654321L }, JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("300", store.GetWatermark(replication, mapping, key));
    }

    [Fact]
    public async Task A_replication_the_runner_owns_answers_409_naming_the_runners_checkpoints_and_changes_nothing()
    {
        var (replication, mapping, store, key) = await WithThreePositionsAsync();
        var access = factory.Services.GetRequiredService<DbDataSync.Api.Runner.RunnerAccess>();
        access.File.SetOwner(replication, DbDataKit.State.TaskOwners.Runner);
        var url = $"/api/replications/{replication}/table-mappings/{mapping}";

        var history = await _client.GetAsync($"{url}/position-history");
        var rewind = await _client.PostAsJsonAsync($"{url}/position-rewind", new { historyId = 1 }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, history.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rewind.StatusCode);
        Assert.Contains("Checkpoints", await rewind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("300", store.GetWatermark(replication, mapping, key));
    }
}
