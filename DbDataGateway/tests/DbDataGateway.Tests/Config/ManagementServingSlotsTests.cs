using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 33 SW01 (plan 30 PB02): the slots of the serving kinds. A model names the data sources it needs in its manifest (<c>slots</c>); a version that names one is refused, naming the slot, until the channel binds it; the binding of a data source made by hand is
/// an administrator's act, recorded and audited, and takes the place of the per-publish <c>adopt</c>. The adopt call (hand a data source to a client) is on the shared route, and a publisher lists only the names it owns.
/// </summary>
public sealed partial class ManagementTests
{
    private static string ModelWithSlots(string catalog, string? managedBy, string source, params string[] slots) =>
        JsonSerializer(new { catalog, managedBy, sources = new Dictionary<string, string> { ["sqlserver_sales"] = source }, ssas = new Dictionary<string, string>(), slots });

    private static string EntityWithSlots(string name, string? managedBy, string dataSource, params string[] slots) => JsonSerializer(new { model = TerritoriesModel.Replace("name: territories", $"name: {name}", StringComparison.Ordinal), dataSource, managedBy, slots });

    private static string SlotBody(string slot, string value) => JsonSerializer(new { slots = new Dictionary<string, string> { [slot] = value } });

    private async Task<(HttpStatusCode Status, JsonNode Body)> BindAsync(PairedServer admin, string kind, string name, string slot, string value)
    {
        var response = await CallAsync(admin, HttpMethod.Put, $"/api/v1/bindings/prod/{kind}/{name}", ServerRole.Admin, SlotBody(slot, value));
        return (response.StatusCode, await BodyAsync(response));
    }

    [Fact]
    public async Task A_model_on_a_data_source_made_by_hand_is_refused_naming_the_slot_until_an_administrator_binds_it_and_then_needs_no_adopt()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(SalesData("by-hand"), null))).StatusCode);
        var manifest = ModelWithSlots(CatalogText, "build-1", "by-hand", "by-hand");

        // the slot is not bound: the registry refuses the version and names it (a data source with no slot would have been refused for want of adopt)
        var unbound = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(manifest));
        Assert.Equal(HttpStatusCode.Conflict, unbound.Status);
        Assert.Equal("unboundslot", (string)unbound.Body["code"]!);
        Assert.Contains("'by-hand'", (string)unbound.Body["error"]!);
        Assert.Contains("channel 'prod'", (string)unbound.Body["error"]!);
        Assert.Null(app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));

        // a publishing client binds only the data sources it made; the refusal says who does and with which call
        var byClient = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/semantic-model/sales_catalog", ServerRole.Publisher, SlotBody("by-hand", "by-hand"));
        Assert.Equal(HttpStatusCode.Forbidden, byClient.StatusCode);
        Assert.Contains("An administrator binds it once", (string)(await BodyAsync(byClient))["error"]!);

        // an administrator binds the slot to the data source of the same name, and to nothing else
        var other = await BindAsync(admin, "semantic-model", "sales_catalog", "by-hand", "elsewhere");
        Assert.Equal(HttpStatusCode.BadRequest, other.Status);
        Assert.Contains("bound to the data source of the same name", (string)other.Body["error"]!);
        var missing = await BindAsync(admin, "semantic-model", "sales_catalog", "nowhere", "nowhere");
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("is not a data source of this server", (string)missing.Body["error"]!);
        var bound = await BindAsync(admin, "semantic-model", "sales_catalog", "by-hand", "by-hand");
        Assert.True(bound.Status == HttpStatusCode.OK, bound.Body.ToJsonString());
        Assert.Equal("by-hand", (string)bound.Body["bindings"]![0]!["bound_to"]!);

        // the same publish, with no adopt, is taken and served; the use is recorded on the data source and in the audit
        var ok = await DefineAsync(build1, first, "semantic-model", "sales_catalog", DefinitionBody(manifest));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());
        Assert.Equal("by-hand", (string)ok.Body["slots_used"]![0]!);
        Assert.Null(ok.Body["adopted"]);
        Assert.Equal(1, app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));
        var source = await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Admin));
        Assert.Contains(source["adoptions"]!.AsArray(), a => (string)a!["client"]! == "build-1" && (string)a["model"]! == "sales_catalog" && (string)a["kind"]! == "semantic-model (slot)");
        var audit = ConfigTestSupport.AuditEntries(dataDirectory);
        // the binding is audited with the client that made the call and the person it acted for
        Assert.Contains(audit, a => (string)a["command"]! == "definition.bind" && (string)a["target"]! == "semantic-model/sales_catalog/prod" && (string)a["outcome"]! == "ok" && (string)a["userId"]! == "console-1" && (string)a["userName"]! == "Ada");
        Assert.Contains(audit, a => (string)a["command"]! == "data-source use" && (string)a["target"]! == "by-hand" && ((string)a["outcome"]!).Contains("through its slot binding", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, a => (string)a["command"]! == "data-source adopt");

        // the binding is the model's own: another model of the same client on that data source is refused naming its own slot
        var second = await DefineAsync(build1, first, "semantic-model", "sales_two", DefinitionBody(ModelWithSlots(CatalogText.Replace("name: sales_catalog", "name: sales_two"), "build-1", "by-hand", "by-hand")));
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Contains("'by-hand'", (string)second.Body["error"]!);
    }

    [Fact]
    public async Task An_entity_model_on_a_data_source_made_by_hand_is_refused_naming_the_slot_until_an_administrator_binds_it_and_then_needs_no_adopt()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null))).StatusCode);
        var manifest = EntityWithSlots("territories", "build-1", "by-hand", "by-hand");

        var unbound = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(manifest));
        Assert.Equal(HttpStatusCode.Conflict, unbound.Status);
        Assert.Contains("'by-hand'", (string)unbound.Body["error"]!);
        Assert.Null(app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedEntityModels>().Find("territories"));

        Assert.True((await BindAsync(admin, "entity-model", "territories", "by-hand", "by-hand")).Status == HttpStatusCode.OK);
        var ok = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(manifest));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());
        Assert.Equal("by-hand", (string)ok.Body["slots_used"]![0]!);
        Assert.NotNull(app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedEntityModels>().Find("territories"));
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source use" && ((string)a["outcome"]!).Contains("for the model territories", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_publisher_binds_the_slot_of_a_data_source_it_made_nobody_binds_another_clients_and_a_slot_nothing_reads_is_refused()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build1, first, "data-source", "mine", DefinitionBody(Plain("mine") + "managed_by: build-1\n"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build2, second, "data-source", "theirs", DefinitionBody(Plain("theirs") + "managed_by: build-2\n"))).Status);

        // own: bound by the client itself, and the model on it is taken
        var own = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/entity-model/territories", ServerRole.Publisher, SlotBody("mine", "mine"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var ok = await DefineAsync(build1, first, "entity-model", "territories", DefinitionBody(EntityWithSlots("territories", "build-1", "mine", "mine")));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());

        // another client's: not even an administrator binds it (it is handed over first)
        var other = await BindAsync(admin, "entity-model", "territories2", "theirs", "theirs");
        Assert.Equal(HttpStatusCode.Forbidden, other.Status);
        Assert.Contains("an administrator hands it over first", (string)other.Body["error"]!);
        var mine = await DefineAsync(build1, first, "entity-model", "territories2", DefinitionBody(EntityWithSlots("territories2", "build-1", "theirs", "theirs")));
        Assert.Equal(HttpStatusCode.BadRequest, mine.Status);
        Assert.Contains("ask an administrator to hand it over", (string)mine.Body["error"]!);

        // a slot is named by a data source the version uses
        var unused = await DefineAsync(build1, first, "entity-model", "territories3", DefinitionBody(EntityWithSlots("territories3", "build-1", "mine", "mine", "ghost")));
        Assert.Equal(HttpStatusCode.BadRequest, unused.Status);
        Assert.Contains("declares the slot 'ghost'", (string)unused.Body["error"]!);
    }

    [Fact]
    public async Task The_adopt_call_is_on_the_shared_route_an_administrators_and_hands_the_data_source_to_a_client()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/definitions/data-source/by-hand", ServerRole.Publisher)).StatusCode);

        // a server client is refused whatever it holds, and so is a call with less than the admin role
        var refused = await CallAsAsync(build1, first, HttpMethod.Post, "/api/v1/definitions/data-source/by-hand/adopt", ServerRole.Publisher, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("managed_by", File.ReadAllText(ConnectionFile("by-hand")));

        var adopted = await CallAsync(admin, HttpMethod.Post, "/api/v1/definitions/data-source/by-hand/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
        var answer = await BodyAsync(adopted);
        Assert.True((bool)answer["adopted"]!);
        Assert.Equal("build-1", (string)answer["managed_by"]!);
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionFile("by-hand")));
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && (string)a["outcome"]! == "adopted by build-1" && (string)a["userId"]! == "ada");

        // a kind that cannot be handed over says so
        var model = await CallAsync(admin, HttpMethod.Post, "/api/v1/definitions/semantic-model/sales_catalog/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.NotFound, model.StatusCode);
    }

    [Fact]
    public async Task A_publisher_lists_the_definitions_of_a_kind_it_owns_and_no_others_and_an_administrator_lists_them_all()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build1, first, "data-source", "mine", DefinitionBody(Plain("mine") + "managed_by: build-1\n"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await DefineAsync(build2, second, "data-source", "theirs", DefinitionBody(Plain("theirs") + "managed_by: build-2\n"))).Status);

        async Task<string[]> Names(Task<HttpResponseMessage> call) => [.. (await BodyAsync(await call))["definitions"]!.AsArray().Select(d => (string)d!["name"]!).Order(StringComparer.Ordinal)];
        Assert.Equal(["mine"], await Names(CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/definitions/data-source", ServerRole.Publisher)));
        Assert.Equal(["theirs"], await Names(CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/definitions/data-source", ServerRole.Publisher)));
        Assert.Equal(["mine", "theirs"], await Names(CallAsync(admin, HttpMethod.Get, "/api/v1/definitions/data-source", ServerRole.Admin)));
    }
}
