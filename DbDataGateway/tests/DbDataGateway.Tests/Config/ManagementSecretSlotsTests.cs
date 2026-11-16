using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Data;
using DbDataGateway.Server.Data;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 30 PB02 (owner decision of 2026-11-16): the secret push as a binding. Every data source, whoever made it, has a slot per secret reference its file names; a channel binds each to the secret of the same name the Gateway holds, and a version
/// (a publish through the shared route or the per-kind route, a switch or an activation: where a model's unbound slot is refused; a rollback is not checked, for either) is refused while its channel has no binding for a slot, naming the data source, the slot, the channel and how to bind it. What is already served keeps serving.
/// A publishing client binds the secrets it stored, an administrator any; a secret the Gateway does not hold is not bound.
/// </summary>
public sealed partial class ManagementTests
{
    private async Task<string> StoreSecretAsync(ConsoleIdentity who, PairedServer server, string name, string value, ServerRole role = ServerRole.Publisher)
    {
        var stored = await CallAsAsync(who, server, HttpMethod.Put, $"/api/v1/secrets/{name}", role, SecretBody(value));
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        return (string)(await BodyAsync(stored))["reference"]!;
    }

    private static string Slot(string reference) => reference["secret:".Length..];

    [Fact]
    public async Task A_data_source_naming_a_secret_is_refused_naming_the_data_source_the_slot_the_channel_and_how_to_bind_it_until_the_channel_binds_the_secret()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        var reference = await StoreSecretAsync(build1, first, "sales_conn", "Data Source=:memory:");
        var slot = Slot(reference);
        Assert.Equal("dbdatakit:gateway:managed-build-1:sales_conn", slot);
        var text = $"schema: connection/1\nname: sales\nengine: duckdb\nconnection_string_secret: '{reference}'\nmanaged_by: build-1\n";

        // the clean break: the version is refused, and the refusal says which data source, which slot, which channel and how to bind it
        var unbound = await DefineAsync(build1, first, "data-source", "sales", DefinitionBody(text));
        Assert.Equal(HttpStatusCode.Conflict, unbound.Status);
        Assert.Equal("unboundslot", (string)unbound.Body["code"]!);
        var error = (string)unbound.Body["error"]!;
        Assert.Contains("data-source 'sales'", error);
        Assert.Contains($"'{slot}'", error);
        Assert.Contains("channel 'prod'", error);
        Assert.Contains("Data sources screen (the Secret slots box of the data source", error);
        Assert.Contains($"PUT /api/v1/bindings/prod/data-source/sales {{\"slots\": {{\"{slot}\": \"{slot}\"}}}}", error);
        Assert.False(File.Exists(ConnectionFile("sales")));

        // the binding rules: the secret of the same name, one the client stored itself, one the Gateway holds
        var renamed = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/sales", ServerRole.Publisher, SlotBody(slot, "dbdatakit:gateway:managed-build-1:other"));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Contains("bound to the secret of the same name", (string)(await BodyAsync(renamed))["error"]!);
        var notHeld = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/sales", ServerRole.Publisher, SlotBody("dbdatakit:gateway:managed-build-1:never-stored", "dbdatakit:gateway:managed-build-1:never-stored"));
        Assert.Equal(HttpStatusCode.Conflict, notHeld.StatusCode);
        Assert.Contains("holds no secret", (string)(await BodyAsync(notHeld))["error"]!);
        var theirs = await StoreSecretAsync(build2, second, "theirs", "x");
        var notOwn = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/sales", ServerRole.Publisher, SlotBody(Slot(theirs), Slot(theirs)));
        Assert.Equal(HttpStatusCode.Forbidden, notOwn.StatusCode);
        Assert.Contains("a publishing client binds only its own", (string)(await BodyAsync(notOwn))["error"]!);
        var environment = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/sales", ServerRole.Publisher, SlotBody("env:PATH", "env:PATH"));
        Assert.Equal(HttpStatusCode.Forbidden, environment.StatusCode);

        // its own secret: bound (names only in the audit), and the same publish is taken and served
        var bound = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/sales", ServerRole.Publisher, SlotBody(slot, slot));
        Assert.True(bound.StatusCode == HttpStatusCode.OK, await bound.Content.ReadAsStringAsync());
        var ok = await DefineAsync(build1, first, "data-source", "sales", DefinitionBody(text));
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Body.ToJsonString());
        await using (var result = await app.Services.GetRequiredService<DataSourceCatalog>().Resolve(Anyone, "sales").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 42 AS answer"), CancellationToken.None))
            Assert.Single(result.Schema.FieldsList);
        Assert.Contains(ConfigTestSupport.AuditEntries(dataDirectory), a => (string)a["command"]! == "definition.bind" && (string)a["target"]! == "data-source/sales/prod" && (string)a["outcome"]! == "ok");

        // the data source's read shows the slot, its binding and who made it
        var admin = await PairAsync();
        var read = await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/sales", ServerRole.Admin));
        var row = read["secret_slots"]!.AsArray().Single()!;
        Assert.Equal(slot, (string)row["slot"]!);
        Assert.Equal("connection_string_secret", (string)row["path"]!);
        Assert.Equal(slot, (string)row["bound_to"]!);
        Assert.Equal("Ada", (string)row["bound_by"]!);
        Assert.True((bool)row["held"]!);

        // a binding in production is production's: another channel binds the secret itself
        var staging = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/definitions/data-source/sales", ServerRole.Publisher,
            JsonSerializer(new Dictionary<string, object?> { ["manifest"] = text, ["expected_version"] = null, ["channel"] = "staging" }));
        var stagingBody = await BodyAsync(staging);
        Assert.True(staging.StatusCode == HttpStatusCode.Conflict, stagingBody.ToJsonString());
        Assert.Contains("channel 'staging'", (string)stagingBody["error"]!);
    }

    [Fact]
    public async Task A_data_source_made_by_hand_keeps_serving_and_a_change_through_the_per_kind_route_is_refused_until_an_administrator_binds_its_secret()
    {
        var variable = "DBDATAKIT_TEST_SLOT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await StartAsync();   // writes connections/erp.yaml by hand, naming env:ERP_DB_PASSWORD
        var admin = await PairAsync();

        // what is served keeps serving: the file made by hand is listed and read, and its read shows the slot no channel binds yet
        var erp = await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/erp", ServerRole.Admin));
        var erpSlot = erp["secret_slots"]!.AsArray().Single()!;
        Assert.Equal("env:ERP_DB_PASSWORD", (string)erpSlot["slot"]!);
        Assert.Equal("auth.password_secret", (string)erpSlot["path"]!);
        Assert.Null(erpSlot["bound_to"]);
        Assert.Contains("erp", app.Services.GetRequiredService<IOptionsMonitor<DbDataGateway.Server.Configuration.GatewayOptions>>().CurrentValue.DataSources.Keys);

        // a new data source an administrator makes by hand that names a stored secret: refused, nothing written, the answer carries the slot and the call
        var reference = await StoreSecretAsync(console, admin, "ledger-login", "pw", ServerRole.Admin);
        var slot = Slot(reference);
        var text = Conn.Yaml("ledger", "duckdb", "Data Source=:memory:", $"auth: {{ mode: sql, user: u, password_secret: '{reference}' }}");
        var refused = await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/ledger", ServerRole.Admin, Put(text, null));
        var body = await BodyAsync(refused);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("unboundslot", (string)body["code"]!);
        Assert.Contains("data-source 'ledger'", (string)body["error"]!);
        Assert.Contains($"'{slot}'", (string)body["error"]!);
        Assert.Contains("channel 'prod'", (string)body["error"]!);
        Assert.Contains("Secret slots box", (string)body["error"]!);
        Assert.Equal(slot, (string)body["slots"]![0]!["slot"]!);
        Assert.Equal("auth.password_secret", (string)body["slots"]![0]!["path"]!);
        Assert.Equal($"PUT /api/v1/bindings/prod/data-source/ledger {{\"slots\": {{\"{slot}\": \"{slot}\"}}}}", (string)body["bind"]!);
        Assert.False(File.Exists(ConnectionFile("ledger")));

        // an administrator binds it before the data source exists, and the write goes through
        Assert.Equal(HttpStatusCode.OK, (await BindAsync(admin, "data-source", "ledger", slot, slot)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/ledger", ServerRole.Admin, Put(text, null))).StatusCode);

        // the file made by hand: a change is refused until its variable is bound, and a variable the Gateway does not have is not bound
        var change = Conn.Yaml("erp", connectionString: "Server=s2;Encrypt=True", auth: $"auth: {{ mode: sql, user: u, password_secret: 'env:{variable}' }}");
        var current = (string)erp["version"]!;
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/erp", ServerRole.Admin, Put(change, current))).StatusCode);
        var missing = await BindAsync(admin, "data-source", "erp", $"env:{variable}", $"env:{variable}");
        Assert.Equal(HttpStatusCode.Conflict, missing.Status);
        Assert.Contains("holds no secret", (string)missing.Body["error"]!);
        Environment.SetEnvironmentVariable(variable, "pw");
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await BindAsync(admin, "data-source", "erp", $"env:{variable}", $"env:{variable}")).Status);
            var changed = await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/erp", ServerRole.Admin, Put(change, current));
            Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }

        // removing the binding stops nothing that is served; the next version naming the secret is refused again
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Delete, $"/api/v1/bindings/prod/data-source/ledger/{Uri.EscapeDataString(slot)}", ServerRole.Admin)).StatusCode);
        Assert.Contains("ledger", app.Services.GetRequiredService<IOptionsMonitor<DbDataGateway.Server.Configuration.GatewayOptions>>().CurrentValue.DataSources.Keys);
        var again = await BodyAsync(await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/ledger", ServerRole.Admin));
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/ledger", ServerRole.Admin, Put(text.Replace("user: u", "user: v"), (string)again["version"]!))).StatusCode);
    }

    [Fact]
    public async Task A_switch_back_to_a_version_that_names_an_unbound_secret_is_refused_naming_the_slot()
    {
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var reference = await StoreSecretAsync(build1, first, "warehouse", "Data Source=:memory:");
        var slot = Slot(reference);
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/bindings/prod/data-source/wh", ServerRole.Publisher, SlotBody(slot, slot))).StatusCode);
        var v1 = await DefineAsync(build1, first, "data-source", "wh", DefinitionBody($"schema: connection/1\nname: wh\nengine: duckdb\nconnection_string_secret: '{reference}'\nmanaged_by: build-1\n"));
        Assert.True(v1.Status == HttpStatusCode.OK, v1.Body.ToJsonString());
        var v2 = await DefineAsync(build1, first, "data-source", "wh", DefinitionBody(Plain("wh") + "managed_by: build-1\n", expected: 1));
        Assert.True(v2.Status == HttpStatusCode.OK, v2.Body.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, first, HttpMethod.Delete, $"/api/v1/bindings/prod/data-source/wh/{Uri.EscapeDataString(slot)}", ServerRole.Publisher)).StatusCode);
        var admin = await PairAsync();
        var back = await CallAsync(admin, HttpMethod.Post, "/api/v1/definitions/data-source/wh/switch", ServerRole.Admin, JsonSerializer(new { channel = "prod", version = 1 }));
        var body = await BodyAsync(back);
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal("unboundslot", (string)body["code"]!);
        Assert.Contains($"'{slot}'", (string)body["error"]!);
        Assert.Contains("channel 'prod'", (string)body["error"]!);
    }
}
