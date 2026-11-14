using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Runner.Client;
using DbDataSync.Api.Runner;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c E13: the lag of a replication the runner owns is the figure the supervisor path gives for the same data. Two replications read one source database; the one this server owns has its position and the polling gate's
/// checks in the state database, the one the runner owns has the same numbers where the runner keeps them (the checkpoint <c>watermark</c>: its <c>position</c> and <c>position_time</c> parts, one item per source table, and the
/// <c>change-checks</c> step each cycle of a drain records) behind a fake runner. <see cref="ReaderLagService"/> is the one computation on both, so the figures are compared with what each case's arithmetic says they are.
/// </summary>
public sealed class RunnerLagTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset Origin = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Pair(string Own, string OnRunner, string Database, string WatermarkKey);

    private async Task<Pair> SetUpAsync(string readerKind)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var database = $"App_{suffix}";
        (await _client.PutAsJsonAsync("/api/connections/lag-src", new ConnectionInput
        {
            Name = "lag-src", DriverType = DriverIds.MsSql, Host = "localhost", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = "DbDataSync_Test_Pw1",
        }, JsonOptions)).EnsureSuccessStatusCode();
        var names = new[] { $"own-{suffix}", $"onrunner-{suffix}" };
        foreach (var name in names)
        {
            (await _client.PutAsJsonAsync($"/api/replications/{name}", new ReplicationTaskConfig
            {
                Name = name,
                Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "lag-src", Database = database }, Target = new EndpointRef { ConnectionName = "lag-src", Database = "DW" } },
                Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
                ChangeProcessing = new ChangeProcessingConfig
                {
                    Reader = new ReaderConfig { Kind = readerKind, Options = [] },
                    Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable },
                    Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
                },
            }, JsonOptions)).EnsureSuccessStatusCode();
            (await _client.PutAsJsonAsync($"/api/replications/{name}/table-mappings/orders", new TableMappingConfig
            {
                Name = "orders", Sources = [new SourceTableSpec { Schema = "dbo", Table = "orders" }], Targets = [new TableSpec { Schema = "dbo", Table = "orders" }],
            }, JsonOptions)).EnsureSuccessStatusCode();
        }

        var task = factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(names[0]);
        var key = WatermarkKey.Build(EndpointResolution.ResolveSource(task, new SourceTableSpec { Schema = "dbo", Table = "orders" }), MsSqlDialect.Instance);
        return new Pair(names[0], names[1], database, key);
    }

    /// <summary>The same position and checks, written where each path keeps them.</summary>
    private (ReaderLagService Own, ReaderLagService OnRunner, FakeRunnerRuns Runner, RunnerLagInputs Inputs) Fill(
        Pair pair, string kind, string? position, DateTimeOffset? positionTime, params (string? Value, DateTimeOffset CheckedAt, DateTimeOffset? SourceTime)[] checks)
    {
        var watermarks = factory.Services.GetRequiredService<ChangeWatermarkStore>();
        if (position is not null) watermarks.SetWatermark(pair.Own, "orders", pair.WatermarkKey, position, positionTime);
        var db = factory.Services.GetRequiredService<StateDatabase>();
        foreach (var (value, checkedAt, sourceTime) in checks)
            db.Retry(() =>
            {
                using var connection = db.OpenConnection();
                using var cmd = db.Command(connection, "INSERT INTO {table:sync_state.change_check} (connection_name, source_database, source_kind, value_text, checked_at, source_at) VALUES ($c, $d, $k, $v, $at, $st);");
                cmd.Bind(db, "c", "lag-src");
                cmd.Bind(db, "d", pair.Database);
                cmd.Bind(db, "k", kind);
                cmd.Bind(db, "v", value, System.Data.DbType.String);
                cmd.Bind(db, "at", checkedAt);
                cmd.Bind(db, "st", sourceTime, sourceTime is null ? System.Data.DbType.DateTimeOffset : null);
                cmd.ExecuteNonQuery();
            });

        var runner = new FakeRunnerRuns();
        var task = RunnerAccess.TaskOf(pair.OnRunner);
        if (position is not null)
        {
            var parts = new Dictionary<string, string> { ["position"] = position };
            if (positionTime is { } at) parts["position_time"] = at.UtcDateTime.ToString("O");
            runner.Checkpoint.Add(new ClientCheckpointItem(WatermarkCheckpoint.ItemKey("orders", pair.WatermarkKey), 2, parts));
        }

        // a drain records the checks of each cycle as a step; two cycles, to show the readings are read across them
        foreach (var group in checks.Chunk(2).Select((chunk, i) => (chunk, i)))
        {
            var batch = new ChangeCheckBatch(pair.OnRunner, [.. group.chunk.Select(c => new ChangeCheckEntry("lag-src", pair.Database, kind, c.Value, c.SourceTime, c.CheckedAt))]);
            runner.AddStep(task, ChangeCheckText.StepName, $"{pair.OnRunner}/{group.chunk[0].CheckedAt.UtcTicks}", JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web)), group.chunk[0].CheckedAt);
        }

        var file = new RunnerLinkFile(Path.Combine(Directory.CreateTempSubdirectory("runner-link-").FullName, RunnerLinkFile.FileName));
        file.SetLink(new RunnerLink("runner-1", "prod", "sales-set"));
        var inputs = new RunnerLagInputs(new RunnerAccess(file, _ => runner));
        ReaderLagService Build(RunnerLagInputs? i) => new(
            factory.Services.GetRequiredService<ChangeSourceResolver>(), watermarks, factory.Services.GetRequiredService<ChangeCheckStore>(), NullLogger<ReaderLagService>.Instance, i);
        return (Build(null), Build(inputs), runner, inputs);
    }

    private async Task<(ReaderLag Own, ReaderLag OnRunner)> BothAsync(Pair pair, (ReaderLagService Own, ReaderLagService OnRunner, FakeRunnerRuns Runner, RunnerLagInputs Inputs) services)
    {
        Assert.True(await services.Inputs.RefreshAsync(pair.OnRunner, CancellationToken.None));
        var config = factory.Services.GetRequiredService<ConfigRepository>();
        var own = services.Own.Describe(config.LoadReplicationTask(pair.Own), "orders");
        var onRunner = services.OnRunner.Describe(config.LoadReplicationTask(pair.OnRunner), "orders");
        // the reading the monitoring row is made of is the same, too
        var reading = ReplicationLagReading.For(services.Own, config, pair.Own)?.Behind;
        Assert.Equal(reading, ReplicationLagReading.For(services.OnRunner, config, pair.OnRunner)?.Behind);
        return (own, onRunner);
    }

    [Fact]
    public async Task CdcLag_Of_A_Runner_Owned_Replication_Equals_The_Supervisor_Paths_On_The_Same_Data()
    {
        var pair = await SetUpAsync(MsSqlDriverKinds.Cdc);
        var lsn = MsSqlCdcCatalog.ToWatermark([0, 0, 0, 42, 0, 0, 0, 100, 0, 1]);
        var services = Fill(pair, MsSqlDriverKinds.Cdc, lsn, Origin - TimeSpan.FromMinutes(5),
            (MsSqlCdcCatalog.ToWatermark([0, 0, 0, 42, 0, 0, 0, 150, 0, 1]), Origin - TimeSpan.FromMinutes(1), Origin - TimeSpan.FromMinutes(2)),
            (MsSqlCdcCatalog.ToWatermark([0, 0, 0, 42, 0, 0, 0, 200, 0, 1]), Origin, Origin),
            (null, Origin + TimeSpan.FromSeconds(5), null));   // the latest poll found no position: the figure is measured from the one before, as it is on both paths

        var (own, onRunner) = await BothAsync(pair, services);

        Assert.Equal(TimeSpan.FromMinutes(5), own.ExactLag);
        Assert.Equal(own, onRunner);
    }

    [Fact]
    public async Task ChangeTrackingLag_Exact_Equals_The_Supervisor_Paths_On_The_Same_Data()
    {
        var pair = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        var services = Fill(pair, MsSqlDriverKinds.ChangeTracking, "100", Origin - TimeSpan.FromMinutes(3),
            ("180", Origin - TimeSpan.FromSeconds(10), Origin - TimeSpan.FromSeconds(12)),
            ("250", Origin, Origin));

        var (own, onRunner) = await BothAsync(pair, services);

        Assert.Equal((TimeSpan.FromMinutes(3), 150L), (own.ExactLag, own.ExactVersionsBehind));
        Assert.Equal(own, onRunner);
    }

    [Fact]
    public async Task ChangeTrackingLag_Estimated_From_The_Checks_Equals_The_Supervisor_Paths_On_The_Same_Data()
    {
        var pair = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        // no commit times: the figure is the distance from the first check that reached the position to the latest one, by when the checks were made
        var services = Fill(pair, MsSqlDriverKinds.ChangeTracking, "100", null,
            ("90", Origin - TimeSpan.FromMinutes(10), null), ("101", Origin - TimeSpan.FromMinutes(6), null), ("130", Origin - TimeSpan.FromMinutes(2), null), ("160", Origin, null));

        var (own, onRunner) = await BothAsync(pair, services);

        Assert.Null(own.ExactLag);
        Assert.Equal(TimeSpan.FromMinutes(6), own.EstimatedLag);
        Assert.Equal(own, onRunner);
    }

    [Fact]
    public async Task A_Runner_Never_Read_Has_No_Figure_And_One_That_Cannot_Be_Read_Keeps_The_Last_Answer_And_Says_Why()
    {
        var pair = await SetUpAsync(MsSqlDriverKinds.ChangeTracking);
        var services = Fill(pair, MsSqlDriverKinds.ChangeTracking, "100", Origin - TimeSpan.FromMinutes(3), ("250", Origin, Origin));
        var config = factory.Services.GetRequiredService<ConfigRepository>();
        var task = config.LoadReplicationTask(pair.OnRunner);

        // not read yet: "no data yet", never a guess, and describing lag asked the runner nothing
        Assert.Null(services.OnRunner.Describe(task, "orders").ExactLag);
        Assert.Equal(0, services.Runner.Calls);

        Assert.True(await services.Inputs.RefreshAsync(pair.OnRunner, CancellationToken.None));
        var known = services.OnRunner.Describe(task, "orders");
        Assert.Equal(TimeSpan.FromMinutes(3), known.ExactLag);

        services.Runner.Down = true;
        Assert.False(await services.Inputs.RefreshAsync(pair.OnRunner, CancellationToken.None));
        Assert.Contains("could not be reached", services.Inputs.Problem(pair.OnRunner));
        Assert.Equal(known, services.OnRunner.Describe(task, "orders"));   // the last answer, not a number made up
        services.Runner.Down = false;
        Assert.True(await services.Inputs.RefreshAsync(pair.OnRunner, CancellationToken.None));
        Assert.Null(services.Inputs.Problem(pair.OnRunner));
    }
}
