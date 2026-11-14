using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DbDataKit.State.Tracking;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E17 (the Sync half of E18): <c>GET api/v1/tracking</c> in the shape the console's page reads, the resolve of a divergence (administrator, audited, the same rules as Build's), the replication's
/// own tracking status with the retention that applies, and the refusal of a retention where no table is kept. No engine is needed: the copy and the divergence are rows of the state.
/// </summary>
public sealed class TrackingEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client = factory.CreateClient();

    private DbDataSync.State.StateDatabase State => factory.Services.GetRequiredService<DbDataSync.State.StateDatabase>();

    private (string Identity, long DivergenceId) Seed()
    {
        var identity = SyncPositionLogStream.Identity(DbDataSync.Drivers.Abstractions.PositionLogEngine.SqlServer, "sql01", "wh", "sales", $"orders{Guid.NewGuid():N}"[..14]);
        var store = new StateTrackingStore(State);
        var rows = Enumerable.Range(1, 5).Select(i => new TrackingRow($"u-{i}", i, $"{{\"entry_seq\":{i},\"committed_at\":\"2026-11-10T10:0{i}:00.0000000+00:00\"}}")).ToList();
        store.WriteAsync(identity, SyncPositionLogStream.Stream, rows, CancellationToken.None).GetAwaiter().GetResult();
        var divergence = new StateTrackingDivergences(State).Mark(identity, SyncPositionLogStream.Name, "The state's position is not one the target recorded.", new TrackingPosition(3, "u-3"), new TrackingPosition(3, "u-3"), new TrackingPosition(5, "u-5"));
        return (identity, divergence.Id);
    }

    [Fact]
    public async Task The_document_lists_the_copy_and_the_open_divergence_with_the_common_point_and_its_time()
    {
        var (identity, id) = Seed();

        var document = JsonNode.Parse(await _client.GetStringAsync("/api/v1/tracking"))!;

        Assert.Equal("sync-server", (string)document["holder"]!);
        var copy = Assert.Single(document["copies"]!.AsArray(), c => (string)c!["identity"]! == identity)!;
        Assert.Equal(5, (int)copy["streams"]![0]!["rows"]!);
        Assert.True((bool)copy["streams"]![0]!["diverged"]!);
        var divergence = Assert.Single(document["divergences"]!.AsArray(), d => (long)d!["id"]! == id)!;
        Assert.True((bool)divergence["open"]!);
        Assert.Equal("u-3", (string)divergence["common"]!["key"]!);
        Assert.StartsWith("2026-11-10T10:03", (string)divergence["common"]!["time"]!);
    }

    [Fact]
    public async Task An_administrator_resolves_a_divergence_once_and_the_copy_is_put_aside_from_the_common_point()
    {
        var (identity, id) = Seed();

        var bad = await _client.PostAsJsonAsync($"/api/v1/tracking/divergences/{id}/resolve", new { resolution = "forget" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/v1/tracking/divergences/99999999/resolve", new { resolution = "archive" })).StatusCode);

        var resolved = await _client.PostAsJsonAsync($"/api/v1/tracking/divergences/{id}/resolve", new { resolution = "archive" });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync($"/api/v1/tracking/divergences/{id}/resolve", new { resolution = "archive" })).StatusCode);

        var store = new StateTrackingStore(State);
        Assert.Equal(3, store.Count(identity, SyncPositionLogStream.Name));
        Assert.Equal(2, store.ArchivedCount(identity, SyncPositionLogStream.Stream, $"divergence-{id}"));
        var after = JsonNode.Parse(await _client.GetStringAsync("/api/v1/tracking"))!;
        var listed = Assert.Single(after["divergences"]!.AsArray(), d => (long)d!["id"]! == id)!;
        Assert.False((bool)listed["open"]!);
        Assert.Equal("archived", (string)listed["status"]!);
        Assert.False((bool)Assert.Single(after["copies"]!.AsArray(), c => (string)c!["identity"]! == identity)!["streams"]![0]!["diverged"]!);
    }

    [Fact]
    public async Task A_divergence_of_another_kind_of_copy_is_not_this_servers_to_resolve()
    {
        var foreign = new StateTrackingDivergences(State).Mark("buildtracking:mssql://x/y@z", "run_log", "elsewhere", null, null, null);

        var response = await _client.PostAsJsonAsync($"/api/v1/tracking/divergences/{foreign.Id}/resolve", new { resolution = "reset" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not a copy of Sync's entries", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(JsonNode.Parse(await _client.GetStringAsync("/api/v1/tracking"))!["divergences"]!.AsArray(), d => (long)d!["id"]! == foreign.Id);   // and its document does not list it
    }

    [Fact]
    public async Task A_replication_that_keeps_its_state_on_the_target_reports_its_retention_and_one_that_keeps_none_refuses_it()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var connection = $"t-{suffix}";
        var replication = $"t-repl-{suffix}";
        (await _client.PutAsJsonAsync($"/api/connections/{connection}", new ConnectionInput
        {
            Name = connection, DriverType = DriverIds.MsSql, Host = "127.0.0.1", Port = 1, Database = "Nowhere",
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true", ["Connect Timeout"] = "2" }, AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "unused",
        }, JsonOptions)).EnsureSuccessStatusCode();
        ReplicationTaskConfig Task(TrackingConfig? tracking) => new()
        {
            Name = replication, Enabled = false,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = connection, Database = "Src" }, Target = new EndpointRef { ConnectionName = connection, Database = "Nowhere" } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = MsSqlDriverKinds.Cdc }, Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable }, Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge } },
            Tracking = tracking,
        };

        var refused = await _client.PutAsJsonAsync($"/api/replications/{replication}", Task(new TrackingConfig { Mode = "state", Retention = new TrackingRetention { Entries = 5 } }), JsonOptions);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("tracking.retention", await refused.Content.ReadAsStringAsync());

        (await _client.PutAsJsonAsync($"/api/replications/{replication}", Task(new TrackingConfig { Mode = "target", Retention = new TrackingRetention { Entries = 50 } }), JsonOptions)).EnsureSuccessStatusCode();
        var status = JsonNode.Parse(await _client.GetStringAsync($"/api/replications/{replication}/tracking"))!;
        Assert.Equal("target", (string)status["effectiveMode"]!);
        Assert.Equal((50, 7.0), ((int)status["retentionEntries"]!, (double)status["retentionDays"]!));
        Assert.Null(status["copy"]);   // nothing followed yet

        var follow = await _client.PostAsync($"/api/replications/{replication}/tracking/catch-up", null);
        var followed = JsonNode.Parse(await follow.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);
        Assert.Contains("did not answer", (string)followed["copy"]!["error"]!);   // a target that is down is the status's error, never an exception or a failed call
        Assert.False((bool)followed["copy"]!["caughtUp"]!);
    }

    [Fact]
    public async Task Following_a_replication_that_keeps_its_state_in_the_runners_state_is_refused_in_a_sentence()
    {
        var response = await _client.PostAsync($"/api/replications/does-not-exist-{Guid.NewGuid():N}/tracking/catch-up", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("runner's state only", await response.Content.ReadAsStringAsync());
    }
}
