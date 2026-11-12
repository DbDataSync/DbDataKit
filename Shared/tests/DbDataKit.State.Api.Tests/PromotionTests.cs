using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api.Tests;

/// <summary>Plan 30 PB06: promotion between groups, a version read from one group and published to another over the shared API.</summary>
public sealed class PromotionTests : IDisposable
{
    private readonly List<string> files = [];
    private static readonly DefinitionsCaller Admin = new("root", true, true, true);

    public void Dispose()
    {
        foreach (var file in files)
            foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    /// <summary>A group: its own state, the same kinds, a trust store and the shared API on it.</summary>
    private sealed class Group(string name, DefinitionsApi api, DefinitionTrustStore trust) : IDefinitionsLink
    {
        public string Name { get; } = name;

        public DefinitionsApi Api { get; } = api;

        public DefinitionTrustStore Trust { get; } = trust;

        public int Uploads;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            if (path.StartsWith("artifacts/", StringComparison.Ordinal))
            {
                var sha = path["artifacts/".Length..];
                if (method == HttpMethod.Head) return new(Api.HasArtifact(sha) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                if (method == HttpMethod.Get) return Api.OpenArtifact(sha) is { } stream ? new(HttpStatusCode.OK) { Content = new StreamContent(stream) } : new(HttpStatusCode.NotFound);
                Interlocked.Increment(ref Uploads);
                var stored = await Api.StoreArtifactAsync(sha, await content!.ReadAsStreamAsync(ct), null, Admin, ct);
                return Json(stored);
            }

            var body = content is null ? null : await content.ReadAsStringAsync(ct);
            return Json(await Api.HandleAsync(method.Method, path, body, Admin, ct));
        }

        private static HttpResponseMessage Json(DefinitionsResult result) => new((HttpStatusCode)result.Status) { Content = new StringContent(result.Body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private Group Open(string name)
    {
        var file = Path.Combine(Path.GetTempPath(), "dbdata-promote-" + Guid.NewGuid().ToString("N") + ".db");
        files.Add(file);
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("model", ["serving"], PublishMode.Serve) { Slots = [new DefinitionSlot("warehouse")] })
            .Register(new DefinitionKind("page", ["serving"], PublishMode.Serve));
        var payloads = new PayloadStore(database);
        var trust = new DefinitionTrustStore(database);
        var api = new DefinitionsApi(new DefinitionPublisher(database, kinds, payloads, "serving"), new DefinitionStore(database), kinds, payloads, new GroupSettings(database), "serving",
            verifier: new TrustStoreVerifier(trust), policy: new SignaturePolicy(new GroupSettings(database)), trust: trust);
        return new Group(name, api, trust);
    }

    private static string Put(object body) => System.Text.Json.JsonSerializer.Serialize(body);

    private static void Trust(Group group, ConsoleSigningKey key, string kind) =>
        Assert.Equal(200, group.Api.Handle("PUT", $"trust/{kind}/keys", Put(new { public_key = Convert.ToBase64String(key.PublicKey).TrimEnd('=').Replace('+', '-').Replace('/', '_'), label = "ci" }), Admin).Status);

    private static async Task<ArtifactRef> Upload(Group group, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await group.Api.StoreArtifactAsync(sha, new MemoryStream(bytes), "text/plain", Admin);
        return new ArtifactRef("data/" + sha[..6] + ".txt", sha, bytes.Length, "text/plain");
    }

    private static async Task<int> PublishSigned(Group group, ConsoleSigningKey key, string kind, string name, string manifest, string channel, params ArtifactRef[] artifacts)
    {
        var signature = DefinitionSigning.Sign(key, kind, name, manifest, artifacts);
        var answer = group.Api.Handle("PUT", $"definitions/{kind}/{name}", Put(new { manifest, channel, signature, artifacts = artifacts.Select(a => new { path = a.Path, sha256 = a.Sha256, size = a.Size, media_type = a.MediaType }) }), Admin);
        Assert.Equal(200, answer.Status);
        await Task.CompletedTask;
        return (int)answer.Body["version"]!;
    }

    [Fact]
    public async Task A_version_moves_from_a_test_group_to_a_production_group_with_its_signature_and_the_second_call_sends_nothing()
    {
        var test = Open("test-group");
        var production = Open("production-group");
        var key = ConsoleSigningKey.Generate();
        Trust(test, key, "page");
        Trust(production, key, "page");
        var one = await Upload(test, "first file");
        var two = await Upload(test, "second file");
        await PublishSigned(test, key, "page", "home", "v1", "prod");
        var v2 = await PublishSigned(test, key, "page", "home", "v2", "prod", one, two);
        Assert.Equal(2, v2);

        var promoter = new DefinitionPromoter();
        var first = await promoter.PromoteAsync(test, production, new PromoteRequest("page", "home", "prod", "prod"));
        Assert.Equal(2, first.SourceVersion);
        Assert.Equal(1, first.TargetVersion);   // the production group numbers its own versions
        Assert.True(first.Changed);
        Assert.Equal((2, 0), (first.Sent, first.Skipped));
        Assert.Equal("verified", first.Signature);
        Assert.Equal(2, production.Uploads);

        var read = production.Api.Handle("GET", "definitions/page/home/versions/1", null, Admin);
        Assert.Equal("v2", (string)read.Body["manifest"]!);
        Assert.StartsWith("ed25519:", (string)read.Body["signature"]!);
        Assert.Equal(2, ((JsonArray)read.Body["artifacts"]!).Count);
        Assert.Equal(1, (int)((JsonArray)production.Api.Handle("GET", "definitions/page/home", null, Admin).Body["channels"]!)[0]!["active"]!);
        // the bytes are the ones the source holds
        using (var stream = production.Api.OpenArtifact(one.Sha256)!) Assert.Equal("first file", new StreamReader(stream).ReadToEnd());

        var again = await promoter.PromoteAsync(test, production, new PromoteRequest("page", "home", "prod", "prod"));
        Assert.False(again.Changed);
        Assert.Equal((0, 2), (again.Sent, again.Skipped));
        Assert.Equal(2, production.Uploads);
        Assert.Equal(1, again.TargetVersion);
    }

    [Fact]
    public async Task A_group_that_does_not_trust_the_key_refuses_the_version()
    {
        var test = Open("test-group");
        var production = Open("production-group");
        var key = ConsoleSigningKey.Generate();
        Trust(test, key, "page");
        await PublishSigned(test, key, "page", "home", "v1", "prod");
        var ex = await Assert.ThrowsAsync<DefinitionPromotionException>(() => new DefinitionPromoter().PromoteAsync(test, production, new PromoteRequest("page", "home", "prod", "prod")));
        Assert.Equal(403, ex.Status);
        Assert.Contains("production-group refused", ex.Message);
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(production.Api.Handle("GET", "definitions/page", null, Admin).Body["definitions"]!.AsArray());
    }

    [Fact]
    public async Task An_unbound_slot_refuses_naming_it_and_the_target_groups_own_bindings_fix_it()
    {
        var test = Open("test-group");
        var production = Open("production-group");
        var key = ConsoleSigningKey.Generate();
        Trust(test, key, "model");
        Trust(production, key, "model");
        Assert.Equal(200, test.Api.Handle("PUT", "bindings/prod/model/sales", Put(new { slots = new { warehouse = "test-warehouse" } }), Admin).Status);
        await PublishSigned(test, key, "model", "sales", "m1", "prod");

        var promoter = new DefinitionPromoter();
        var refused = await Assert.ThrowsAsync<DefinitionPromotionException>(() => promoter.PromoteAsync(test, production, new PromoteRequest("model", "sales", "prod", "prod")));
        Assert.Equal(409, refused.Status);
        Assert.Contains("warehouse", refused.Message);
        Assert.Empty(production.Api.Handle("GET", "definitions/model", null, Admin).Body["definitions"]!.AsArray());

        var done = await promoter.PromoteAsync(test, production, new PromoteRequest("model", "sales", "prod", "prod", Bindings: new Dictionary<string, string> { ["warehouse"] = "production-warehouse" }));
        Assert.Equal(["warehouse"], done.Bound);
        var bindings = (JsonArray)production.Api.Handle("GET", "definitions/model/sales", null, Admin).Body["bindings"]!;
        Assert.Equal("production-warehouse", (string)bindings[0]!["bound_to"]!);
        // the source group's binding is its own and was not touched
        Assert.Equal("test-warehouse", (string)((JsonArray)test.Api.Handle("GET", "definitions/model/sales", null, Admin).Body["bindings"]!)[0]!["bound_to"]!);

        // once the channel is bound the next promotion needs no bindings
        await PublishSigned(test, key, "model", "sales", "m2", "prod");
        var next = await promoter.PromoteAsync(test, production, new PromoteRequest("model", "sales", "prod", "prod"));
        Assert.Equal((2, 2), (next.SourceVersion, next.TargetVersion));
    }

    [Fact]
    public async Task A_dependency_that_is_not_active_in_the_target_refuses_and_a_channel_with_nothing_active_has_nothing_to_promote()
    {
        var test = Open("test-group");
        var production = Open("production-group");
        var key = ConsoleSigningKey.Generate();
        foreach (var group in new[] { test, production }) Trust(group, key, "page");
        await PublishSigned(test, key, "page", "base", "b1", "prod");
        var signature = DefinitionSigning.Sign(key, "page", "top", "t1", []);
        Assert.Equal(200, test.Api.Handle("PUT", "definitions/page/top", Put(new { manifest = "t1", signature, depends_on = new[] { "page:base" } }), Admin).Status);

        var promoter = new DefinitionPromoter();
        var refused = await Assert.ThrowsAsync<DefinitionPromotionException>(() => promoter.PromoteAsync(test, production, new PromoteRequest("page", "top", "prod", "prod")));
        Assert.Contains("base", refused.Message);
        await promoter.PromoteAsync(test, production, new PromoteRequest("page", "base", "prod", "prod"));
        Assert.Equal(1, (await promoter.PromoteAsync(test, production, new PromoteRequest("page", "top", "prod", "prod"))).TargetVersion);

        var none = await Assert.ThrowsAsync<DefinitionPromotionException>(() => promoter.PromoteAsync(test, production, new PromoteRequest("page", "base", "preview", "prod")));
        Assert.Contains("no active version in channel 'preview'", none.Message);
        var missing = await Assert.ThrowsAsync<DefinitionPromotionException>(() => promoter.PromoteAsync(test, production, new PromoteRequest("page", "ghost", "prod", "prod")));
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task A_version_is_promoted_to_another_channel_staged_and_by_number()
    {
        var test = Open("test-group");
        var production = Open("production-group");
        var key = ConsoleSigningKey.Generate();
        foreach (var group in new[] { test, production }) Trust(group, key, "page");
        await PublishSigned(test, key, "page", "home", "v1", "prod");
        await PublishSigned(test, key, "page", "home", "v2", "prod");

        var staged = await new DefinitionPromoter().PromoteAsync(test, production, new PromoteRequest("page", "home", "prod", "preview", Version: 1, Mode: "stage"));
        Assert.Equal("stage", staged.Mode);
        Assert.Equal(1, staged.SourceVersion);
        var head = (JsonObject)((JsonArray)production.Api.Handle("GET", "definitions/page/home", null, Admin).Body["channels"]!)[0]!;
        Assert.Equal("preview", (string)head["channel"]!);
        Assert.Equal(1, (int)head["staged"]!);
    }
}
