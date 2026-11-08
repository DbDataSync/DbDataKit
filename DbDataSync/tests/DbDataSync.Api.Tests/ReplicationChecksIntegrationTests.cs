using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Monitoring;
using DbDataKit.Quality;
using DbDataKit.State;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using DbDataSync.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 23a FQ10 on a real SQL Server: the checks stored with a replication (<c>checks:</c> in its <c>task.yaml</c>, FQ04) are evaluated over its target tables when a run of it ends and on a clock, through the shared evaluator, and
/// the results are in the <c>quality</c> module of Sync's state database. Freshness is measured the way Monitoring's lag finding is (the replication's lag reading, which the polling gate and the passes cache in the state database),
/// so the check and the finding agree. The lag is driven the way <see cref="ReaderLagTests"/> drives it (the position and its time the pass stored, the source's last known one); no runner is spawned, so what is shown
/// is the evaluation and its wiring (the hosted service's tick, the state module, the identities), not the runner's work.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReplicationChecksIntegrationTests(TestApiFactory factory) : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static string ServerConnectionString => Environment.GetEnvironmentVariable("DBDATASYNC_TEST_MSSQL_SERVER") ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private string Database => $"DbDataSyncChecks_{_suffix}";
    private string Connection => $"chk-{_suffix}";
    private string Replication => $"chk-repl-{_suffix}";
    private string SourceDatabase => $"Src_{_suffix}";
    private ReplicationTaskConfig _task = null!;

    private static string Lsn(params byte[] bytes) => MsSqlCdcCatalog.ToWatermark(bytes);

    public async Task InitializeAsync()
    {
        await using (var server = new SqlConnection(ServerConnectionString))
        {
            await server.OpenAsync();
            await Exec(server, $"CREATE DATABASE [{Database}];");
        }

        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = Database };
        await using (var db = new SqlConnection(builder.ConnectionString))
        {
            await db.OpenAsync();
            await Exec(db, "CREATE TABLE dbo.orders (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL, Amount DECIMAL(10,2) NOT NULL);");
            await Exec(db, "INSERT INTO dbo.orders VALUES (1, N'Alice', 10), (2, NULL, 20), (3, N'Carol', 30);");
        }

        (await _client.PutAsJsonAsync($"/api/connections/{Connection}", new ConnectionInput
        {
            Name = Connection, DriverType = DriverIds.MsSql, Host = "localhost", Port = TestEngines.MsSql.Port, Database = Database,
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" }, AuthMode = AuthMode.SqlAuth, UserId = "sa", Password = TestEngines.MsSql.Password,
        }, JsonOptions)).EnsureSuccessStatusCode();

        // disabled: the scheduler leaves it alone, so nothing but this test writes its runs and its lag
        (await _client.PutAsJsonAsync($"/api/replications/{Replication}", new ReplicationTaskConfig
        {
            Name = Replication, Enabled = false,
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = Connection, Database = SourceDatabase }, Target = new EndpointRef { ConnectionName = Connection, Database = Database } },
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = MsSqlDriverKinds.Cdc }, Cache = new CacheConfig { Kind = MsSqlDriverKinds.StagingTable }, Writer = new WriterConfig { Kind = MsSqlDriverKinds.Merge },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync($"/api/replications/{Replication}/table-mappings/orders", new TableMappingConfig
        {
            Name = "orders", Sources = [new SourceTableSpec { Schema = "dbo", Table = "orders" }], Targets = [new TableSpec { Schema = "dbo", Table = "orders" }], PrimaryKey = ["Id"],
            ColumnMappings = [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }, new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" }, new ColumnMapping { SourceColumn = "Amount", TargetColumn = "Amount" }],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // the section of FQ04, written as a person writes it
        var file = Path.Combine(factory.RepoRoot, "config", "replications", Replication, "task.yaml");
        File.AppendAllText(file, "\nchecks:\n"
            + "  - {id: orders_fresh, kind: freshness, dataset: dbo.orders, max_age: 10m, severity: block}\n"
            + "  - {id: orders_name_present, kind: not_null, dataset: dbo.orders, column: Name}\n"
            + "  - {id: orders_id_unique, kind: unique, dataset: dbo.orders, column: Id}\n");
        _task = factory.Services.GetRequiredService<ConfigRepository>().LoadReplicationTask(Replication);
    }

    public async Task DisposeAsync()
    {
        await using var server = new SqlConnection(ServerConnectionString);
        await server.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(server, Database);
    }

    private static async Task Exec(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private ReplicationQuality Quality() => factory.Services.GetRequiredService<ReplicationQuality>();

    private QualityStore Store() => new(factory.Services.GetRequiredService<StateDatabase>());

    private string Identity()
    {
        var connection = factory.Services.GetRequiredService<ConfigRepository>().LoadConnection(Connection);
        return DbDataKit.Lineage.LineageIdentity.Dataset(DbDataSync.Core.Lineage.SyncLineage.DefinitionOf(connection, Database), "dbo", "orders").ToString();
    }

    /// <summary>The mapping's applied position and the time it was mapped to, and the source's last known one (what a pass and the gate cache): the replication is <paramref name="behind"/> behind.</summary>
    private void SetLag(TimeSpan behind)
    {
        var now = DateTimeOffset.UtcNow;
        var source = EndpointResolution.ResolveSource(_task, new SourceTableSpec { Schema = "dbo", Table = "orders" });
        factory.Services.GetRequiredService<ChangeWatermarkStore>().SetWatermark(Replication, "orders", WatermarkKey.Build(source, MsSqlDialect.Instance), Lsn(0, 0, 0, 42, 0, 0, 0, 100, 0, 1), now - behind);
        var db = factory.Services.GetRequiredService<StateDatabase>();
        db.Retry(() =>
        {
            using var connection = db.OpenConnection();
            using var cmd = db.Command(connection, """
                INSERT INTO {table:sync_state.change_check} (connection_name, source_database, source_kind, value_text, checked_at, source_at)
                VALUES ($connection, $database, $kind, $value, $checkedAt, $sourceTime);
                """);
            cmd.Bind(db, "connection", Connection);
            cmd.Bind(db, "database", SourceDatabase);
            cmd.Bind(db, "kind", MsSqlDriverKinds.Cdc);
            cmd.Bind(db, "value", Lsn(0, 0, 0, 42, 0, 0, 0, 200, 0, 1), System.Data.DbType.String);
            cmd.Bind(db, "checkedAt", now);
            cmd.Bind(db, "sourceTime", now, null);
            cmd.ExecuteNonQuery();
        });
    }

    private JobHealth Health() => new HealthEvaluator().Evaluate(new JobExpectation(Replication, MaxLag: TimeSpan.FromMinutes(10)), null, ReplicationLagReading.For(factory.Services.GetRequiredService<ReaderLagService>(), factory.Services.GetRequiredService<ConfigRepository>(), Replication));

    [Fact]
    public async Task A_replication_that_falls_behind_fails_its_freshness_check_and_passes_after_it_catches_up_and_the_lag_finding_agrees()
    {
        var identity = Identity();
        var store = Store();

        // behind: half an hour against ten minutes allowed
        SetLag(TimeSpan.FromMinutes(30));
        var behind = await Quality().EvaluateAsync(Replication, runId: null);
        var fresh = behind.Report.Checks.Single(c => c.Check.Id == "orders_fresh");
        Assert.Equal(EvaluationStatus.Fail, fresh.Evaluation.Status);
        Assert.InRange(fresh.Evaluation.Measured!.Value, 1799m, 1805m);                 // seconds: the lag itself
        Assert.Equal(600m, fresh.Evaluation.Threshold);
        Assert.True(fresh.Blocking);                                                    // severity block
        Assert.Contains(Health().Findings, f => f.Rule == HealthRule.Lagging);          // Monitoring says the same
        Assert.Equal(["orders_fresh"], store.BlocksOf(identity).Select(b => b.CheckId));

        // the other two checks are about the rows of the target table and are the same in both
        var byId = behind.Report.Checks.ToDictionary(c => c.Check.Id);
        Assert.Equal((EvaluationStatus.Fail, 1m), (byId["orders_name_present"].Evaluation.Status, byId["orders_name_present"].Evaluation.Measured!.Value));
        Assert.Equal(["Id=2"], byId["orders_name_present"].Evaluation.SampleKeys);          // the target's key (the mapping's primary key): the row that failed, never its other columns
        Assert.Equal(EvaluationStatus.Pass, byId["orders_id_unique"].Evaluation.Status);

        // a successful run, and the replication caught up: the same check passes, the block clears, the finding is gone
        SetLag(TimeSpan.Zero);
        var queue = factory.Services.GetRequiredService<WorkQueueStore>();
        var runId = queue.Enqueue(Replication, RunKind.Primary, "orders");
        factory.Services.GetRequiredService<TaskRunStore>().CompleteRun(runId, RunStatus.Succeeded, 3, 3, null);
        var service = new ReplicationChecksService(Quality(), factory.Services.GetRequiredService<TaskRunStore>(), NullLogger<ReplicationChecksService>.Instance);
        await service.TickAsync(CancellationToken.None);                                  // the run ended: its after-run evaluation, under its id

        Assert.DoesNotContain(Health().Findings, f => f.Rule == HealthRule.Lagging);
        var status = store.Status(identity, "orders_fresh")!;
        Assert.Equal(CheckOutcome.Pass, status.Outcome);
        Assert.Empty(store.BlocksOf(identity));
        var history = store.History(identity, "orders_fresh");
        Assert.Equal([CheckOutcome.Pass, CheckOutcome.Fail], history.Select(h => h.Outcome));
        Assert.Equal(runId.ToString(), history[0].RunId);                                  // the run that set it off
        Assert.Null(history[1].RunId);                                                     // the evaluation on demand
    }

    [Fact]
    public async Task A_tick_with_no_run_evaluates_on_the_clock_from_the_lag_reading_and_a_quiet_caught_up_replication_stays_fresh()
    {
        var identity = Identity();
        SetLag(TimeSpan.Zero);
        var service = new ReplicationChecksService(Quality(), factory.Services.GetRequiredService<TaskRunStore>(), NullLogger<ReplicationChecksService>.Instance) { IdleInterval = TimeSpan.Zero };

        await service.TickAsync(CancellationToken.None);
        Assert.Equal(CheckOutcome.Pass, Store().Status(identity, "orders_fresh")!.Outcome);

        // the source has not moved for an hour and nothing ran: the check is measured against the source's position, not the clock
        var before = Store().History(identity, "orders_fresh").Count;     // the host's own service may have ticked meanwhile
        await service.TickAsync(CancellationToken.None);
        Assert.Equal(before + 1, Store().History(identity, "orders_fresh").Count);
        Assert.All(Store().History(identity, "orders_fresh"), h => Assert.Equal(CheckOutcome.Pass, h.Outcome));
    }

    [Fact]
    public async Task The_source_is_one_dataset_for_the_target_table_the_checks_name_under_its_lineage_identity()
    {
        var notes = new List<string>();
        var source = Quality().SourceFor(Replication, notes);
        var dataset = Assert.Single(await source.DatasetsAsync());
        Assert.Equal(Identity(), dataset.Identity);
        Assert.Equal(3, dataset.Checks.Count);
        Assert.Empty(notes);
        Assert.Contains(Replication, Quality().ReplicationsWithChecks());
    }
}
