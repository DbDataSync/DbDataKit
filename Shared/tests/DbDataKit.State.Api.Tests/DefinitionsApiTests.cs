using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api.Tests;

/// <summary>Plan 30 PB02: the shared publish API on a plain state, kind by kind.</summary>
public sealed class DefinitionsApiTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-defs-api-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly List<string> audited = [];

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private static readonly DefinitionsCaller Admin = new("root", true, true, true);
    private static readonly DefinitionsCaller PublisherA = new("a", false, true, true, "client-a");
    private static readonly DefinitionsCaller PublisherB = new("b", false, true, true, "client-b");
    private static readonly DefinitionsCaller Viewer = new("v", false, false, true);

    private DefinitionsApi Open(List<(string Kind, string Name, string Channel)>? changes = null)
    {
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("model", ["serving"], PublishMode.Serve) { Slots = [new DefinitionSlot("warehouse")], Validator = c => c.Manifest.Contains("broken") ? "it does not load" : null })
            .Register(new DefinitionKind("page", ["serving"], PublishMode.Serve))
            .Register(new DefinitionKind("job", ["runner"], PublishMode.Stage));
        var payloads = new PayloadStore(database);
        return new DefinitionsApi(new DefinitionPublisher(database, kinds, payloads, "serving"), new DefinitionStore(database), kinds, payloads, new GroupSettings(database), "serving",
            (verb, caller, kind, name, channel, outcome, _) => audited.Add($"{verb} {caller.Actor} {kind}/{name} {channel} {outcome}"), (k, n, c) => changes?.Add((k, n, c)));
    }

    private static string Put(object body) => System.Text.Json.JsonSerializer.Serialize(body);

    [Fact]
    public void A_publish_serves_a_version_the_registry_checks_and_tells_the_host()
    {
        var changes = new List<(string, string, string)>();
        var api = Open(changes);
        var ok = api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v1", note = "first" }), PublisherA);
        Assert.Equal(200, ok.Status);
        Assert.Equal(1, (int)ok.Body["version"]!);
        Assert.Equal([("page", "home", "prod")], changes);

        Assert.Equal(404, api.Handle("PUT", "definitions/nope/x", Put(new { manifest = "v" }), Admin).Status);
        Assert.Equal(400, api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v" }), Admin).Status);   // a runner kind is not accepted here
        var broken = api.Handle("PUT", "definitions/page/other", Put(new { manifest = "x" }), Admin);
        Assert.Equal(200, broken.Status);
        Assert.Equal(400, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v2", expected_version = 1, mode = "x" }), Admin).Status);
        var stale = api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v2", expected_version = 5 }), Admin);
        Assert.Equal(409, stale.Status);
        Assert.Equal("version_conflict", (string)stale.Body["code"]!);
        Assert.Contains("definition.publish a page/home prod ok", audited);
    }

    [Fact]
    public void A_publisher_changes_only_what_it_created_and_only_an_administrator_moves_versions()
    {
        var api = Open();
        Assert.Equal(200, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v1" }), PublisherA).Status);
        Assert.Equal(403, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v2" }), PublisherB).Status);
        Assert.Equal(200, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v2" }), PublisherA).Status);
        Assert.Equal(403, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v3" }), Viewer).Status);
        Assert.Equal(403, api.Handle("POST", "definitions/page/home/rollback", "{}", PublisherA).Status);

        var back = api.Handle("POST", "definitions/page/home/rollback", Put(new { expected_version = 2 }), Admin);
        Assert.Equal(200, back.Status);
        Assert.Equal(1, (int)back.Body["active"]!);
        Assert.Equal(200, api.Handle("POST", "definitions/page/home/switch", Put(new { version = 2 }), Admin).Status);
        Assert.True((bool)api.Handle("POST", "definitions/page/home/retire", "{}", Admin).Body["retired"]!);

        var described = api.Handle("GET", "definitions/page/home", null, Viewer);
        Assert.Equal(2, ((JsonArray)described.Body["versions"]!).Count);
        Assert.Equal("client-a", (string)described.Body["owner"]!);
        Assert.Single((JsonArray)api.Handle("GET", "definitions/page", null, Viewer).Body["definitions"]!);
    }

    [Fact]
    public void A_version_whose_slot_is_unbound_in_the_channel_is_refused_naming_it_until_it_is_bound()
    {
        var api = Open();
        var refused = api.Handle("PUT", "definitions/model/sales", Put(new { manifest = "m1", channel = "test" }), Admin);
        Assert.Equal(409, refused.Status);
        Assert.Contains("warehouse", (string)refused.Body["message"]!);

        var bound = api.Handle("PUT", "bindings/test/model/sales", Put(new { slots = new { warehouse = "connection:dw" } }), Admin);
        Assert.Equal(200, bound.Status);
        Assert.Equal(200, api.Handle("PUT", "definitions/model/sales", Put(new { manifest = "m1", channel = "test" }), Admin).Status);
        Assert.Contains("definition.bind root model/sales test ok", audited);
        Assert.DoesNotContain(audited, a => a.Contains("connection:dw"));
        Assert.True((bool)api.Handle("DELETE", "bindings/test/model/sales/warehouse", null, Admin).Body["removed"]!);
    }

    [Fact]
    public async Task An_upload_is_checked_against_its_hash_and_a_version_names_it_by_hash()
    {
        var api = Open();
        var bytes = "hello"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.False(api.HasArtifact(sha));
        var wrong = await api.StoreArtifactAsync(new string('0', 64), new MemoryStream(bytes), null, PublisherA);
        Assert.Equal(400, wrong.Status);
        Assert.False(api.HasArtifact(new string('0', 64)));
        Assert.Equal(403, (await api.StoreArtifactAsync(sha, new MemoryStream(bytes), null, Viewer)).Status);
        var stored = await api.StoreArtifactAsync(sha, new MemoryStream(bytes), "text/plain", PublisherA);
        Assert.False((bool)stored.Body["already"]!);
        Assert.True(api.HasArtifact(sha));
        Assert.True((bool)(await api.StoreArtifactAsync(sha, new MemoryStream(bytes), null, PublisherA)).Body["already"]!);

        var published = api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v1", artifacts = new[] { new { path = "a.txt", sha256 = sha, size = bytes.Length, media_type = "text/plain" } } }), PublisherA);
        Assert.Equal(200, published.Status);
        var missing = api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v2", artifacts = new[] { new { path = "b.txt", sha256 = new string('1', 64), size = 1 } } }), PublisherA);
        Assert.Equal(400, missing.Status);
    }

    [Fact]
    public void Prune_is_a_dry_run_unless_told_otherwise()
    {
        var api = Open();
        for (var i = 1; i <= 5; i++) api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v" + i }), Admin);
        var dry = api.Handle("POST", "definitions/page/home/prune", Put(new { keep = 1 }), Admin);
        Assert.True((bool)dry.Body["dry_run"]!);
        Assert.Equal([1, 2, 3], ((JsonArray)dry.Body["remove"]!).Select(n => (int)n!));
        Assert.Equal(400, api.Handle("POST", "definitions/page/home/prune", "{}", Admin).Status);
        api.Handle("POST", "definitions/page/home/prune", Put(new { keep = 1, dry_run = false }), Admin);
        Assert.Equal(2, ((JsonArray)api.Handle("GET", "definitions/page/home", null, Admin).Body["versions"]!).Count);
    }
}
