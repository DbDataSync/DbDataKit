using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 22a GP02: the rules of the `access` section travel with the published definition. A model with rules is published in format 2 (a live instance that reads only format 1 refuses it, naming the instance); its version carries the rules,
/// a change of a rule is a new version and the same rules again are not, a rollback returns the old rules with the old version; a rules table the data source cannot read is refused naming the table, before anything is stored; the answer
/// warns that no Build metadata was there to check the rules table against (QR2). The shared publish API does the same as the route.
/// </summary>
public sealed partial class ManagementTests
{
    private const string RulesTableYaml = """
          - name: region_access
            x_source: sqlserver_sales
            source: region_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}
        """;

    /// <summary>The sample catalog with a rules table and an `access` section that filters its customer table by region (a rule is `include` by default; <paramref name="effect"/> changes it).</summary>
    private static string AccessCatalog(bool rulesTable = true, string effect = "include", bool access = true)
    {
        var text = CatalogText.Replace("\ntables:\n", "\ntables:\n" + (rulesTable ? RulesTableYaml.TrimStart('\n').TrimEnd() + "\n" : ""), StringComparison.Ordinal).TrimEnd() + "\n";
        if (!access) return text;
        return text + $$"""
            access:
              row:
                - dataset: dim_customer
                  groups:
                    - id: by_region
                      rules:
                        - { id: r1, effect: {{effect}}, table: region_access, subject: who, columns: { region: region_code } }
                        - { id: r2, effect: include, table: region_access, subject: who, columns: { region: region_code } }
            """;
    }

    private string SalesDataFor(string catalogText, string name)
    {
        var file = Path.Combine(dataDirectory, name + ".duckdb");
        using var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file);
        connection.Open();
        foreach (var ddl in SalesSchema.Tables(catalogText))
        {
            using var command = connection.CreateCommand();
            command.CommandText = ddl;
            command.ExecuteNonQuery();
        }
        return Conn.Yaml(name, "duckdb", "Data Source=" + file);
    }

    [Fact]
    public async Task A_model_with_access_rules_is_published_in_format_2_with_its_rules_and_a_rollback_returns_the_old_rules_with_the_old_version()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(AccessCatalog(), "sales-ro"), null));
        var store = app.Services.GetRequiredService<DefinitionStore>();

        async Task<HttpResponseMessage> PutAsync(string catalog, int expected) =>
            await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(catalog, "sales-ro", expected: expected == 0 ? null : expected));

        var plain = await PutAsync(AccessCatalog(access: false), 0);
        Assert.True(plain.StatusCode == HttpStatusCode.OK, await plain.Content.ReadAsStringAsync());
        Assert.Equal(1, store.Current("semantic-model", "sales_catalog")!.Format);

        var ruled = await PutAsync(AccessCatalog(), 1);
        Assert.True(ruled.StatusCode == HttpStatusCode.OK, await ruled.Content.ReadAsStringAsync());
        var body = await BodyAsync(ruled);
        Assert.Equal(2, (int)body["version"]!);
        // the answer says what was not checked: no Build metadata to tell that a Build model produces the rules table (QR2)
        Assert.Contains("region_access", (string)body["warnings"]![0]!);
        Assert.Contains("build-metadata", (string)body["warnings"]![0]!);
        var v2 = store.Current("semantic-model", "sales_catalog")!;
        Assert.Equal(2, v2.Format);
        Assert.Contains("region_access", v2.Content);
        Assert.Contains("by_region", v2.Content);

        // the same rules again are no new version; a changed rule is one, with another hash
        var again = await PutAsync(AccessCatalog(), 2);
        Assert.Equal(2, (int)(await BodyAsync(again))["version"]!);
        var changed = await PutAsync(AccessCatalog(effect: "exclude"), 2);
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        var v3 = store.Current("semantic-model", "sales_catalog")!;
        Assert.Equal(3, v3.Version);
        Assert.NotEqual(v2.Hash, v3.Hash);

        // a rollback returns the rules of the version before, with that version
        var back = await CallAsAsync(build1, publisher, HttpMethod.Post, "/api/v1/semantic-models/sales_catalog/rollback", ServerRole.Publisher);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var now = store.Current("semantic-model", "sales_catalog")!;
        Assert.Equal((2, v2.Hash), (now.Version, now.Hash));
        Assert.Contains("effect: include", (string)(await BodyAsync(await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)))["catalog"]!);
    }

    [Fact]
    public async Task A_rules_table_the_data_source_cannot_read_is_refused_naming_it_and_nothing_is_stored()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        // the data source has the tables of the plain catalog only: no rules table
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(CatalogText, "sales-ro"), null));
        var store = app.Services.GetRequiredService<DefinitionStore>();

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(AccessCatalog(), "sales-ro"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = (string)(await BodyAsync(refused))["error"]!;
        Assert.Contains("region_access", message);
        Assert.Null(store.Current("semantic-model", "sales_catalog"));

        // a rule over a table the catalog does not have is refused by the loader, with the file's words
        var unknown = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(AccessCatalog(rulesTable: false), "sales-ro"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("DBD-ACC-003", (string)(await BodyAsync(unknown))["detail"]!);
        Assert.Null(store.Current("semantic-model", "sales_catalog"));
    }

    [Fact]
    public async Task A_catalog_with_access_rules_is_refused_while_a_live_instance_reads_only_format_1()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(AccessCatalog(), "sales-ro"), null));
        var store = app.Services.GetRequiredService<DefinitionStore>();
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(AccessCatalog(access: false), "sales-ro"));

        var state = app.Services.GetRequiredService<StateDatabase>();
        using (var connection = state.OpenConnection())
        {
            using var insert = state.Command("serving", connection, "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at, software_version, module_versions, definition_formats) VALUES ($key, $host, $pid, $at, $at, $software, $modules, $formats);");
            insert.Bind(state, "key", "older-instance").Bind(state, "host", "other-host").Bind(state, "pid", 7).Bind(state, "at", DateTimeOffset.UtcNow)
                .Bind(state, "software", "2026.9.1.0900").Bind(state, "modules", "serving=4").Bind(state, "formats", "semantic-model=1-1");
            insert.ExecuteNonQuery();
        }

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(AccessCatalog(), "sales-ro", expected: 1));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (string)(await BodyAsync(refused))["error"]!;
        Assert.Contains("older-instance", message);
        Assert.Contains("format 2", message);
        Assert.Equal(1, store.Current("semantic-model", "sales_catalog")!.Version);
    }

    [Fact]
    public async Task The_shared_publish_api_checks_and_stores_the_rules_as_the_route_does()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(AccessCatalog(), "sales-ro"), null));
        var store = app.Services.GetRequiredService<DefinitionStore>();

        var bad = await DefineAsync(build1, publisher, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(AccessCatalog().Replace("table: region_access", "table: elsewhere"), "build-1", "sales-ro")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Null(store.Current("semantic-model", "sales_catalog"));

        var good = await DefineAsync(build1, publisher, "semantic-model", "sales_catalog", DefinitionBody(ModelManifest(AccessCatalog(), "build-1", "sales-ro")));
        Assert.True(good.Status == HttpStatusCode.OK, good.Body.ToJsonString());
        Assert.Contains("region_access", (string)good.Body["warnings"]![0]!);
        var version = store.Current("semantic-model", "sales_catalog")!;
        Assert.Equal(2, version.Format);
        Assert.Contains("by_region", version.Content);
    }

    [Fact]
    public async Task A_catalog_with_checks_is_refused_while_a_live_instance_reads_only_format_1_and_goes_through_when_it_reads_2()
    {
        // the done-when of plan 23a FQ05, with the same mechanism as the rules (UC01)
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(CatalogText, "sales-ro"), null));
        var store = app.Services.GetRequiredService<DefinitionStore>();
        var withChecks = CatalogText.TrimEnd() + "\nchecks:\n  - {id: fresh, kind: freshness, dataset: fact_sales, column: ordered_at, max_age: 1d}\n";
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));

        var state = app.Services.GetRequiredService<StateDatabase>();
        using (var connection = state.OpenConnection())
        {
            using var insert = state.Command("serving", connection, "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at, software_version, module_versions, definition_formats) VALUES ($key, $host, $pid, $at, $at, $software, $modules, $formats);");
            insert.Bind(state, "key", "older-instance").Bind(state, "host", "other-host").Bind(state, "pid", 7).Bind(state, "at", DateTimeOffset.UtcNow)
                .Bind(state, "software", "2026.9.1.0900").Bind(state, "modules", "serving=4").Bind(state, "formats", "semantic-model=1-1");
            insert.ExecuteNonQuery();
        }

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(withChecks, "sales-ro", expected: 1));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("older-instance", (string)(await BodyAsync(refused))["error"]!);
        Assert.Equal(1, store.Current("semantic-model", "sales_catalog")!.Version);

        using (var connection = state.OpenConnection())
        {
            using var update = state.Command("serving", connection, "UPDATE {table:serving_instance} SET definition_formats = $formats WHERE instance_key = 'older-instance';");
            update.Bind(state, "formats", "semantic-model=1-2").ExecuteNonQuery();
        }

        var accepted = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(withChecks, "sales-ro", expected: 1));
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        Assert.Equal((2, 2), (store.Current("semantic-model", "sales_catalog")!.Version, store.Current("semantic-model", "sales_catalog")!.Format));
        Assert.Contains("fresh", store.Current("semantic-model", "sales_catalog")!.Content);
    }
}
