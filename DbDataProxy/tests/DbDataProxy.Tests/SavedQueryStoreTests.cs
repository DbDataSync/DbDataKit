using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataProxy.Server.State;
using Npgsql;

namespace DbDataProxy.Tests;

/// <summary>User-owned saved queries in the Proxy's state database (plan 19, J27): saved with history, shared by user, group or everyone, and found by the model source for whoever may see them. On SQLite, and on PostgreSQL when the shared engines are up.</summary>
public sealed class SavedQueryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbproxy-saved-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SavedQueryDef Sql(string text, string? description = null) => new() { Sql = text, Description = description };

    private static ServingCaller Caller(string name, params string[] roles) => new(name, name, new HashSet<string>(roles), new Dictionary<string, string>());

    private static void Exercise(SavedQueryStore store)
    {
        var first = store.Save("alice", "sales_catalog", "revenue", Sql("SELECT 1", "first"), "alice");
        Assert.Equal(1, first.Version);
        var second = store.Save("alice", "sales_catalog", "revenue", Sql("SELECT 2", "second"), "alice", expectedVersion: 1);
        Assert.Equal(2, second.Version);
        Assert.Equal("second", store.Get("alice", "sales_catalog", "revenue")!.Query.Description);

        // a stale save and a second create are conflicts that say where the query is
        Assert.Equal(2, Assert.Throws<SavedQueryConflictException>(() => store.Save("alice", "sales_catalog", "revenue", Sql("SELECT 3"), "alice", expectedVersion: 1)).CurrentVersion);
        Assert.Equal(2, Assert.Throws<SavedQueryConflictException>(() => store.Save("alice", "sales_catalog", "revenue", Sql("SELECT 3"), "alice")).CurrentVersion);

        // history keeps every version; a restore is a new version, so an undo can be undone
        Assert.Equal(["first", "second"], store.History("alice", "sales_catalog", "revenue").Select(v => v.Query.Description));
        var restored = store.Restore("alice", "sales_catalog", "revenue", 1, "alice");
        Assert.Equal((3, "first"), (restored.Version, restored.Query.Description));
        Assert.Equal(3, store.History("alice", "sales_catalog", "revenue").Count);

        // who sees it: the owner; then whoever it is shared with (a user, a role, everyone) and no one else
        Assert.NotNull(store.Find(Caller("alice"), "sales_catalog", "revenue"));
        Assert.Null(store.Find(Caller("bob"), "sales_catalog", "revenue"));
        store.Share("alice", "sales_catalog", "revenue", "user:bob");
        Assert.NotNull(store.Find(Caller("bob"), "sales_catalog", "revenue"));
        Assert.Null(store.Find(Caller("carol", "sales"), "sales_catalog", "revenue"));
        store.Share("alice", "sales_catalog", "revenue", "group:Sales");
        Assert.NotNull(store.Find(Caller("carol", "sales"), "sales_catalog", "revenue"));
        Assert.Null(store.Find(Caller("dave"), "sales_catalog", "revenue"));
        store.Share("alice", "sales_catalog", "revenue", "*");
        Assert.NotNull(store.Find(Caller("dave"), "sales_catalog", "revenue"));
        Assert.Null(store.Find(Caller("dave"), "other_model", "revenue"));
        Assert.Equal(["*", "group:Sales", "user:bob"], store.Shares("alice", "sales_catalog", "revenue"));
        store.Unshare("alice", "sales_catalog", "revenue", "*");
        Assert.Null(store.Find(Caller("dave"), "sales_catalog", "revenue"));

        // a caller's own query of the same name is the one they call, and it is listed once
        store.Save("bob", "sales_catalog", "revenue", Sql("SELECT 'bob'", "bobs"), "bob");
        Assert.Equal("bobs", store.Find(Caller("bob"), "sales_catalog", "revenue")!.Description);
        Assert.Equal([("revenue", "bob")], store.List(Caller("bob"), "sales_catalog").Select(q => (q.Name, q.Owner)));

        // names and definitions that are not a saved query are refused
        Assert.Throws<ArgumentException>(() => store.Save("alice", "sales_catalog", "Bad Name", Sql("SELECT 1"), "alice"));
        Assert.Throws<ArgumentException>(() => store.Save("alice", "sales_catalog", "both", new SavedQueryDef { Sql = "SELECT 1", Request = new System.Text.Json.Nodes.JsonObject() }, "alice"));
        Assert.Throws<ArgumentException>(() => store.Share("alice", "sales_catalog", "revenue", "everyone"));

        Assert.True(store.Delete("alice", "sales_catalog", "revenue"));
        Assert.False(store.Delete("alice", "sales_catalog", "revenue"));
        Assert.Null(store.Find(Caller("carol", "sales"), "sales_catalog", "revenue"));
        Assert.Empty(store.History("alice", "sales_catalog", "revenue"));
    }

    [Fact]
    public void Saved_queries_are_kept_with_history_and_seen_only_by_whoever_they_are_shared_with() =>
        Exercise(new SavedQueryStore(ProxyState.Open(_dir)));

    [Fact]
    public void The_same_on_a_postgresql_database_the_operator_names()
    {
        var server = Environment.GetEnvironmentVariable("DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip("set DBDATA_TEST_POSTGRES to run on PostgreSQL");

        var name = $"proxy_saved_{Guid.NewGuid():N}";
        using (var admin = new NpgsqlConnection(server))
        {
            admin.Open();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name}";
            create.ExecuteNonQuery();
        }
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            Exercise(new SavedQueryStore(ProxyState.Open(_dir, new StateSettings("postgres", connection))));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(server);
            admin.Open();
            using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
    }
}
