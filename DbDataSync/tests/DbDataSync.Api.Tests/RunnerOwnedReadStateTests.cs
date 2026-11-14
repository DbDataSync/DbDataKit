using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Config.Git;
using DbDataKit.Runner.Client;
using DbDataSync.Api.Controllers;
using DbDataSync.Api.Runner;
using DbDataSync.Core.Config;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E12g (E22 precondition P3) over the Sync API with a fake runner: the read intent, hold and captured position of a mapping of a replication the runner owns are read from and changed in the runner's checkpoint
/// <c>watermark</c>, where its drain reads them. A drain that moves the item meanwhile is read again; one that never stops moving is a 409; a runner that is down is a 503; nothing is written to this server's own state.
/// </summary>
public sealed class RunnerOwnedReadStateTests(RunnerOwnedApiFactory factory) : IClassFixture<RunnerOwnedApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private (string Replication, string Mapping, string ItemKey) SetUp()
    {
        var name = $"rs-{Guid.NewGuid():N}"[..14];
        var config = factory.Services.GetRequiredService<ConfigRepository>();
        config.SaveConnection(new ConnectionInput { Name = "src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "DbDataSync_Test_Pw1" }, Author);
        config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "src", Database = "App" }, Target = new EndpointRef { ConnectionName = "src", Database = "DW" } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, Author);
        config.SaveTableMapping(name, new TableMappingConfig
        {
            Name = "orders",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Orders" }],
            Targets = [new TableSpec { Schema = "dbo", Table = "Orders" }],
        }, Author);
        factory.RunnerOwns(name);
        factory.Runner.Checkpoint.Clear();
        factory.Runner.CheckpointSets.Clear();
        factory.Runner.MovedUnderneath = 0;
        var table = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }, DbDataSync.Core.Sql.MsSqlDialect.Instance);
        return (name, "orders", WatermarkCheckpoint.ItemKey("orders", table));
    }

    private Task<HttpResponseMessage> Post(string replication, string mapping, object body) =>
        _client.PostAsJsonAsync($"/api/replications/{replication}/table-mappings/{mapping}/read-state", body, JsonOptions);

    private void Seed(string key, long version, params (string Part, string Value)[] parts) =>
        factory.Runner.Checkpoint.Add(new ClientCheckpointItem(key, version, parts.ToDictionary(p => p.Part, p => p.Value)));

    private ClientCheckpointItem Item(string key) => factory.Runner.Checkpoint.Single(i => i.Key == key);

    [Fact]
    public async Task The_read_state_is_the_runners_item_and_a_mapping_with_none_reads_as_the_defaults()
    {
        var (replication, mapping, key) = SetUp();
        var url = $"/api/replications/{replication}/table-mappings/{mapping}/read-state";

        var none = (await _client.GetFromJsonAsync<MappingReadStateDto>(url, JsonOptions))!;
        Seed(key, 3, ("position", "0x9F"), ("position_time", "2026-11-09T11:30:00.0000000Z"), ("intent", "ChangesFromEarliest"), ("hold", "PositionExpired"));
        var held = (await _client.GetFromJsonAsync<MappingReadStateDto>(url, JsonOptions))!;

        Assert.Equal((ReadIntent.InitialLoad, ReadHold.None, null), (none.Intent, none.Hold, none.Watermark));
        Assert.Equal((ReadIntent.ChangesFromEarliest, ReadHold.PositionExpired, "0x9F"), (held.Intent, held.Hold, held.Watermark));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 11, 30, 0, TimeSpan.Zero), held.WatermarkTimeUtc);
    }

    [Fact]
    public async Task An_intent_and_a_hold_are_written_to_the_runners_item_and_nothing_to_this_servers_state()
    {
        var (replication, mapping, key) = SetUp();
        Seed(key, 2, ("position", "0x9F"), ("intent", "Changes"), ("hold", "None"));

        var response = await Post(replication, mapping, new { intent = ReadIntent.ChangesFromEarliest, hold = ReadHold.Paused, note = "month end" });

        response.EnsureSuccessStatusCode();
        var state = (await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions))!;
        Assert.Equal((ReadIntent.ChangesFromEarliest, ReadHold.Paused, "0x9F"), (state.Intent, state.Hold, state.Watermark));
        var item = Item(key);
        Assert.Equal((3L, "ChangesFromEarliest", "Paused", "0x9F"), (item.Version, item.Parts["intent"], item.Parts["hold"], item.Parts["position"]));
        var table = WatermarkKey.Build(new SourceTableRef { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "Orders" }, DbDataSync.Core.Sql.MsSqlDialect.Instance);
        Assert.Null(factory.Services.GetRequiredService<ChangeWatermarkStore>().GetReadState(replication, mapping, table));
    }

    [Fact]
    public async Task The_captured_position_is_promoted_on_the_runner_as_a_finished_load_would_have_left_it_and_a_mapping_not_loading_is_refused()
    {
        var (replication, mapping, key) = SetUp();
        var refused = await Post(replication, mapping, new { intent = ReadIntent.Changes, hold = ReadHold.None, useCapturedPosition = true });
        Seed(key, 4, ("position", "0x01"), ("intent", "InitialLoad"), ("hold", "Loading"), ("pending_position", "ABC123"), ("pending_position_time", "2026-11-10T08:00:00.0000000Z"), ("pending_batch", "b-1"));

        var promoted = await Post(replication, mapping, new { intent = ReadIntent.InitialLoad, hold = ReadHold.Loading, useCapturedPosition = true });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("no captured position", await refused.Content.ReadAsStringAsync());
        promoted.EnsureSuccessStatusCode();
        var item = Item(key);
        Assert.Equal(5, item.Version);
        Assert.Equal(("ABC123", "2026-11-10T08:00:00.0000000Z", "Changes", "None"), (item.Parts["position"], item.Parts["position_time"], item.Parts["intent"], item.Parts["hold"]));
        Assert.DoesNotContain(item.Parts.Keys, k => k.StartsWith("pending_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaving_loading_by_hand_abandons_the_pending_position()
    {
        var (replication, mapping, key) = SetUp();
        Seed(key, 1, ("intent", "InitialLoad"), ("hold", "Loading"), ("pending_position", "ABC123"), ("pending_batch", "b-1"));

        (await Post(replication, mapping, new { intent = ReadIntent.InitialLoad, hold = ReadHold.None })).EnsureSuccessStatusCode();

        var item = Item(key);
        Assert.Equal("None", item.Parts["hold"]);
        Assert.DoesNotContain(item.Parts.Keys, k => k.StartsWith("pending_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_drain_that_moves_the_item_meanwhile_is_read_again_and_one_that_never_stops_is_a_409_that_changes_nothing()
    {
        var (replication, mapping, key) = SetUp();
        Seed(key, 1, ("position", "10"), ("intent", "Changes"), ("hold", "None"));
        factory.Runner.MovedUnderneath = 2;

        var retried = await Post(replication, mapping, new { intent = ReadIntent.Changes, hold = ReadHold.Paused });

        retried.EnsureSuccessStatusCode();
        Assert.Equal(("Paused", 4L), (Item(key).Parts["hold"], Item(key).Version));   // two drain moves (2, 3), then this change
        factory.Runner.MovedUnderneath = 100;
        var busy = await Post(replication, mapping, new { intent = ReadIntent.Changes, hold = ReadHold.None });

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("Paused", Item(key).Parts["hold"]);
    }

    [Fact]
    public async Task The_status_of_a_replication_carries_the_runners_row_and_running_is_whether_its_drain_is_up_there()
    {
        var (replication, _, _) = SetUp();
        var due = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        factory.Runner.TaskStatus = new ClientTaskStatus(RunnerAccess.TaskOf(replication), "runner", due, new ClientLastRun("1", "succeeded", due.AddHours(-1), due.AddHours(-1).AddMinutes(2), 1, 40), 1, 2, true, "ada");
        var url = $"/api/replications/{replication}/status";

        var up = (await _client.GetFromJsonAsync<JsonElement>(url, JsonOptions));
        factory.Runner.Down = true;
        var down = (await _client.GetFromJsonAsync<JsonElement>(url, JsonOptions));
        factory.Runner.Down = false;
        factory.Runner.TaskStatus = null;

        Assert.True(up.GetProperty("running").GetBoolean());   // plan 28c E22: the drain on the runner is what runs
        var runner = up.GetProperty("runner");
        Assert.True(runner.GetProperty("reachable").GetBoolean());
        Assert.True(runner.GetProperty("running").GetBoolean());
        Assert.Equal((1, 2, true, "ada"), (runner.GetProperty("runningCount").GetInt32(), runner.GetProperty("queued").GetInt32(), runner.GetProperty("paused").GetBoolean(), runner.GetProperty("pausedBy").GetString()));
        Assert.Equal("succeeded", runner.GetProperty("lastRun").GetProperty("outcome").GetString());
        Assert.False(down.GetProperty("running").GetBoolean());
        Assert.False(down.GetProperty("runner").GetProperty("reachable").GetBoolean());
        Assert.Contains("down", down.GetProperty("runner").GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_runner_that_is_down_is_a_503_and_never_an_empty_state()
    {
        var (replication, mapping, _) = SetUp();
        factory.Runner.Down = true;
        try
        {
            var read = await _client.GetAsync($"/api/replications/{replication}/table-mappings/{mapping}/read-state");
            var write = await Post(replication, mapping, new { intent = ReadIntent.Changes, hold = ReadHold.Paused });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, write.StatusCode);
        }
        finally { factory.Runner.Down = false; }
    }
}
