using System.Text;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Apps.Tests;

/// <summary>
/// Plan 26 section 3, plan 20 O08c: an app's data calls go through the apps host, which checks the app's token and hands the call, with the person, the app and the scope of its manifest, to what answers it (in the serving
/// layer, the group's published models); nothing but row reads and changes, and nothing for a token that is not this instance's.
/// </summary>
public sealed class AppDataGatewayTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-gateway-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity host = new("dbdatagateway-apps", ConsoleSigningKey.Generate());
    private readonly Recorder data = new();
    private readonly AppInstanceStore instances;
    private readonly AppDataGateway gateway;
    private readonly AppSession session;

    public AppDataGatewayTests()
    {
        Directory.CreateDirectory(folder);
        instances = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
        instances.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write], region: [read]}\n", "ada");
        instances.Save(new AppInstance("acme", "notes", Bindings: new Dictionary<string, string> { ["territories"] = "acme-data" }), "ada");
        gateway = new AppDataGateway(instances, data, host);
        session = new AppSessions().Create(new DbDataKit.Identity.HomeIdentity("user-7", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:acme"] = ["author"] }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private sealed class Recorder : IAppData
    {
        public List<AppDataCall> Seen { get; } = [];

        public Task<AppResponse> SendAsync(AppDataCall call, CancellationToken cancellationToken)
        {
            Seen.Add(call);
            return Task.FromResult(new AppResponse(200, "application/json", new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{\"ok\":true}")));
        }
    }

    private string AppToken() => new AppTokenBroker(host.Issuer()).Issue(session, "acme").Token;

    private Task<AppResponse> Call(string path, string? token, string method = "GET", string query = "", string body = "", string instance = "acme") =>
        gateway.ForwardAsync(instance, path, method, query, token is null ? null : "Bearer " + token, Encoding.UTF8.GetBytes(body), default);

    [Fact]
    public async Task A_call_with_the_apps_token_is_answered_for_the_person_with_the_apps_scope_and_the_copy_its_binding_names()
    {
        var response = await Call("api/v1/models/territories/entities/territory/rows", AppToken(), query: "?limit=5&offset=10");

        Assert.Equal(200, response.Status);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("*", response.Headers["Access-Control-Allow-Origin"]);   // the route's own CORS, whatever the answer carried
        var call = Assert.Single(data.Seen);
        Assert.Null(call.Via);   // a sandboxed app has no approval to name
        Assert.Equal((AppDataRoute.Rows, "territories", "territory", "acme-data"), (call.Route, call.Model, call.Entity, call.Copy));   // the instance's binding chose the copy
        Assert.Equal(("user-7", "Ada", "acme"), (call.SubjectId, call.SubjectName, call.App));
        Assert.Equal(["territories/territory:write", "territories/region:read"], call.Scope);
        Assert.Equal(ServerRole.Author, call.Roles & ServerRole.Author);
        Assert.Equal(("5", "10"), (call.Query["limit"], call.Query["offset"]));
        Assert.True(call.Allows("territory", write: true) && call.Allows("region", write: false));
        Assert.False(call.Allows("region", write: true) || call.Allows("district", write: false));
    }

    [Fact]
    public async Task A_post_of_changes_carries_its_body_and_a_copy_named_in_the_path_wins_over_the_binding()
    {
        var response = await Call("api/v1/models/territories/instances/other/changes", AppToken(), "POST", body: "{\"changes\":[]}");

        Assert.Equal(200, response.Status);
        var call = Assert.Single(data.Seen);
        Assert.Equal((AppDataRoute.Changes, null, "other", "{\"changes\":[]}"), (call.Route, call.Entity, call.Copy, Encoding.UTF8.GetString(call.Body)));

        Assert.Equal(200, (await Call("api/v1/models/territories/entities/territory/row", AppToken(), query: "?key.code=NA")).Status);
        Assert.Equal((AppDataRoute.Row, "NA"), (data.Seen[^1].Route, data.Seen[^1].Query["key.code"]));
    }

    [Fact]
    public async Task The_schema_of_a_model_is_a_read_the_data_route_answers_for_the_apps_scope()
    {
        var response = await Call("api/v1/models/territories/schema", AppToken());
        Assert.Equal(200, response.Status);
        var call = Assert.Single(data.Seen);
        Assert.Equal((AppDataRoute.Schema, "territories", null), (call.Route, call.Model, call.Entity));
        Assert.Equal(["territories/territory:write", "territories/region:read"], call.Scope);   // the answerer keeps the entities this lists
        Assert.Equal(405, (await Call("api/v1/models/territories/schema", AppToken(), "POST")).Status);
        Assert.Equal(401, (await Call("api/v1/models/territories/schema", null)).Status);
    }

    [Fact]
    public async Task A_preview_of_an_instance_reads_what_it_reads_for_the_roles_of_the_instance_and_never_writes_and_its_token_is_its_own()
    {
        const string manifest = "schemaVersion: 1\nname: notes\nversion: 1.1.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write], region: [read]}\n";
        instances.PublishApp(manifest, "ada", "preview");
        instances.Save(new AppInstance("acme", "notes", Bindings: new Dictionary<string, string> { ["territories"] = "acme-data" }), "ada", "preview");
        Assert.Equal(("acme", "preview"), AppInstanceName.Split("acme~preview"));
        Assert.Equal(("acme", DbDataKit.State.DefinitionStore.Production), AppInstanceName.Split("acme"));
        Assert.Equal("1.1.0", instances.Resolve("acme~preview")!.Manifest.Version);   // the preview channel's version
        Assert.Equal("1.0.0", instances.Resolve("acme")!.Manifest.Version);           // production's is untouched
        Assert.Null(instances.Resolve("acme~test"));
        Assert.Equal(["author"], session.RolesOn("acme~preview"));                  // the roles on `acme` open its preview
        var preview = new AppTokenBroker(host.Issuer()).Issue(session, "acme~preview");
        Assert.Equal("app:acme~preview", preview.Audience);

        Assert.Equal(200, (await Call("api/v1/models/territories/entities/territory/rows", preview.Token, instance: "acme~preview")).Status);
        var call = Assert.Single(data.Seen);
        Assert.Equal(("acme~preview", "acme-data"), (call.App, call.Copy));   // it reads the copy the instance is bound to: production's data
        var write = await Call("api/v1/models/territories/changes", preview.Token, "POST", body: "{\"changes\":[]}", instance: "acme~preview");
        Assert.Equal(403, write.Status);
        Assert.Contains("preview", Encoding.UTF8.GetString(write.Body));
        Assert.Single(data.Seen);                                                // the write never reached the data

        // a token for the preview is no good for production, and production's is no good for the preview
        Assert.Equal(401, (await Call("api/v1/models/territories/entities/territory/rows", preview.Token)).Status);
        Assert.Equal(401, (await Call("api/v1/models/territories/entities/territory/rows", AppToken(), instance: "acme~preview")).Status);
        Assert.Equal(200, (await Call("api/v1/models/territories/changes", AppToken(), "POST", body: "{\"changes\":[]}")).Status);   // production still writes
    }

    [Fact]
    public async Task An_app_with_change_request_may_read_and_propose_but_the_scope_never_says_write()
    {
        instances.PublishApp("schemaVersion: 1\nname: ask\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [change request], region: [read]}\n", "ada");
        instances.Save(new AppInstance("ask", "ask"), "ada");
        var token = new AppTokenBroker(host.Issuer()).Issue(new AppSessions().Create(new DbDataKit.Identity.HomeIdentity("user-7", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:ask"] = ["author"] })), "ask").Token;

        Assert.Equal(200, (await Call("api/v1/models/territories/changes", token, "POST", body: "{\"changes\":[]}", instance: "ask")).Status);
        var call = Assert.Single(data.Seen);
        Assert.Equal(["territories/territory:propose", "territories/region:read"], call.Scope);
        Assert.True(call.Proposes("territory"));
        Assert.False(call.Proposes("region"));                                  // read only
        Assert.True(call.Allows("territory", write: false));                   // proposing a change is reading the row first
        Assert.False(call.Allows("territory", write: true));                    // and the app never applies one itself
    }

    [Theory]
    [InlineData("api/v1/models")]
    [InlineData("api/v1/models/territories/storage/create")]
    [InlineData("api/v1/models/territories/requests/1/approve")]
    [InlineData("api/v1/info")]
    [InlineData("api/v1/models/territories/entities/territory/../../storage")]
    public async Task Only_row_reads_the_schema_and_changes_are_answered(string path)
    {
        Assert.Equal(403, (await Call(path, AppToken())).Status);
        Assert.Empty(data.Seen);
    }

    [Fact]
    public async Task Rows_are_read_with_get_and_changes_sent_with_post()
    {
        Assert.Equal(405, (await Call("api/v1/models/territories/entities/territory/rows", AppToken(), "POST")).Status);
        Assert.Equal(405, (await Call("api/v1/models/territories/changes", AppToken(), "GET")).Status);
        Assert.Empty(data.Seen);
    }

    [Fact]
    public async Task The_path_an_app_used_with_a_paired_store_is_refused_with_the_path_it_now_takes()
    {
        var response = await Call("store-1/api/v1/models/territories/entities/territory/rows", AppToken());

        Assert.Equal(410, response.Status);
        Assert.Contains("/apps/acme/_data/api/v1/models/", Encoding.UTF8.GetString(response.Body));
        Assert.Empty(data.Seen);
    }

    [Fact]
    public async Task A_trusted_apps_data_calls_stop_when_no_approval_stands_for_its_version()
    {
        var definitions = new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module]));
        instances.PublishApp("schemaVersion: 1\nname: vault\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\ntrust: trusted\nmodels:\n  - model: territories\n    entities: {territory: [read]}\n", "ada");
        instances.Save(new AppInstance("acme", "vault"), "ada");
        var approvals = new AppApprovalStore(definitions);
        var gated = new AppDataGateway(instances, data, host, approvals: approvals);
        Task<AppResponse> Read() => gated.ForwardAsync("acme", "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + AppToken(), [], default);

        Assert.Equal(403, (await Read()).Status);
        var approval = approvals.Approve("vault", 1, "grace");
        Assert.Equal(200, (await Read()).Status);
        approvals.Revoke(approval.Id, "grace");
        Assert.Equal(403, (await Read()).Status);   // within the token's five minutes
        // the call that ran carries the approval for the servers' audit: the app, its version, the approver and the approval's id
        Assert.Equal(approval.AuditLabel, Assert.Single(data.Seen).Via);
        Assert.Contains("approved by grace", approval.AuditLabel);
    }

    private sealed class SemanticRecorder : IAppSemantic
    {
        public List<AppSemanticCall> Seen { get; } = [];

        public Task<AppResponse> SendAsync(AppSemanticCall call, CancellationToken cancellationToken)
        {
            Seen.Add(call);
            return Task.FromResult(new AppResponse(200, "application/json", new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{\"ok\":true}")));
        }
    }

    [Fact]
    public async Task The_semantic_route_checks_the_token_the_verb_and_the_manifests_models_before_the_host_is_asked()
    {
        instances.PublishApp("schemaVersion: 1\nname: shop\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nsemantic: [sales, ops]\n", "ada");
        instances.Save(new AppInstance("shop", "shop"), "ada");
        var semantic = new SemanticRecorder();
        var gated = new AppDataGateway(instances, data, host, semantic: semantic);
        string Token(string instance) => new AppTokenBroker(host.Issuer()).Issue(new AppSessions().Create(new DbDataKit.Identity.HomeIdentity("user-7", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:" + instance] = ["viewer"] })), instance).Token;
        Task<AppResponse> Send(string path, string method, string? token, string body = "") =>
            gated.ForwardAsync("shop", path, method, "", token is null ? null : "Bearer " + token, Encoding.UTF8.GetBytes(body), default);

        var list = await Send("api/v1/semantic/models", "GET", Token("shop"));
        Assert.Equal(200, list.Status);
        Assert.Equal("*", list.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal((AppSemanticRoute.Models, "shop", "user-7"), (semantic.Seen[0].Route, semantic.Seen[0].App, semantic.Seen[0].SubjectId));
        Assert.Equal(["sales", "ops"], semantic.Seen[0].Allowed);

        Assert.Equal(200, (await Send("api/v1/semantic/models/sales/catalog", "GET", Token("shop"))).Status);
        Assert.Equal((AppSemanticRoute.Catalog, "sales"), (semantic.Seen[1].Route, semantic.Seen[1].Model));
        Assert.Equal(200, (await Send("api/v1/semantic/models/ops/query", "POST", Token("shop"), "{\"kind\":\"query\"}")).Status);
        Assert.Equal((AppSemanticRoute.Query, "ops", "{\"kind\":\"query\"}"), (semantic.Seen[2].Route, semantic.Seen[2].Model, Encoding.UTF8.GetString(semantic.Seen[2].Body)));
        var asked = semantic.Seen.Count;

        // a model the manifest does not list, whatever the host serves; the verbs; paths the route does not have; no token and another instance's token
        var unlisted = await Send("api/v1/semantic/models/finance/query", "POST", Token("shop"), "{}");
        Assert.Equal(403, unlisted.Status);
        Assert.Contains("does not list it", Encoding.UTF8.GetString(unlisted.Body));
        Assert.Equal(405, (await Send("api/v1/semantic/models/sales/query", "GET", Token("shop"))).Status);
        Assert.Equal(405, (await Send("api/v1/semantic/models", "POST", Token("shop"))).Status);
        Assert.Equal(403, (await Send("api/v1/semantic/models/sales/members", "GET", Token("shop"))).Status);
        Assert.Equal(403, (await Send("api/v1/semantic/models/sales/query/extra", "POST", Token("shop"))).Status);
        Assert.Equal(401, (await Send("api/v1/semantic/models", "GET", null)).Status);
        Assert.Equal(401, (await Send("api/v1/semantic/models", "GET", Token("acme"))).Status);
        Assert.Equal(asked, semantic.Seen.Count);

        // a host with no semantic models answers 404 for the route and still answers rows
        var without = new AppDataGateway(instances, data, host);
        Assert.Equal(404, (await without.ForwardAsync("shop", "api/v1/semantic/models", "GET", "", "Bearer " + Token("shop"), [], default)).Status);
    }

    [Fact]
    public async Task A_missing_foreign_or_other_instances_token_is_refused_before_anything_is_asked()
    {
        const string Path = "api/v1/models/territories/entities/territory/rows";
        Assert.Equal(401, (await Call(Path, null)).Status);
        Assert.Equal(401, (await Call(Path, "not-a-token")).Status);

        // a token signed by some other key, and a token for another instance's audience
        var stranger = new ConsoleIdentity(host.Id, ConsoleSigningKey.Generate());
        Assert.Equal(401, (await Call(Path, new AppTokenBroker(stranger.Issuer()).Issue(session, "acme").Token)).Status);
        var other = new AppTokenBroker(host.Issuer()).Issue(new AppSessions().Create(session.Identity with { Roles = new Dictionary<string, IReadOnlyList<string>> { ["app:globex"] = ["author"] } }), "globex").Token;
        Assert.Equal(401, (await Call(Path, other)).Status);
        Assert.Equal(404, (await Call(Path, AppToken(), instance: "globex")).Status);   // no such instance
        Assert.Empty(data.Seen);
    }
}
