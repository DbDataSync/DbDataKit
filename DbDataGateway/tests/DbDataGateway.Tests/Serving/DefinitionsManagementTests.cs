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
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main")));
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
