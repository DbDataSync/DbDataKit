using DbDataGateway.Server.Serving;
using DbDataKit.State;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 30 PB05 on the management API: the prune call of every serving kind.</summary>
public sealed class DefinitionsManagementTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-defs-mgmt-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public void A_prune_is_a_dry_run_unless_it_says_otherwise_and_keeps_what_a_channel_serves()
    {
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, DbDataKit.Web.Trust.TrustModule.Module], new StateScope(Group: "main")));
        var store = ServingKinds.StoreOf(database);
        var navigation = (string n) => "{\"title\":\"" + n + "\",\"entries\":[]}";
        for (var i = 1; i <= 5; i++) store.Publish("app-nav", "menu", navigation("v" + i), "ada");
        var api = new DbDataGateway.Server.Management.GatewayDefinitionsManagement(database);
        var caller = DbDataGateway.Server.Management.ManagementCaller.Local("root");

        var dry = api.Handle("POST", "/api/v1/definitions/app-nav/menu/prune", "{\"keep\": 1}", caller);
        Assert.Equal(200, dry.Status);
        Assert.True(dry.Body["dry_run"]!.GetValue<bool>());
        Assert.Equal(5, store.Versions("app-nav", "menu").Count);
        Assert.Equal([1, 2, 3], dry.Body["remove"]!.AsArray().Select(n => n!.GetValue<int>()));

        var real = api.Handle("POST", "/api/v1/definitions/app-nav/menu/prune", "{\"keep\": 1, \"dry_run\": false}", caller);
        Assert.False(real.Body["dry_run"]!.GetValue<bool>());
        Assert.Equal([4, 5], store.Versions("app-nav", "menu").Select(v => v.Version));

        Assert.Equal(400, api.Handle("POST", "/api/v1/definitions/app-nav/menu/prune", "{}", caller).Status);
        Assert.Equal(404, api.Handle("POST", "/api/v1/definitions/nonsense/menu/prune", "{\"keep\":1}", caller).Status);
        Assert.Equal(404, api.Handle("POST", "/api/v1/definitions/app-nav/none/prune", "{\"keep\":1}", caller).Status);
    }
}

public sealed class SharedPublishOnTheGatewayTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-shared-publish-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public void A_navigation_set_is_published_staged_read_and_rolled_back_through_the_shared_routes()
    {
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, DbDataKit.Web.Trust.TrustModule.Module], new StateScope(Group: "main")));
        var api = new DbDataGateway.Server.Management.GatewayDefinitionsManagement(database);
        var caller = DbDataGateway.Server.Management.ManagementCaller.Local("root");
        string Menu(string t) => System.Text.Json.JsonSerializer.Serialize(new { manifest = "{\"title\":\"" + t + "\",\"entries\":[]}" });
        // signed versions are required by default (PB03); a test group does not need them
        var unsigned = api.Handle("PUT", "/api/v1/definitions/app-nav/menu", Menu("one"), caller);
        Assert.Equal(403, unsigned.Status);
        Assert.Equal(200, api.Handle("PUT", "/api/v1/settings/group", "{\"mode\":\"test\"}", caller).Status);
        Assert.Equal(200, api.Handle("PUT", "/api/v1/definitions/app-nav/menu", Menu("one"), caller).Status);
        Assert.Equal(200, api.Handle("PUT", "/api/v1/definitions/app-nav/menu", Menu("two"), caller).Status);
        // a body the kind does not accept is refused naming the kind
        var bad = api.Handle("PUT", "/api/v1/definitions/app-nav/menu", System.Text.Json.JsonSerializer.Serialize(new { manifest = "[]" }), caller);
        Assert.Equal(400, bad.Status);
        Assert.Contains("app-nav", (string)bad.Body["message"]!);
        Assert.Equal(200, api.Handle("POST", "/api/v1/definitions/app-nav/menu/rollback", "{}", caller).Status);
        Assert.Equal(1, (int)api.Handle("GET", "/api/v1/definitions/app-nav/menu", null, caller).Body["channels"]!.AsArray()[0]!["active"]!);
        Assert.True(DbDataGateway.Server.Management.GatewayDefinitionsManagement.Handles("/api/v1/bindings/prod/app/x"));
        Assert.False(DbDataGateway.Server.Management.GatewayDefinitionsManagement.Handles("/api/v1/semantic-models/x"));
    }
}
