using DbDataKit.Serving.Conformance;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 22a GP14 for the Store half, on SQL Server and PostgreSQL: the rows of an entity of a published Store model, with row rules from two rules tables and column rules on an attribute, are the same through the Store API, the <c>store</c> module's listener, an
/// app's data call and the <c>mcp</c> read tool, and each equals what the rules give the person (written out in <see cref="StoreOracle"/>, because the Store has no serving core to ask). The rules change between runs: a row in the rules table and the producer's
/// <c>data.changed</c>, a column rule published.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class StoreAccessConformanceTests
{
    public static IEnumerable<object[]> Engines => [["mssql"], ["postgres"]];

    private static readonly string[] People = ["ada", "bea", "cy", "dee"];

    [Theory, MemberData(nameof(Engines))]
    public async Task Every_module_that_reads_an_entity_gives_each_person_the_rows_and_columns_the_rules_give_them_before_and_after_the_rules_change(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, "off");
        var oracle = new StoreOracle(rig);
        await rig.PublishEntitiesAsync("{ subject: east, dataset: territory, column: region, treatment: mask }", "{ subject: nullers, dataset: territory, column: region, treatment: hide }");
        var suite = new ModuleAccessSuite(oracle, [new StoreApiModule(rig), new StoreHttpModule(rig), new AppsDataModule(rig), new McpEntityModule(rig)], People, [StoreOracle.Territory]);

        Assert.Equal(["E1|********", "E2|********"], (await oracle.AskAsync("ada", StoreOracle.Territory)).Rows);
        Assert.Equal(["X1"], (await oracle.AskAsync("cy", StoreOracle.Territory)).Rows);
        var report = await suite.RunAsync("initial");

        // the producer refreshed the rules table: east also reads West
        rig.Execute("INSERT INTO rules.region_access VALUES ('east', 'West')");
        oracle.RegionAccess.Add(("east", "West"));
        rig.RefreshEntityRules("rules.region_access");
        await suite.RunAsync("rules table changed", report);

        // a new version of the model: east does not see the region at all
        await rig.PublishEntitiesOnlyAsync("{ subject: east, dataset: territory, column: region, treatment: hide }", "{ subject: nullers, dataset: territory, column: region, treatment: hide }");
        oracle.Treatments["east"] = "hide";
        Assert.Equal(["E1", "E2", "W1"], (await oracle.AskAsync("ada", StoreOracle.Territory)).Rows);
        await suite.RunAsync("column rule published", report);

        Assert.True(report.Passed, report.Describe());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_get_and_the_schema_apply_the_column_rules_and_a_hash_mask_is_the_groups_keyed_hash(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, "off");
        await rig.PublishEntitiesAsync("{ subject: east, dataset: territory, column: region, treatment: \"null\" }", "{ subject: emea, dataset: territory, column: region, treatment: mask, mask: hash }", "{ subject: nullers, dataset: territory, column: region, treatment: hide }");
        var api = DbDataGateway.Server.Hosting.GatewayStoreModule.ApiOf(rig.Get<Microsoft.Extensions.Options.IOptionsMonitor<DbDataGateway.Server.Configuration.GatewayOptions>>(), rig.Get<DbDataKit.State.StateDatabase>(), rig.Get<DbDataGateway.Server.Secrets.SecretsRuntime>(), rig.Get<PublishedEntityModels>());
        StoreCaller Who(string person) => new(rig.People[person].UserId, rig.People[person].Name, false, DbDataKit.Web.Trust.ServerRole.Viewer);
        static IReadOnlyDictionary<string, string> Key(string code, params (string, string)[] more) => new Dictionary<string, string>(more.ToDictionary(m => m.Item1, m => m.Item2)) { ["key.code"] = code };
        static System.Text.Json.Nodes.JsonObject Values(StoreAnswer answer) => (System.Text.Json.Nodes.JsonObject)answer.Body["values"]!;

        // east: the attribute is there as NULL; cy: not there at all; bea: a hash of the value, the same for the same value and another for another
        var asEast = Values(await api.RowAsync(Who("ada"), "sales", "territory", Key("E1"), CancellationToken.None));
        Assert.True(asEast.ContainsKey("region") && asEast["region"] is null);
        Assert.False(Values(await api.RowAsync(Who("cy"), "sales", "territory", Key("X1"), CancellationToken.None)).ContainsKey("region"));
        var e1 = (string)Values(await api.RowAsync(Who("bea"), "sales", "territory", Key("E1"), CancellationToken.None))["region"]!;
        var e2 = (string)Values(await api.RowAsync(Who("bea"), "sales", "territory", Key("E2"), CancellationToken.None))["region"]!;
        var w1 = (string)Values(await api.RowAsync(Who("bea"), "sales", "territory", Key("W1"), CancellationToken.None))["region"]!;
        Assert.Matches("^#[0-9a-f]{16}$", e1);
        Assert.Equal(e1, e2);
        Assert.NotEqual(e1, w1);
        Assert.DoesNotContain("East", e1);

        // the schema a person reads does not list an attribute their rules hide, and says what a treated one is
        var schema = api.Schema(Who("cy"), "sales").Body["entities"]!.AsArray().Single()!["attributes"]!.AsArray().Select(a => (string)a!["name"]!).ToArray();
        Assert.Equal(["code"], schema);
        var masked = api.Schema(Who("bea"), "sales").Body["entities"]!.AsArray().Single()!["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "region")!;
        Assert.Equal("mask:hash", (string)masked["access"]!);
        Assert.Equal(["code", "region"], api.Schema(Who("dee"), "sales").Body["entities"]!.AsArray().Single()!["attributes"]!.AsArray().Select(a => (string)a!["name"]!).ToArray());
    }
}
