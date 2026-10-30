using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataProxy.Server.State;
using DbDataProxy.Tests.Support;

namespace DbDataProxy.Tests;

/// <summary>The proxy's state database (plan 13, P02): created on first start in the data directory, SQLite through DbDataKit.State.</summary>
public sealed class StateTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbproxy-state-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_started_proxy_has_created_its_state_database_in_the_data_directory() =>
        Assert.True(File.Exists(ProxyState.PathIn(proxy.DataDirectory)));

    [Fact]
    public void Opening_creates_the_file_and_the_directory_and_reopening_keeps_what_was_written()
    {
        var dir = Path.Combine(_dir, "not", "yet", "there");
        Assert.False(Directory.Exists(dir));

        var first = ProxyState.Open(dir);
        new NotificationStore(first).Raise(NotificationKinds.CertificateExpiring, "the certificate ends soon");
        var second = ProxyState.Open(dir);

        Assert.True(File.Exists(ProxyState.PathIn(dir)));
        Assert.Equal(["the certificate ends soon"], new NotificationStore(second).List().Select(n => n.Message));
        using var connection = second.OpenConnection();
        Assert.Equal(ProxyState.Schema.Count, second.Dialect.GetSchemaVersion(connection));
    }

    [Fact]
    public void A_postgresql_database_the_operator_names_keeps_the_feed_with_the_secret_resolved_when_it_is_opened()
    {
        var server = Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip("set DBDATA_TEST_POSTGRES to run on PostgreSQL");

        var name = $"proxy_state_{Guid.NewGuid():N}";
        using (var admin = new Npgsql.NpgsqlConnection(server))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name}";
            create.ExecuteNonQuery();
        }
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(server) { Database = name };
            var password = builder.Password;
            builder.Password = null;
            var settings = new StateSettings("postgres", builder.ConnectionString + ";Password=secret:state-db");
            var opened = ProxyState.Open(_dir, settings, text => text.Replace("secret:state-db", password));

            new NotificationStore(opened).Raise(NotificationKinds.CertificateExpiring, "kept on the server");
            Assert.Equal(["kept on the server"], new NotificationStore(ProxyState.Open(_dir, settings, text => text.Replace("secret:state-db", password))).List().Select(n => n.Message));
            Assert.False(File.Exists(ProxyState.PathIn(_dir)), "a server engine keeps no SQLite file");
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            using var admin = new Npgsql.NpgsqlConnection(server);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Each_console_reads_the_feed_on_its_own()
    {
        var store = new NotificationStore(ProxyState.Open(_dir));
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpired, "two");
        var last = store.List()[^1].Id;

        store.MarkSeen("console-a", last);

        Assert.Equal(0, store.UnreadCount(store.GetCursor("console-a")));
        Assert.Equal(2, store.UnreadCount(store.GetCursor("console-b")));
    }
}
