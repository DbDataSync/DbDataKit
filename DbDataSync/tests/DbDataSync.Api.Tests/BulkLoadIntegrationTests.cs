using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Controllers;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DbDataSync.TestSupport;

namespace DbDataSync.Api.Tests;

/// <summary>
/// The bulk load trigger endpoint driven end to end through real HTTP, a real spawned
/// DbDataSync.TaskRunner worker, and a real SQL Server — proving the properties the whole per-mapping
/// run model was built for, rather than asserting them against the stores directly (which is what
/// WorkQueueStoreTests already does, and which can't observe two processes racing).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BulkLoadIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private const int RowsPerTable = 9;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("DBDATAKIT_TEST_MSSQL")
        ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";

    private readonly HttpClient _client;
    private readonly SecretStore _secrets;
    private readonly WorkQueueStore _workQueueStore;
    private readonly TaskRunStore _taskRunStore;
    private readonly string _databaseName = $"DbDataSyncBulkLoadTest_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"bf-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName;

    public BulkLoadIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        _workQueueStore = factory.Services.GetRequiredService<WorkQueueStore>();
        _taskRunStore = factory.Services.GetRequiredService<TaskRunStore>();
        _replicationName = $"bf-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
            await ExecuteAsync(bootstrap,
                $"ALTER DATABASE [{_databaseName}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
        }

        await using var db = await OpenDatabaseAsync();
        foreach (var i in new[] { 1, 2 })
        {
            await ExecuteAsync(db, $"CREATE TABLE dbo.[Src_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
            await ExecuteAsync(db, $"ALTER TABLE dbo.[Src_{i}] ENABLE CHANGE_TRACKING;");
            await ExecuteAsync(db, $"CREATE TABLE dbo.[Tgt_{i}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");

            var values = string.Join(", ", Enumerable.Range(1, RowsPerTable).Select(r => $"({r}, 'Row{i}_{r}')"));
            await ExecuteAsync(db, $"INSERT INTO dbo.[Src_{i}] (Id, Name) VALUES {values};");
        }

        SetSecretEnvVar(_connectionName, TestSupport.TestEngines.MsSql.Password);
        await CreateConnectionAsync();
        await CreateReplicationAsync();
        await CreateMappingAsync(1);
        await CreateMappingAsync(2);
    }

    public async Task DisposeAsync()
    {
        SetSecretEnvVar(_connectionName, null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(connection, _databaseName);
    }

    private async Task SetUpSegmentedMappingAsync()
    {
        await using var db = await OpenDatabaseAsync();
        await ExecuteAsync(db, "CREATE TABLE dbo.[Src_3] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(db, "ALTER TABLE dbo.[Src_3] ENABLE CHANGE_TRACKING;");
        await ExecuteAsync(db, "CREATE TABLE dbo.[Tgt_3] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        var values = string.Join(", ", Enumerable.Range(1, RowsPerTable).Select(r => $"({r}, 'Row3_{r}')"));
        await ExecuteAsync(db, $"INSERT INTO dbo.[Src_3] (Id, Name) VALUES {values};");

        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map-3", new TableMappingConfig
        {
            Name = "map-3",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Src_3" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Tgt_3" }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
            // Empty means Full otherwise — this is the one thing that distinguishes this mapping from
            // CreateMappingAsync's own map-1/map-2: an initial load requests one segment per entry here.
            DefaultSegmenting = [new RangeSegment("Id", "1", "5"), new RangeSegment("Id", "5", "10")],
        }, JsonOptions)).EnsureSuccessStatusCode();

        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/map-3/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task BulkLoad_ForAnUnknownMapping_Is404()
    {
        var response = await PostBulkLoadAsync("no-such-mapping", new FullSegment());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Rejected up front rather than queued: a segment naming a column that doesn't exist
    /// can only ever fail, and the caller is right there to be told why.</summary>
    [Fact]
    public async Task BulkLoad_WithASegmentColumnThatDoesNotExist_Is400()
    {
        var response = await PostBulkLoadAsync("map-1", new AutoSegment("NoSuchColumn", 2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("NoSuchColumn", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BulkLoad_WithNoSegments_Is400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{_replicationName}/mappings/map-1/bulk-load",
            new { readerKind = "MsSqlBatchReload", segments = Array.Empty<object>() },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private Task<HttpResponseMessage> PostBulkLoadAsync(string mappingName, BatchReloadSegment segment)
    {
        // Serialized through SegmentSerializer so the polymorphic "mode" discriminator on the wire is
        // built exactly the way every other producer of segment JSON builds it.
        var body = $$"""
            {
              "readerKind": "{{MsSqlKinds.BatchReload}}",
              "cacheKind": "{{MsSqlKinds.StagingTable}}",
              "writerKind": "{{MsSqlKinds.MergeReconcile}}",
              "segments": [{{SegmentSerializer.Serialize(segment)}}]
            }
            """;
        return _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/{mappingName}/bulk-load",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    /// <summary>The MSSQL driver's Kind strings, repeated here rather than referenced: these tests
    /// exercise the HTTP contract, where a Kind is just a string a caller got from the capabilities
    /// endpoint — taking a project reference on the driver would test something weaker than that.</summary>
    private static class MsSqlKinds
    {
        public const string BatchReload = "MsSqlBatchReload";
        public const string StagingTable = "MsSqlStagingTable";
        public const string MergeReconcile = "MsSqlMergeReconcile";
    }

    private static async Task<List<Guid>> ReadRunIdsAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new Exception($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
    }

    private static void AssertAllSucceeded(IReadOnlyList<JsonElement> runs)
    {
        var failures = runs
            .Where(r => r.GetProperty("status").GetString() != "Succeeded")
            .Select(r => $"{r.GetProperty("runId").GetString()}: {r.GetProperty("status").GetString()} — {r.GetProperty("errorSummary").GetString()}")
            .ToList();
        Assert.True(failures.Count == 0, "Expected every run to succeed, but got:\n" + string.Join("\n", failures));
    }

    private async Task<JsonElement> GetLatestBulkLoadAsync()
    {
        var batches = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/replications/{_replicationName}/bulk-loads", JsonOptions);
        var list = batches.EnumerateArray().ToList();
        Assert.NotEmpty(list);
        return list[0];
    }

    private async Task<JsonElement> PollUntilTerminalAsync(Guid runId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync($"/api/runs/{runId}");
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                var run = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (run.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled")
                    return run;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Run {runId} did not reach a terminal status within 90s.");
    }

    /// <summary>True the moment a mapping's own read-state reports the given hold, false if it never
    /// does within <paramref name="timeout"/> — never throws, since "it finished before we could catch
    /// it" is an expected, non-erroneous outcome for a caller racing a real background load.</summary>
    private async Task<bool> PollUntilHoldAsync(string mappingName, ReadHold hold, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _client.GetAsync(
                $"/api/replications/{_replicationName}/table-mappings/{mappingName}/read-state");
            response.EnsureSuccessStatusCode();
            var state = await response.Content.ReadFromJsonAsync<MappingReadStateDto>(JsonOptions);
            if (state!.Hold == hold)
                return true;
            await Task.Delay(50);
        }

        return false;
    }

    private async Task<SqlConnection> OpenDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<int> CountAsync(string table)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}];";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateConnectionAsync() =>
        (await _client.PutAsJsonAsync($"/api/connections/{_connectionName}", new ConnectionInput
        {
            Name = _connectionName,
            DriverType = DriverIds.MsSql,
            Host = "localhost",
            Port = TestSupport.TestEngines.MsSql.Port,
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" },
            Database = _databaseName,
            AuthMode = AuthMode.SqlAuth,
            UserId = "sa",
            Password = TestSupport.TestEngines.MsSql.Password,
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateReplicationAsync() =>
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}", new ReplicationTaskConfig
        {
            Name = _replicationName,
            // Deliberately configured for *incremental* sync: a bulk load of this replication must
            // override the pipeline entirely, which is the whole point of the per-item Kinds.
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = MsSqlKinds.StagingTable },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateMappingAsync(int index)
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map-{index}", new TableMappingConfig
        {
            Name = $"map-{index}",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = $"Src_{index}" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = $"Tgt_{index}" }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // The PUT saves the mapping without introspecting — phase 90 captures the cache from the
        // columns a client sends, and a hand-built TableMappingConfig sends none, leaving phase 91's
        // readers and writers nothing to run from. This is the Refresh metadata endpoint, which reads
        // both catalogs server-side.
        (await _client.PostAsync(
            $"/api/replications/{_replicationName}/table-mappings/map-{index}/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    private void SetSecretEnvVar(string connectionName, string? password) =>
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(connectionName)), password);
}
