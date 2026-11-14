using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DbDataSync.TestSupport;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Phase 124's <c>POST .../reconcile-deletes</c> through real HTTP against a real SQL Server: what the API refuses before it sends a sweep to the runner. The sweeps themselves run in the replication's drain on
/// the runner (plan 28c E22); what they delete, the guard's refusal and the override are the engine's, tested in <c>RunExecutorIntegrationTests.ReconcileDeletes_*</c> (and <c>Scd2NaturalKeyIntegrationTests</c>
/// for an SCD2 target), and the request's way into the drain in <c>DrainRequestsTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReconcileDeletesIntegrationTests : IClassFixture<TestApiFactory>, IAsyncLifetime
{
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
    private readonly string _databaseName = $"DbDataSyncReconcileTest_{Guid.NewGuid():N}";
    private readonly string _connectionName = $"rc-conn-{Guid.NewGuid():N}";
    private readonly string _replicationName;

    public ReconcileDeletesIntegrationTests(TestApiFactory factory)
    {
        _client = factory.CreateClient();
        _secrets = factory.Services.GetRequiredService<SecretStore>();
        _replicationName = $"rc-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await using (var bootstrap = new SqlConnection(ServerConnectionString))
        {
            await bootstrap.OpenAsync();
            await ExecuteAsync(bootstrap, $"CREATE DATABASE [{_databaseName}];");
        }

        await using var db = await OpenDatabaseAsync();
        await ExecuteAsync(db, "CREATE TABLE dbo.[Src] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(db, "CREATE TABLE dbo.[Tgt] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(db, "INSERT INTO dbo.[Src] (Id, Name) VALUES (1, 'One'), (2, 'Two'), (3, 'Three');");

        SetSecretEnvVar(_connectionName, TestSupport.TestEngines.MsSql.Password);
        await CreateConnectionAsync();
        await CreateReplicationAsync();
        await CreateMappingAsync();
    }

    public async Task DisposeAsync()
    {
        SetSecretEnvVar(_connectionName, null);

        await using var connection = new SqlConnection(ServerConnectionString);
        await connection.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(connection, _databaseName);
    }

    [Fact]
    public async Task ReconcileDeletes_ForAnUnknownMapping_Is404()
    {
        var response = await PostReconcileAsync(new FullSegment(), mappingName: "no-such-mapping");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReconcileDeletes_WithNoSegments_Is400()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/replications/{_replicationName}/mappings/map/reconcile-deletes",
            new { segments = Array.Empty<object>() },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static StringContent Empty() => new("", Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostReconcileAsync(BatchReloadSegment segment, string mappingName = "map")
    {
        var body = $$"""{ "segments": [{{SegmentSerializer.Serialize(segment)}}] }""";
        return _client.PostAsync(
            $"/api/replications/{_replicationName}/mappings/{mappingName}/reconcile-deletes",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static async Task<List<Guid>> ReadRunIdsAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return []; // 404/400 cases assert on the status code themselves, not on RunIds.

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

    private async Task<SqlConnection> OpenDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = _databaseName };
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<int> CountAsync()
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.[Tgt];";
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
            Enabled = false,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 3600 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "MsSqlMerge" },
            },
        }, JsonOptions)).EnsureSuccessStatusCode();

    private async Task CreateMappingAsync()
    {
        (await _client.PutAsJsonAsync($"/api/replications/{_replicationName}/table-mappings/map", new TableMappingConfig
        {
            Name = "map",
            Sources = [new SourceTableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Src" }],
            Targets = [new TableSpec { ConnectionName = _connectionName, Database = _databaseName, Schema = "dbo", Table = "Tgt" }],
            ColumnMappings =
            [
                new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" },
                new ColumnMapping { SourceColumn = "Name", TargetColumn = "Name" },
            ],
        }, JsonOptions)).EnsureSuccessStatusCode();

        // Populates the phase-90/91 column cache from the real catalog — without it, KeyReconcile
        // (like every phase-91 consumer) throws MetadataNotCachedException on its first run.
        (await _client.PostAsync($"/api/replications/{_replicationName}/table-mappings/map/refresh-metadata", null))
            .EnsureSuccessStatusCode();
    }

    private void SetSecretEnvVar(string connectionName, string? password) =>
        Environment.SetEnvironmentVariable(
            _secrets.EnvName(SecretRefs.ForConnection(connectionName)), password);
}
