using DbDataKit.Monitoring;
using DbDataKit.State;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests;

/// <summary>The gateway's state database (plan 13, P02): created on first start in the data directory, SQLite through DbDataKit.State.</summary>
public sealed class StateTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-state-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_started_gateway_has_created_its_state_database_in_the_data_directory() =>
        Assert.True(File.Exists(GatewayState.PathIn(gateway.DataDirectory)));

    [Fact]
    public void Opening_creates_the_file_and_the_directory_and_reopening_keeps_what_was_written()
    {
        var dir = Path.Combine(_dir, "not", "yet", "there");
        Assert.False(Directory.Exists(dir));

        var first = GatewayState.Open(dir);
        new NotificationFeedStore(first, GatewayState.ServiceId).Raise(NotificationKinds.CertificateExpiring, "the certificate ends soon");
        var second = GatewayState.Open(dir);

        Assert.True(File.Exists(GatewayState.PathIn(dir)));
        Assert.Equal(["the certificate ends soon"], new NotificationFeedStore(second, GatewayState.ServiceId).List().Select(n => n.Message));
        // every module the gateway is built from is recorded in the registry at its current version
        using var connection = second.OpenConnection();
        using var registry = connection.CreateCommand();
        registry.CommandText = $"SELECT module, version FROM {second.Layout!.Registry};";
        using var reader = registry.ExecuteReader();
        var recorded = new Dictionary<string, long>();
        while (reader.Read()) recorded[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1));
        Assert.Equal(GatewayState.Modules.ToDictionary(m => m.Name, m => (long)m.Version), recorded);
    }

    [Fact]
    public void A_state_database_from_before_the_modules_is_refused_naming_the_file_and_the_three_interleaving_constants_are_gone()
    {
        var path = GatewayState.PathIn(_dir);
        // what an earlier gateway wrote: one schema version for the whole database
        _ = new StateDatabase(path, new StateSchema([DbDataKit.State.NotificationSchema.Feed]));

        var refused = Assert.Throws<InvalidOperationException>(() => GatewayState.Open(_dir));

        Assert.Contains("state.db", refused.Message);
        Assert.Contains("one schema version", refused.Message);
        foreach (var gone in new[] { "HomeTemplatesBeforeGroup", "HomeTemplatesBeforeTrust", "HomeTemplatesBeforeSecrets" })
            Assert.Null(typeof(GatewayState).GetField(gone));
    }

    [Fact]
    public void A_postgresql_database_the_operator_names_keeps_the_feed_with_the_secret_resolved_when_it_is_opened()
    {
        var server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES");
        if (server is null) Assert.Skip("set DBDATAKIT_TEST_POSTGRES to run on PostgreSQL");

        var name = $"gateway_state_{Guid.NewGuid():N}";
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
            var opened = GatewayState.Open(_dir, settings, text => text.Replace("secret:state-db", password));

            new NotificationFeedStore(opened, GatewayState.ServiceId).Raise(NotificationKinds.CertificateExpiring, "kept on the server");
            Assert.Equal(["kept on the server"], new NotificationFeedStore(GatewayState.Open(_dir, settings, text => text.Replace("secret:state-db", password)), GatewayState.ServiceId).List().Select(n => n.Message));
            Assert.False(File.Exists(GatewayState.PathIn(_dir)), "a server engine keeps no SQLite file");
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
        var store = new NotificationFeedStore(GatewayState.Open(_dir), GatewayState.ServiceId);
        store.Raise(NotificationKinds.CertificateExpiring, "one");
        store.Raise(NotificationKinds.CertificateExpired, "two");
        var last = store.List()[^1].Id;

        store.MarkSeen("console-a", last);

        Assert.Equal(0, store.UnreadCount(store.GetCursor("console-a")));
        Assert.Equal(2, store.UnreadCount(store.GetCursor("console-b")));
    }
}
