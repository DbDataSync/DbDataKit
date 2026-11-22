using System.Net;
using System.Net.Sockets;
using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataKit.State.Engines.Housekeeping;
using DbDataKit.State.Engines.Snapshots;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 35 I1: the Gateway keeps its own state in its own process: retention and a snapshot on the interval of the <c>housekeeping:</c> part of its <c>state:</c> section in <c>server.yaml</c>, each run recorded in its state.</summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GatewayHousekeepingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbgateway-housekeeping-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Action> _drops = [];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var drop in _drops) drop();
        TestFolder.Delete(_root);
        return ValueTask.CompletedTask;
    }

    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task The_gateway_runs_retention_and_a_snapshot_of_its_state_on_its_interval_and_records_each_run(string engine)
    {
        string[] state = engine == "sqlite" ? [] : Scratch(engine);
        Directory.CreateDirectory(GatewayConfigFile.DirectoryIn(_root));
        File.WriteAllText(Path.Combine(GatewayConfigFile.DirectoryIn(_root), "server.yaml"),
            "schemaVersion: 2\ntls: { mode: selfSigned }\nstate:\n  housekeeping:\n    retention_every_minutes: 0.02\n    backup_directory: backups\n    backup_every_hours: 0.0003\n    backup_keep: 2\n");
        var app = GatewayHost.Build([$"--Gateway:DataDirectory={_root}", $"--Gateway:Listen=https://127.0.0.1:{FreePort()}", "--Gateway:Management:Enabled=false", .. state]);
        await app.StartAsync();
        try
        {
            var runs = new RunStore(app.Services.GetRequiredService<StateDatabase>(), GatewayState.ServiceId);
            int Succeeded(string job) => runs.Read(new RunQuery(GatewayState.ServiceId, job, RunOutcome.Succeeded)).Count;
            var until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until && (Succeeded(StateHousekeeping.RetentionJobId) < 2 || Succeeded(StateHousekeeping.BackupJobId) < 2)) await Task.Delay(200);

            Assert.True(Succeeded(StateHousekeeping.RetentionJobId) >= 2, "retention ran twice and was recorded");
            Assert.True(Succeeded(StateHousekeeping.BackupJobId) >= 2, "the snapshot ran twice and was recorded");
            var kept = StateBackupJob.List(Path.Combine(_root, "backups"));
            Assert.InRange(kept.Count, 1, 2);
            Assert.All(kept, k => Assert.True(File.Exists(Path.Combine(k, "manifest.json"))));
        }
        finally
        {
            try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void A_housekeeping_setting_that_is_not_one_is_refused_naming_it()
    {
        var options = new GatewayStateOptions { Housekeeping = { ["backup_kept"] = "3" } };
        var refused = Assert.Throws<InvalidOperationException>(() => options.ToSettings());
        Assert.Contains("state.housekeeping.backup_kept", refused.Message, StringComparison.Ordinal);
    }

    private string[] Scratch(string engine)
    {
        var server = Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set DBDATAKIT_TEST_{(engine == "mssql" ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        var name = $"dbgateway_hk_{Guid.NewGuid():N}";
        void Admin(string sql)
        {
            using System.Data.Common.DbConnection admin = engine == "mssql" ? new SqlConnection(server) : new Npgsql.NpgsqlConnection(server);
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Admin(engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        _drops.Add(() =>
        {
            SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Admin(engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        });
        var connection = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new Npgsql.NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        return [$"--Gateway:State:Engine={engine}", $"--Gateway:State:Connection={connection}"];
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
