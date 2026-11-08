using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.State;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

public sealed partial class ManagementTests
{
    private static string CatalogText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));

    private static string Publish(string catalog, string source, int? expected = null, object? ssas = null) =>
        JsonSerializer(new { catalog, sources = new Dictionary<string, string> { ["sqlserver_sales"] = source }, ssas, expected_version = expected });

    /// <summary>A DuckDB file holding an empty table for every table the sample catalog's SQL source names, as the publish check needs.</summary>
    private string SalesData(string name)
    {
        var file = Path.Combine(dataDirectory, name + ".duckdb");
        var sales = CatalogLoader.LoadText(CatalogText).Doc.Sources.Where(s => DbDataKit.Serving.Semantic.SemanticModel.BaseSource(s.Key) == "sqlserver_sales").Select(s => s.Value);
        using var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file);
        connection.Open();
        foreach (var table in sales.SelectMany(s => s.Joins.Values.Select(j => j.Table).Append(s.Fact.Table)).Distinct())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE IF NOT EXISTS {table} (x INTEGER)";
            command.ExecuteNonQuery();
        }
        return Conn.Yaml(name, "duckdb", "Data Source=" + file);
    }

    private async Task<JsonNode> BodyAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_publishing_client_publishes_a_model_over_its_own_data_source_and_the_gateway_serves_it_at_once_and_rolls_it_back()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesData("sales-ro"), null));
        var models = app.Services.GetRequiredService<SemanticModels>();
        Assert.Null(models.Find("sales_catalog"));

        var first = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Assert.Equal(1, (int)(await BodyAsync(first))["version"]!);
        Assert.Equal("sales-ro", models.Find("semantic:sales_catalog")!.ServingSource("sqlserver_sales"));

        var second = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText.Replace("Governed sales", "Sales"), "sales-ro", expected: 1));
        Assert.Equal(2, (int)(await BodyAsync(second))["version"]!);
        var served = models.Find("sales_catalog")!;

        var read = await BodyAsync(await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher));
        Assert.Equal(2, (int)read["current_version"]!);
        Assert.Equal(2, read["versions"]!.AsArray().Count);

        var back = await CallAsAsync(build1, publisher, HttpMethod.Post, "/api/v1/semantic-models/sales_catalog/rollback", ServerRole.Publisher);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(1, (int)(await BodyAsync(back))["version"]!);
        Assert.NotSame(served, models.Find("sales_catalog"));
        Assert.Contains("Governed sales", (string)(await BodyAsync(await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)))["catalog"]!);

        // it is the group's: kept in the state, which every instance (and this one after a restart) serves from
        Assert.Equal(1, app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));
    }

    [Fact]
    public async Task A_catalog_with_a_cache_section_is_published_in_format_2_and_refused_while_a_live_instance_reads_only_format_1()
    {
        // plan 24a UC03 and UC01: the format of the package is 2 only when the catalog uses a section of it; the group never holds a version one of its instances cannot serve
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesData("sales-ro"), null));
        var store = app.Services.GetRequiredService<DbDataKit.State.DefinitionStore>();
        var withCache = CatalogText.TrimEnd() + "\ncache:\n  enabled: true\n  ttl: 10m\n";

        var plain = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(1, store.Current("semantic-model", "sales_catalog")!.Format);

        // an instance of the group that reads format 1 only (an older release), live: the publish is refused naming it, and the model stays as it was
        var state = app.Services.GetRequiredService<DbDataKit.State.StateDatabase>();
        using (var connection = state.OpenConnection())
        {
            using var insert = state.Command("serving", connection, "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at, software_version, module_versions, definition_formats) VALUES ($key, $host, $pid, $at, $at, $software, $modules, $formats);");
            insert.Bind(state, "key", "older-instance").Bind(state, "host", "other-host").Bind(state, "pid", 7).Bind(state, "at", DateTimeOffset.UtcNow)
                .Bind(state, "software", "2026.9.1.0900").Bind(state, "modules", "serving=4").Bind(state, "formats", "semantic-model=1-1");
            insert.ExecuteNonQuery();
        }

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(withCache, "sales-ro", expected: 1));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = (string)(await BodyAsync(refused))["error"]!;
        Assert.Contains("older-instance", message);
        Assert.Contains("other-host", message);
        Assert.Contains("2026.9.1.0900", message);
        Assert.Contains("format 2", message);
        Assert.Equal(1, store.Current("semantic-model", "sales_catalog")!.Version);

        // the instance is upgraded (it reads 1 to 2 now): the same publish goes through, in format 2, and the catalog's cache section is what the instance serves
        using (var connection = state.OpenConnection())
        {
            using var update = state.Command("serving", connection, "UPDATE {table:serving_instance} SET definition_formats = $formats WHERE instance_key = 'older-instance';");
            update.Bind(state, "formats", "semantic-model=1-2").ExecuteNonQuery();
        }

        var accepted = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(withCache, "sales-ro", expected: 1));
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        Assert.Equal((2, 2), (store.Current("semantic-model", "sales_catalog")!.Version, store.Current("semantic-model", "sales_catalog")!.Format));
        Assert.True(app.Services.GetRequiredService<SemanticModels>().Find("sales_catalog") is not null);
    }

    [Fact]
    public async Task A_publish_that_is_invalid_stale_or_aimed_at_data_it_may_not_reach_leaves_the_serving_version_alone()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesData("sales-ro"), null));
        var models = app.Services.GetRequiredService<SemanticModels>();
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));

        async Task<HttpStatusCode> Try(string body, string name = "sales_catalog") =>
            (await CallAsAsync(build1, publisher, HttpMethod.Put, $"/api/v1/semantic-models/{name}", ServerRole.Publisher, body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish("not: [a catalog", "sales-ro", 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro"), "other_name"));                  // the catalog's name is the call's name
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "no-such-source", 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "by-hand", 1)));                                  // not made by this client
        Assert.Equal(HttpStatusCode.Conflict, await Try(Publish(CatalogText, "sales-ro", 7)));                                    // stale
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText.Replace("fact_sales", "no_such_table"), "sales-ro", 1)));  // a table the data source lacks
        if (!OperatingSystem.IsWindows())
            Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro", 1, new { ssas_finance = "secret:x" })));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro", 1, new { ssas_finance = "Data Source=x;Password=literal" })));

        Assert.Equal(1, app.Services.GetRequiredService<DbDataGateway.Server.Serving.PublishedSemanticModels>().Current("sales_catalog"));
        Assert.Equal("sales-ro", models.Find("sales_catalog")!.ServingSource("sqlserver_sales"));
    }

    [Fact]
    public async Task One_publishing_client_cannot_see_replace_or_remove_another_clients_model_and_an_administrator_can_remove_any()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesData("sales-ro"), null));
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/sales-ro2", ServerRole.Publisher, Put(SalesData("sales-ro2"), null));
        await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));

        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro2", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Post, "/api/v1/semantic-models/sales_catalog/rollback", ServerRole.Publisher)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Delete, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)).StatusCode);
        Assert.Empty((await BodyAsync(await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/semantic-models", ServerRole.Publisher)))["models"]!.AsArray());

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Delete, "/api/v1/semantic-models/sales_catalog", ServerRole.Admin)).StatusCode);
        Assert.Null(app.Services.GetRequiredService<SemanticModels>().Find("sales_catalog"));
    }
}
