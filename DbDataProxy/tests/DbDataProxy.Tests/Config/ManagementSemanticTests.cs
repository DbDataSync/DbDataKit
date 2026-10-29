using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataProxy.Tests.Config;

public sealed partial class ManagementTests
{
    private static string CatalogText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));

    private static string Publish(string catalog, string source, int? expected = null, object? ssas = null) =>
        JsonSerializer(new { catalog, sources = new Dictionary<string, string> { ["sqlserver_sales"] = source }, ssas, expected_version = expected });

    private async Task<JsonNode> BodyAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task A_publishing_client_publishes_a_model_over_its_own_data_source_and_the_proxy_serves_it_at_once_and_rolls_it_back()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(Plain("sales-ro"), null));
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

        // it survives a restart: the published models are read from the data directory when the server starts
        Assert.Equal(1, new DbDataProxy.Server.Serving.PublishedSemanticModels(dataDirectory).Current("sales_catalog"));
    }

    [Fact]
    public async Task A_publish_that_is_invalid_stale_or_aimed_at_data_it_may_not_reach_leaves_the_serving_version_alone()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(Plain("sales-ro"), null));
        var models = app.Services.GetRequiredService<SemanticModels>();
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(CatalogText, "sales-ro"));

        async Task<HttpStatusCode> Try(string body, string name = "sales_catalog") =>
            (await CallAsAsync(build1, publisher, HttpMethod.Put, $"/api/v1/semantic-models/{name}", ServerRole.Publisher, body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish("not: [a catalog", "sales-ro", 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro"), "other_name"));                  // the catalog's name is the call's name
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "no-such-source", 1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "by-hand", 1)));                                  // not made by this client
        Assert.Equal(HttpStatusCode.Conflict, await Try(Publish(CatalogText, "sales-ro", 7)));                                    // stale
        if (!OperatingSystem.IsWindows())
            Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro", 1, new { ssas_finance = "secret:x" })));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(Publish(CatalogText, "sales-ro", 1, new { ssas_finance = "Data Source=x;Password=literal" })));

        Assert.Equal(1, new DbDataProxy.Server.Serving.PublishedSemanticModels(dataDirectory).Current("sales_catalog"));
        Assert.Equal("sales-ro", models.Find("sales_catalog")!.ServingSource("sqlserver_sales"));
    }

    [Fact]
    public async Task One_publishing_client_cannot_see_replace_or_remove_another_clients_model_and_an_administrator_can_remove_any()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(Plain("sales-ro"), null));
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/sales-ro2", ServerRole.Publisher, Put(Plain("sales-ro2"), null));
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
