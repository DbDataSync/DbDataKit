using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The operator's way to ask for a full reload of a mapping. Offered rather than performed: a full
/// reload of a table that fell behind can be hours of work, and nobody asked for it just because a
/// pass failed. Per phase 101's retarget of <c>ResyncService</c>, it now sets
/// <see cref="ReadIntent.InitialLoad"/> and clears the hold rather than clearing the stored watermark
/// and forcing a <c>BatchReload</c> — and it is no longer gated to only a <c>PositionExpired</c> failure.
/// </summary>
public sealed class ResyncTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(string Replication, string Mapping)> SetUpAsync()
    {
        var replicationName = $"resync-{Guid.NewGuid():N}";

        // A real connection, never opened. The resync has to know which engine spelled the stored
        // watermark key before it can clear the right row, and that comes from the connection's
        // driver type — see ResyncService.
        (await _client.PutAsJsonAsync("/api/connections/src", new ConnectionInput
        {
            Name = "src",
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Database = "App",
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();

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

    /// <summary>Records a failed run the way a runner would, so the endpoint is exercised against the
    /// state it actually reacts to rather than a hand-built object.</summary>
    private Guid RecordFailedRun(string replication, string mapping, string? failureKind)
    {
        // Enqueued and then completed, which is the only way a TaskRuns row comes into existence —
        // so this exercises the endpoint against state the runner could actually have produced.
        var runId = factory.Services.GetRequiredService<WorkQueueStore>()
            .Enqueue(replication, RunKind.Primary, mapping);
        factory.Services.GetRequiredService<TaskRunStore>()
            .CompleteRun(runId, RunStatus.Failed, 0, 0, "position gone", failureKind);
        return runId;
    }

    /// <summary>
    /// A resync is a request of the drain on the runner (plan 28c E12, E22: the Runner is Sync's only scheduler), whatever failed and whether or not the run was a position that expired. With no runner
    /// linked nothing can run it, and the answer says so (409) instead of setting an intent this server no longer reads.
    /// </summary>
    [Theory]
    [InlineData(RunFailureKinds.PositionExpired)]
    [InlineData(null)]
    public async Task A_resync_goes_to_the_runner_and_with_none_linked_is_refused_saying_so(string? failureKind)
    {
        var (replication, mapping) = await SetUpAsync();
        var runId = RecordFailedRun(replication, mapping, failureKind);

        var response = await _client.PostAsync($"/api/runs/{runId}/resync", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No runner is linked", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownRun_IsNotFound() =>
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.PostAsync($"/api/runs/{Guid.NewGuid()}/resync", null)).StatusCode);

    /// <summary>The failure kind reaches the client, because the Resync affordance is keyed off it.</summary>
    [Fact]
    public async Task TheFailureKind_IsReportedInRunHistory()
    {
        var (replication, mapping) = await SetUpAsync();
        RecordFailedRun(replication, mapping, RunFailureKinds.PositionExpired);

        // { runs, nextCursor } since phase 104 added paging — a bare array before that.
        var history = await _client.GetFromJsonAsync<RunHistoryResponseDto>(
            $"/api/replications/{replication}/runs", JsonOptions);

        Assert.Equal(RunFailureKinds.PositionExpired, Assert.Single(history!.Runs).FailureKind);
    }

    /// <summary>Deserialization target for the paged history response — see
    /// <c>DbDataSync.Api.Models.RunHistoryResponse</c>, which this mirrors field for field.</summary>
    private sealed record RunHistoryResponseDto(List<TaskRunRecord> Runs, string? NextCursor);
}
