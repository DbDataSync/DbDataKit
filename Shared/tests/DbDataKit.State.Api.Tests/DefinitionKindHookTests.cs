using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api.Tests;

/// <summary>Plan 30 PB02: what one kind adds to the shared publish API (<see cref="IDefinitionKindHook"/>), and that a kind without a hook is published as before.</summary>
public sealed class DefinitionKindHookTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-defs-hook-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly List<string> audited = [];

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    private static readonly DefinitionsCaller Admin = new("root", true, true, true);
    private static readonly DefinitionsCaller PublisherA = new("a", false, true, false, "client-a");
    private static readonly DefinitionsCaller PublisherB = new("b", false, true, false, "client-b");

    private sealed class Hook : IDefinitionKindHook
    {
        public string Kind => "job";
        public List<string> Seen { get; } = [];
        public bool PublisherMayRead => false;
        public bool? SignaturesRequired { get; set; }
        public string? UnsignedRefusal(string name, SignatureVerdict verdict) => $"The job {name} is unsigned (hook says so)";
        public int? StatusOf(PublishRefusal reason) => reason == PublishRefusal.UnboundSlot ? 400 : null;
        public void CheckChannel(string channel) { if (channel == "closed") throw new DefinitionsRefusalException(403, $"This host serves no channel '{channel}'."); }
        public void BeforePublish(PublishHookContext context) { Seen.Add($"before {context.Name}/{context.Channel}"); if (context.Manifest.Contains("loop")) throw new DefinitionsRefusalException(400, "A trigger loop."); }
        public void AfterPublish(PublishHookContext context, PublishResult result, JsonObject response) { Seen.Add($"after {context.Name}@{result.Version.Version}"); response["tasks"] = new JsonArray("a", "b"); }
        public Task<DefinitionsResult?> ActAsync(LifecycleHookContext context, CancellationToken ct)
        {
            Seen.Add($"{context.Act} {context.Name}/{context.Channel} expected={context.ExpectedVersion} version={context.Version}");
            return Task.FromResult<DefinitionsResult?>(context.Act == "activate" ? new DefinitionsResult(202, new JsonObject { ["state"] = "waiting", ["via"] = "hook" }) : null);
        }
        public void BeforeBind(BindingHookContext context) { if (context.Slots.ContainsKey("bad")) throw new DefinitionsRefusalException(409, "Production's connection would serve a non-production channel.") { Body = new JsonObject { ["needsConfirmation"] = new JsonArray("scope") } }; }
        public void AfterBind(BindingHookContext context) => Seen.Add("bound " + string.Join(",", context.Slots.Keys));
        public void BeforeUnbind(BindingHookContext context) { if (context.Slots.ContainsKey("held")) throw new DefinitionsRefusalException(409, "A served version declares the slot."); }
        public DefinitionsResult? AfterUnbind(BindingHookContext context, bool removed) => removed ? null : new DefinitionsResult(404, new JsonObject { ["error"] = "not bound" });
        public JsonObject? Describe(string name) => name == "special" ? new JsonObject { ["own"] = "shape" } : null;
    }

    private (DefinitionsApi Api, Hook Hook) Open(bool withHook = true)
    {
        var database = new StateDatabase(file, StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module, PayloadStore.Module, TrustModule.Module], new StateScope(Group: "main")));
        var kinds = new DefinitionKinds()
            .Register(new DefinitionKind("job", ["serving"], PublishMode.Serve) { Slots = [new DefinitionSlot("db")] })
            .Register(new DefinitionKind("page", ["serving"], PublishMode.Serve));
        var payloads = new PayloadStore(database);
        var hook = new Hook();
        var api = new DefinitionsApi(new DefinitionPublisher(database, kinds, payloads, "serving"), new DefinitionStore(database), kinds, payloads, new GroupSettings(database), "serving",
            (verb, caller, kind, name, channel, outcome, _) => audited.Add($"{verb} {caller.Actor} {kind}/{name} {channel} {outcome}"),
            verifier: new TrustStoreVerifier(new DefinitionTrustStore(database)), trust: new DefinitionTrustStore(database), hooks: withHook ? [hook] : null, payloadOwner: "mine");
        return (api, hook);
    }

    private static string Put(object body) => System.Text.Json.JsonSerializer.Serialize(body);

    [Fact]
    public void A_hook_checks_the_channel_and_the_manifest_before_the_version_is_stored_and_adds_to_the_answer_after()
    {
        var (api, hook) = Open();
        Assert.Equal(403, api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v", channel = "closed", mode = "stage" }), Admin).Status);
        var loop = api.Handle("PUT", "definitions/job/x", Put(new { manifest = "loop", mode = "stage" }), Admin);
        Assert.Equal(400, loop.Status);
        Assert.Equal("A trigger loop.", (string)loop.Body["error"]!);
        Assert.Equal(404, api.Handle("GET", "definitions/job/x", null, Admin).Status);   // nothing was stored
        Assert.Contains("definition.publish root job/x closed refused", audited);   // a hook's refusal is audited like any refusal
        Assert.Contains("definition.publish root job/x prod refused", audited);

        api.Handle("PUT", "bindings/prod/job/x", Put(new { slots = new { db = "erp" } }), Admin);
        var ok = api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1" }), Admin);
        Assert.Equal(200, ok.Status);
        Assert.Equal(["a", "b"], ((JsonArray)ok.Body["tasks"]!).Select(n => (string)n!));
        Assert.Equal(["before x/prod", "before x/prod", "after x@1"], hook.Seen.Where(s => s.StartsWith("before", StringComparison.Ordinal) || s.StartsWith("after", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_kind_without_a_hook_is_published_as_before_and_a_hook_is_for_its_kind_only()
    {
        var (api, hook) = Open();
        Assert.Equal(200, api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v1", channel = "closed" }), Admin).Status);   // the page kind has no hook
        Assert.Empty(hook.Seen);
        var plain = Open(withHook: false).Api;
        plain.Handle("PUT", "bindings/closed/job/x", Put(new { slots = new { db = "erp" } }), Admin);
        Assert.Equal(200, plain.Handle("PUT", "definitions/job/x", Put(new { manifest = "loop", channel = "closed" }), Admin).Status);
    }

    [Fact]
    public async Task A_hook_takes_the_life_cycle_calls_and_the_api_still_refuses_what_is_not_an_administrators()
    {
        var (api, hook) = Open();
        api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1", mode = "stage" }), Admin);   // the slot "db" is unbound: refused (409 unless the hook maps it)
        api.Handle("PUT", "bindings/prod/job/x", Put(new { slots = new { db = "erp" } }), Admin);
        Assert.Equal(200, api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1", mode = "stage" }), Admin).Status);
        var waiting = await api.HandleAsync("POST", "definitions/job/x/activate", Put(new { channel = "prod", expected_version = 1 }), Admin);
        Assert.Equal(202, waiting.Status);
        Assert.Equal("hook", (string)waiting.Body["via"]!);
        Assert.Contains("activate x/prod expected=1 version=", hook.Seen);
        // switch is answered by the API itself (the hook returned null)
        Assert.Equal(400, (await api.HandleAsync("POST", "definitions/job/x/switch", Put(new { channel = "prod" }), Admin)).Status);
        // a publisher never reaches the hook
        Assert.Equal(403, (await api.HandleAsync("POST", "definitions/job/x/activate", Put(new { channel = "prod" }), PublisherA)).Status);
        Assert.DoesNotContain("activate x/prod expected= version=", hook.Seen);
        Assert.Contains("definition.activate a job/x prod refused", audited);
    }

    [Fact]
    public void A_publisher_reads_only_where_the_hook_lets_it_and_the_hook_may_describe_a_definition_its_own_way()
    {
        var (api, _) = Open();
        api.Handle("PUT", "bindings/prod/job/special", Put(new { slots = new { db = "erp" } }), Admin);
        api.Handle("PUT", "definitions/job/special", Put(new { manifest = "v1" }), Admin);
        api.Handle("PUT", "definitions/page/home", Put(new { manifest = "v1" }), Admin);
        Assert.Equal(403, api.Handle("GET", "definitions/job", null, PublisherA).Status);
        Assert.Equal(403, api.Handle("GET", "trust/job", null, PublisherA).Status);
        Assert.Equal(200, api.Handle("GET", "definitions/page", null, PublisherA).Status);
        Assert.Equal("shape", (string)api.Handle("GET", "definitions/job/special", null, Admin).Body["own"]!);
        Assert.Equal(1, (int)api.Handle("GET", "definitions/page/home", null, Admin).Body["latest_version"]!);
    }

    [Fact]
    public void A_hook_vetoes_a_binding_before_it_is_written_and_a_refused_registry_rule_answers_the_status_it_chose()
    {
        var (api, hook) = Open();
        var unbound = api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1" }), Admin);
        Assert.Equal(400, unbound.Status);   // the registry's UnboundSlot, answered 400 by this kind's hook (409 for a kind without one)
        Assert.Equal(409, Open(withHook: false).Api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1" }), Admin).Status);

        var refused = api.Handle("PUT", "bindings/prod/job/x", Put(new { slots = new { bad = "erp" } }), Admin);
        Assert.Equal(409, refused.Status);
        Assert.NotNull(refused.Body["needsConfirmation"]);   // the hook's own body is the answer
        Assert.Contains("definition.bind root job/x prod refused", audited);
        Assert.DoesNotContain(hook.Seen, s => s.StartsWith("bound", StringComparison.Ordinal));

        Assert.Equal(200, api.Handle("PUT", "bindings/prod/job/x", Put(new { slots = new { db = "erp" } }), Admin).Status);
        Assert.Contains("bound db", hook.Seen);
        Assert.Equal(409, api.Handle("DELETE", "bindings/prod/job/x/held", null, Admin).Status);
        Assert.Equal(404, api.Handle("DELETE", "bindings/prod/job/x/missing", null, Admin).Status);   // the hook words an empty slot as 404
        Assert.Equal(200, api.Handle("DELETE", "bindings/prod/job/x/db", null, Admin).Status);
    }

    [Fact]
    public void The_hook_says_whether_signatures_are_required_and_words_the_refusal()
    {
        var (api, hook) = Open();
        api.Handle("PUT", "bindings/prod/job/x", Put(new { slots = new { db = "erp" } }), Admin);
        hook.SignaturesRequired = true;
        var refused = api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1" }), Admin);
        Assert.Equal(403, refused.Status);
        Assert.Contains("The job x is unsigned (hook says so)", (string)refused.Body["message"]!);
        Assert.True((bool)api.Handle("GET", "trust/job", null, Admin).Body["required"]!);
        hook.SignaturesRequired = false;
        Assert.Equal(200, api.Handle("PUT", "definitions/job/x", Put(new { manifest = "v1" }), Admin).Status);
    }

    [Fact]
    public void Binding_the_slots_of_a_first_version_makes_the_binder_its_owner_so_another_publisher_cannot_publish_over_it()
    {
        var (api, _) = Open(withHook: false);
        Assert.Equal(200, api.Handle("PUT", "bindings/prod/job/new", Put(new { slots = new { db = "erp" } }), PublisherA).Status);
        var other = api.Handle("PUT", "definitions/job/new", Put(new { manifest = "v1" }), PublisherB);
        Assert.Equal(403, other.Status);
        Assert.Contains("publisher role", (string)other.Body["message"]!);
        Assert.Equal(200, api.Handle("PUT", "definitions/job/new", Put(new { manifest = "v1" }), PublisherA).Status);
        Assert.Equal(403, api.Handle("PUT", "bindings/prod/job/new", Put(new { slots = new { db = "other" } }), PublisherB).Status);
    }
}
