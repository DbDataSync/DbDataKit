using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 22a GP12 (owner decision 2026-11-12): a model that reads a table another published model puts access rules on, without rules of its own for it, is not refused; its publish answer carries a warning naming the other model and the table.
/// A model with rules for the table of its own (any rules) gets none, and a model that is removed puts no rules on anything.
/// </summary>
public sealed partial class ManagementTests
{
    private const string UnappliedWords = "Model 'sales_catalog' puts access rules on 'dim_customer' of data source 'sales-ro'; this model reads the same table and does not apply them.";

    private static string SecondCatalog(string name, bool access = false, string effect = "include") =>
        AccessCatalog(access: access, effect: effect).Replace("name: sales_catalog", "name: " + name, StringComparison.Ordinal);

    private static string CustomersModel(bool rules) =>
        "schemaVersion: 2\nname: customers\nstorage: {connection: sales-ro}\n"
        + (rules ? "access:\n  row:\n    - dataset: dim_customer\n      groups:\n        - id: g\n          rules:\n            - { id: r1, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }\n" : "")
        + "entities:\n  dim_customer:\n    key: [code]\n    attributes:\n      code: {type: VARCHAR(10), required: true}\n      region: {type: VARCHAR(20)}\n";

    private static IEnumerable<string> Warnings(JsonNode body) => body["warnings"] is JsonArray list ? list.Select(w => (string)w!) : [];

    [Fact]
    public async Task A_semantic_model_that_reads_a_table_another_model_puts_rules_on_is_published_with_a_warning_naming_that_model_and_the_table()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(AccessCatalog(), "sales-ro"), null));
        async Task<JsonNode> PutAsync(string name, string catalog, int? expected = null)
        {
            var answer = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/" + name, ServerRole.Publisher, Publish(catalog, "sales-ro", expected: expected));
            Assert.True(answer.StatusCode == HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
            return await BodyAsync(answer);
        }

        // no model has rules yet: nothing to say
        Assert.DoesNotContain(Warnings(await PutAsync("sales_plain", SecondCatalog("sales_plain"))), w => w.Contains("puts access rules", StringComparison.Ordinal));

        var holder = await PutAsync("sales_catalog", AccessCatalog());
        Assert.DoesNotContain(Warnings(holder), w => w.Contains("puts access rules", StringComparison.Ordinal));   // a model never warns about its own rules

        // the model without rules over the same table is published (not refused) and says whose rules it does not apply
        var without = await PutAsync("sales_plain", SecondCatalog("sales_plain"), expected: 1);
        Assert.Contains(UnappliedWords, Warnings(without));
        Assert.Single(Warnings(without), w => w.Contains("puts access rules", StringComparison.Ordinal));

        // rules of its own for the table (different ones) say nothing of this kind
        var own = await PutAsync("sales_plain", SecondCatalog("sales_plain", access: true, effect: "exclude"), expected: 1);
        Assert.DoesNotContain(Warnings(own), w => w.Contains("puts access rules", StringComparison.Ordinal));

        // the model with the rules is removed: no rules, no warning
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, publisher, HttpMethod.Delete, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)).StatusCode);
        var after = await PutAsync("sales_plain", SecondCatalog("sales_plain"), expected: 2);
        Assert.DoesNotContain(Warnings(after), w => w.Contains("puts access rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entity_model_that_reads_a_table_a_semantic_model_puts_rules_on_gets_the_same_warning_and_the_shared_publish_api_too()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/sales-ro", ServerRole.Publisher, Put(SalesDataFor(AccessCatalog(), "sales-ro"), null));
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher, Publish(AccessCatalog(), "sales-ro"))).StatusCode);

        async Task<JsonNode> PutAsync(string model, int? expected = null)
        {
            var answer = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/entity-models/customers", ServerRole.Publisher,
                JsonSerializer(new Dictionary<string, object?> { ["model"] = model, ["data_source"] = "sales-ro", ["expected_version"] = expected }));
            Assert.True(answer.StatusCode == HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
            return await BodyAsync(answer);
        }

        var without = await PutAsync(CustomersModel(rules: false));
        Assert.Contains(UnappliedWords, Warnings(without));

        var with = await PutAsync(CustomersModel(rules: true), expected: 1);
        Assert.DoesNotContain(Warnings(with), w => w.Contains("puts access rules", StringComparison.Ordinal));

        // through the shared publish API the answer is the same
        string Manifest(string model) => new JsonObject { ["model"] = model, ["dataSource"] = "sales-ro", ["managedBy"] = "build-1" }.ToJsonString();
        var shared = await DefineAsync(build1, publisher, "entity-model", "customers", DefinitionBody(Manifest(CustomersModel(rules: false)), expected: 2));
        Assert.True(shared.Status == HttpStatusCode.OK, shared.Body.ToJsonString());
        Assert.Contains(UnappliedWords, Warnings(shared.Body));

        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, publisher, HttpMethod.Delete, "/api/v1/semantic-models/sales_catalog", ServerRole.Publisher)).StatusCode);
        var after = await DefineAsync(build1, publisher, "entity-model", "customers", DefinitionBody(Manifest(CustomersModel(rules: false) + "# again\n"), expected: 3));
        Assert.True(after.Status == HttpStatusCode.OK, after.Body.ToJsonString());
        Assert.DoesNotContain(Warnings(after.Body), w => w.Contains("puts access rules", StringComparison.Ordinal));
    }
}
