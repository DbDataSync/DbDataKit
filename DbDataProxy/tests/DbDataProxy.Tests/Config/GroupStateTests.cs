using System.Net;
using System.Net.Sockets;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.State;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Config;

/// <summary>
/// Plan 20 O02: instances of a group share their state on SQL Server or PostgreSQL. Two instances running at once against one state serve each other's sessions (a pass-through session's database credentials with them,
/// encrypted with the group's key) and write one audit; an instance given another key refuses to start, naming the secret. A second instance against a SQLite state is refused.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GroupStateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbproxy-group-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<WebApplication> _apps = [];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            await app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
        TestFolder.Delete(_root);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>An instance in a data directory of its own (or a shared one), on the state the arguments name.</summary>
    private WebApplication Instance(string dataDirectory, params string[] state)
    {
        Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(dataDirectory));
        File.WriteAllText(Path.Combine(ProxyConfigFile.DirectoryIn(dataDirectory), "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        var app = ProxyHost.Build([$"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Listen:Address=127.0.0.1", $"--Proxy:Listen:Port={FreePort()}", .. state]);
        _apps.Add(app);
        return app;
    }

    [Fact]
    public async Task A_second_instance_against_a_sqlite_state_is_refused_and_starts_once_the_first_has_stopped()
    {
        var shared = Path.Combine(_root, "one");
        var first = Instance(shared);
        await first.StartAsync();

        var second = Instance(shared);
        var refused = await Assert.ThrowsAsync<SecondInstanceException>(() => second.StartAsync());
        Assert.Contains("SQLite", refused.Message);
        Assert.Contains("SQL Server or PostgreSQL", refused.Message);

        await first.StopAsync();
        var third = Instance(shared);
        await third.StartAsync();   // the first one's row went when it stopped
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task Two_instances_on_a_server_state_share_sessions_and_audit_and_one_with_another_key_refuses_to_start(string engine, string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable) ?? Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATA_TEST_SQLSERVER" : "DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set {variable} to run on {engine}");

        var name = $"dbproxy_group_{Guid.NewGuid():N}";
        void Admin(string sql)
        {
            using System.Data.Common.DbConnection admin = engine == "mssql" ? new SqlConnection(server) : new Npgsql.NpgsqlConnection(server);
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Admin(engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        try
        {
            var connection = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new Npgsql.NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            string[] state = [$"--Proxy:State:Engine={engine}", $"--Proxy:State:ConnectionString={connection}"];

            // the first instance makes the group's key; the second is given a copy of it, as an operator would
            var (dirA, dirB) = (Path.Combine(_root, "a"), Path.Combine(_root, "b"));
            var a = Instance(dirA, state);
            await a.StartAsync();
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirA });
            using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + GroupSessionKey.Secret)))
                await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirB }).SetAsync(SecretName.Parse(GroupSessionKey.Secret), key!);
            var b = Instance(dirB, state);
            await b.StartAsync();   // a second instance on a server engine is the point of one

            // a pass-through session made on one instance is served by the other, credentials and all
            var sessionsA = a.Services.GetRequiredService<DbDataProxy.Server.Auth.SessionStore>();
            var sessionsB = b.Services.GetRequiredService<DbDataProxy.Server.Auth.SessionStore>();
            var token = sessionsA.Issue(new ProxyPrincipal("ada", "passthrough", ["erp"], "erp", new DbCredentials("ada_db", "s3cret-pass")));
            var onB = sessionsB.TryGet(token);
            Assert.NotNull(onB);
            Assert.Equal(("ada", "passthrough", "erp"), (onB.Name, onB.Method, onB.DefaultDataSource));
            Assert.Equal(new DbCredentials("ada_db", "s3cret-pass"), onB.Credentials);
            Assert.Contains(sessionsB.List(), s => s.User == "ada");

            // the database never holds the password in the clear, nor the token
            var database = a.Services.GetRequiredService<StateDatabase>();
            using (var conn = database.OpenConnection())
            using (var cmd = database.Command(conn, "SELECT TokenHash, Principal FROM ClientSessions;"))
            using (var reader = cmd.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.DoesNotContain("s3cret-pass", reader.GetString(1), StringComparison.Ordinal);
                Assert.NotEqual(token, reader.GetString(0));
            }

            // ending it on one ends it on both
            sessionsB.Revoke(token);
            Assert.Null(sessionsA.TryGet(token));

            // one audit: what one instance records, the other reads
            var at = DateTimeOffset.UtcNow;
            new StateAuditLog(database).Append(new AuditEntry(at, "console-1", "Console", "grace", "Grace Hopper", true, "data-source.write", "erp", "ok", "t1"));
            Assert.Contains(new StateAuditLog(b.Services.GetRequiredService<StateDatabase>()).Read(), e => e.Command == "data-source.write" && e.UserId == "grace");

            // a third instance with a key of its own (none copied) is refused, naming the secret, and keeps no key it made
            var c = Instance(Path.Combine(_root, "c"), state);
            var refused = await Assert.ThrowsAnyAsync<SecretConfigurationException>(() => c.StartAsync());
            Assert.Contains(GroupSessionKey.Secret, refused.Message);
            using (var left = await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = Path.Combine(_root, "c") }).TryResolveAsync(SecretRef.Parse("secret:" + GroupSessionKey.Secret)))
                Assert.Null(left);
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Admin(engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        }
    }

    [Fact]
    public void The_audit_file_an_instance_wrote_before_is_read_into_the_state_once()
    {
        var directory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, StateAuditLog.LegacyFileName);
        File.WriteAllLines(file, [
            """{"at":"2026-10-01T10:00:00+00:00","consoleId":"c1","consoleName":"Console","userId":"ada","userName":"Ada","byConsole":true,"command":"data-source.write","target":"erp","outcome":"ok","tokenId":"t1"}""",
            """{"at":"2026-10-02T10:00:00+00:00","consoleId":"c1","consoleName":"Console","userId":"ada","userName":"Ada","byConsole":true,"command":"secret.set","target":"x","outcome":"ok","tokenId":"t2"}""",
        ]);
        var log = new StateAuditLog(ProxyState.Open(directory));

        Assert.Equal(2, log.ImportFile(directory));
        Assert.Equal(["data-source.write", "secret.set"], log.Read().Select(e => e.Command));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".imported"));
        Assert.Equal(0, log.ImportFile(directory));   // once
        Assert.Equal(2, log.Read().Count);
    }
}
