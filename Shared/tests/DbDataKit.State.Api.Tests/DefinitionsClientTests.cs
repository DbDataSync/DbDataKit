using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api.Tests;

/// <summary>Plan 30 PB02: the client of the shared publish API (<see cref="DefinitionsClient"/>) over a group's API, and the hook members a host with owner rules of its own uses (<c>BeforePublishAsync</c>, the request body, <c>OwnerOf</c>, <c>BeforeRead</c>).</summary>
public sealed class DefinitionsClientTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-defs-client-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private static readonly DefinitionsCaller Admin = new("root", true, true, true);
    private static readonly DefinitionsCaller PublisherA = new("a", false, true, false, "client-a");
    private static readonly DefinitionsCaller PublisherB = new("b", false, true, false, "client-b");

    /// <summary>A kind of a host with its own owner rules: a manifest names its owner (<c>owner=...</c>), a refusal waits on a task, and the body's <c>adopt</c> reaches the hook.</summary>
    private sealed class OwnedHook : IDefinitionKindHook
    {
        public string Kind => "model";
        public bool? SignaturesRequired { get; set; }
        public List<string> Seen { get; } = [];
        public Dictionary<string, string> Owners { get; } = [];

        public async Task BeforePublishAsync(PublishHookContext context, CancellationToken ct)
        {
            await Task.Delay(1, ct);
            Seen.Add($"before {context.Name} adopt={context.Body["adopt"]}");
            if (context.Manifest.Contains("broken")) throw new DefinitionsRefusalException(400, "The model does not load.") { Body = new JsonObject { ["error"] = "The model does not load.", ["valid"] = false } };
        }

        public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response)
        {
            Owners[context.Name] = context.Caller.ClientId ?? "admin";
            response["served"] = true;
        }

        public string? OwnerOf(string name) => name == "legacy" ? "client-a" : null;

        public void BeforeRead(string name, DefinitionsCaller caller)
        {
            if (!caller.Admin && Owners.TryGetValue(name, out var owner) && owner != caller.ClientId) throw new DefinitionsRefusalException(403, "This client may not read that model.");
        }
    }

    /// <summary>A group's API reached as a client reaches it: through a link with the caller's roles.</summary>
    private sealed class Link(DefinitionsApi api, DefinitionsCaller caller) : IDefinitionsLink
    {
        public string Name => "group";
        public List<string> Calls { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            Calls.Add($"{method.Method} {path}");
            if (path.StartsWith("artifacts/", StringComparison.Ordinal))
            {
                var sha = path["artifacts/".Length..];
                if (method == HttpMethod.Head) return new(api.HasArtifact(sha) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                var stored = await api.StoreArtifactAsync(sha, await content!.ReadAsStreamAsync(ct), null, caller, ct);
                return Reply(stored);
            }
            var body = content is null ? null : await content.ReadAsStringAsync(ct);
            return Reply(await api.HandleAsync(method.Method, path, body, caller, ct));
        }

        private static HttpResponseMessage Reply(DefinitionsResult result) => new((HttpStatusCode)result.Status) { Content = new StringContent(result.Body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private (DefinitionsApi Api, OwnedHook Hook, DefinitionTrustStore Trust, SignaturePolicy Policy) Open()
    {
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        var kinds = new DefinitionKinds().Register(new DefinitionKind("model", ["serving"], PublishMode.Serve)).Register(new DefinitionKind("page", ["serving"], PublishMode.Serve) { Slots = [new DefinitionSlot("db")] });
        var payloads = new PayloadStore(database);
        var trust = new DefinitionTrustStore(database);
        var settings = new GroupSettings(database);
        var policy = new SignaturePolicy(settings);
        var hook = new OwnedHook();
        var api = new DefinitionsApi(new DefinitionPublisher(database, kinds, payloads, "serving"), new DefinitionStore(database), kinds, payloads, settings, "serving",
            verifier: new TrustStoreVerifier(trust), policy: policy, trust: trust, hooks: [hook]);
        return (api, hook, trust, policy);
    }

    private static string Key(ConsoleSigningKey key) => Url.Encode(key.PublicKey);

    [Fact]
    public async Task A_client_publishes_over_what_it_read_and_a_stale_version_is_the_groups_refusal()
    {
        var (api, _, _, policy) = Open();
        policy.MarkTestGroup(true);
        var client = new DefinitionsClient(new Link(api, PublisherA));

        Assert.Null(await client.ServedAsync("model", "sales", "prod"));
        var first = await client.PublishAsync(new DefinitionPublishRequest("model", "sales", "one", ExpectedVersion: 0));
        Assert.True(first.Ok, first.Error);
        Assert.Equal(1, (int)first.Body["version"]!);

        var served = await client.ServedAsync("model", "sales", "prod");
        Assert.Equal(new ServedDefinition(1, 1, "one"), served);
        var second = await client.PublishAsync(new DefinitionPublishRequest("model", "sales", "two", ExpectedVersion: served!.Latest));
        Assert.Equal(2, (int)second.Body["version"]!);

        var stale = await client.PublishAsync(new DefinitionPublishRequest("model", "sales", "three", ExpectedVersion: 1));
        Assert.Equal(409, stale.Status);
        Assert.Equal("version_conflict", (string)stale.Body["code"]!);
        Assert.Equal(2, (int)stale.Body["current_version"]!);
    }

    [Fact]
    public async Task A_hook_waits_on_its_check_sees_the_body_and_adds_to_the_answer_and_its_refusal_keeps_its_words()
    {
        var (api, hook, _, policy) = Open();
        policy.MarkTestGroup(true);
        var client = new DefinitionsClient(new Link(api, PublisherA));

        var bad = await client.PublishAsync(new DefinitionPublishRequest("model", "m", "broken", Extra: new JsonObject { ["adopt"] = "x" }));
        Assert.Equal(400, bad.Status);
        Assert.Equal("The model does not load.", bad.Error);
        Assert.False((bool)bad.Body["valid"]!);
        Assert.Null(await client.ServedAsync("model", "m", "prod"));

        var ok = await client.PublishAsync(new DefinitionPublishRequest("model", "m", "fine", Extra: new JsonObject { ["adopt"] = "x" }));
        Assert.True((bool)ok.Body["served"]!);
        Assert.Contains("before m adopt=x", hook.Seen);
    }

    [Fact]
    public async Task The_owner_a_host_names_is_the_owner_the_api_enforces_and_a_host_may_refuse_a_read()
    {
        var (api, _, _, policy) = Open();
        policy.MarkTestGroup(true);
        var a = new DefinitionsClient(new Link(api, PublisherA));
        var b = new DefinitionsClient(new Link(api, PublisherB));

        // 'legacy' was made through a route that predates the API (the host knows its owner); there is no version and no owner setting in the group
        var refused = await b.PublishAsync(new DefinitionPublishRequest("model", "legacy", "x"));
        Assert.Equal(403, refused.Status);
        Assert.Contains("belongs to another publisher", refused.Error);
        Assert.True((await a.PublishAsync(new DefinitionPublishRequest("model", "legacy", "x"))).Ok);

        Assert.True((await a.PublishAsync(new DefinitionPublishRequest("model", "mine", "x"))).Ok);
        var seen = new List<DefinitionsAnswer>();
        Assert.Null(await b.ServedAsync("model", "mine", "prod", seen.Add));
        Assert.Equal(403, Assert.Single(seen).Status);
        Assert.Equal("This client may not read that model.", seen[0].Error);
    }

    [Fact]
    public async Task A_client_signs_with_a_key_the_group_trusts_and_says_what_to_do_when_the_group_requires_a_signature_it_does_not_trust()
    {
        var (api, hook, _, _) = Open();
        var key = ConsoleSigningKey.Generate();
        var link = new Link(api, PublisherA);
        var client = new DefinitionsClient(link, key);

        // the hook says signatures are not required: the version goes unsigned, and the key is not trusted, so it is not used
        hook.SignaturesRequired = false;
        var unsigned = await client.PublishAsync(new DefinitionPublishRequest("model", "m", "one"));
        Assert.True(unsigned.Ok, unsigned.Error);
        Assert.Equal("unsigned", (string)unsigned.Body["signature"]!);

        // the group requires them and does not trust the key: the client says so before sending, with the call that fixes it
        hook.SignaturesRequired = true;
        var untrusted = await client.PublishAsync(new DefinitionPublishRequest("model", "m", "two"));
        Assert.Equal(403, untrusted.Status);
        Assert.Equal("signature_key_not_trusted", (string)untrusted.Body["code"]!);
        Assert.Contains($"PUT /api/v1/trust/model/keys", untrusted.Error);
        Assert.Contains(key.Fingerprint, untrusted.Error);
        Assert.Single(link.Calls, c => c == "PUT definitions/model/m");   // the refused one was never sent

        // an administrator trusts the key: the version is signed and verified
        Assert.Equal(200, api.Handle("PUT", "trust/model/keys", $"{{\"public_key\":\"{Key(key)}\",\"label\":\"build\"}}", Admin).Status);
        var signed = await client.PublishAsync(new DefinitionPublishRequest("model", "m", "two"));
        Assert.True(signed.Ok, signed.Error);
        Assert.Equal("verified", (string)signed.Body["signature"]!);
    }

    [Fact]
    public async Task Files_are_uploaded_by_hash_once_and_bindings_are_made_over_the_same_link()
    {
        var (api, _, _, policy) = Open();
        policy.MarkTestGroup(true);
        var link = new Link(api, Admin);
        var client = new DefinitionsClient(link);
        var files = new[] { new DefinitionFile("data/a.txt", Encoding.UTF8.GetBytes("hello"), "text/plain") };

        var bound = await client.BindAsync("prod", "page", "p", new Dictionary<string, string> { ["db"] = "erp" });
        Assert.Equal(200, bound.Status);
        Assert.True((await client.PublishAsync(new DefinitionPublishRequest("page", "p", "m1", files))).Ok);
        Assert.True((await client.PublishAsync(new DefinitionPublishRequest("page", "p", "m2", files))).Ok);
        Assert.Single(link.Calls, c => c.StartsWith("PUT artifacts/", StringComparison.Ordinal));
        Assert.Equal(2, link.Calls.Count(c => c.StartsWith("HEAD artifacts/", StringComparison.Ordinal)));
    }
}
