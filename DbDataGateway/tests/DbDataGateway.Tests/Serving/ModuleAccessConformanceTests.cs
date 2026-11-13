using DbDataKit.Serving.Conformance;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 22a GP14 and 24a UC15 on the real Gateway, on SQL Server and PostgreSQL: the same four people (three in a rules group each, one in none) and the same questions (revenue by region, the total, the members of an attribute, orders by a column the
/// column rules hide or mask) are put to the serving core and to every module that reaches data through it (Flight SQL as a semantic request and as semantic SQL, the semantic REST and feeds as a request, as semantic SQL and as a saved query, an app's semantic
/// call, the <c>mcp</c> read tool), and every answer must equal the core's. The rules change between runs (a row in the rules table and the producer's <c>data.changed</c>, a new version of the model with another column rule), and the whole run is made with the
/// result cache off, per caller and per rules, each question asked of each person twice in opposite orders, so what one person's request left in the cache shows when the next asks. The negative modules (one with its own connection to the database, one with a
/// cache of its own whose key leaves the person out) fail the same suite.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ModuleAccessConformanceTests
{
    public static IEnumerable<object[]> Setups => new[] { "mssql", "postgres" }.SelectMany(engine => new[] { "rules", "caller", "off" }.Select(cache => new object[] { engine, cache }));

    public static IEnumerable<object[]> Engines => [["mssql"], ["postgres"]];

    private static readonly string[] People = ["ada", "bea", "cy", "dee"];

    private static IReadOnlyList<ConformanceQuestion> Questions => [.. ModuleAccessRig.Asks.Select(ModuleAccessRig.Question)];

    private static IReadOnlyList<IConformanceModule> Modules(ModuleAccessRig rig) =>
    [
        new FlightModule(rig, sql: false), new FlightModule(rig, sql: true), new FeedsModule(rig, "request"), new FeedsModule(rig, "sql"), new FeedsModule(rig, "saved"), new AppsSemanticModule(rig), new McpSemanticModule(rig), new ViewsModule(rig),
    ];

    [Theory, MemberData(nameof(Setups))]
    public async Task Every_module_gives_each_person_the_rows_the_core_gives_them_before_and_after_the_rules_change(string engine, string cache)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, cache);
        var core = new CoreModule(rig);
        var suite = new ModuleAccessSuite(core, Modules(rig), People, Questions);

        // the rules as the first phase has them (the oracle: what the rules table gives each person, written out, so the core is not only compared with itself)
        Assert.Equal(["East|200|2"], (await core.AskAsync("ada", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        Assert.Equal(["East|300|3", "West|300|3"], (await core.AskAsync("bea", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        Assert.Equal(["NULL|40|1"], (await core.AskAsync("cy", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        Assert.Empty((await core.AskAsync("dee", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        Assert.Equal(["********|2"], (await core.AskAsync("ada", ModuleAccessRig.Question(ModuleAccessRig.ByCustomer))).Rows);                 // a fixed mask
        Assert.True((await core.AskAsync("cy", ModuleAccessRig.Question(ModuleAccessRig.ByCustomer))).Refused);                                    // a hidden column is unknown
        var report = await suite.RunAsync("initial");

        // the producer refreshed the rules table: east now also sees West
        rig.Execute("INSERT INTO region_access VALUES ('east', 'West')");
        Assert.True(rig.Refresh("region_access") >= 0);
        Assert.Equal(["East|200|2", "West|220|2"], (await core.AskAsync("ada", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        await suite.RunAsync("rules table changed", report);

        // a person is added to a group the rules table names: Dee, who was in none, sees what east sees from then on (the groups are read from the identity home for every request)
        DbDataKit.State.StateDatabase state = rig.Get<DbDataKit.State.StateDatabase>();
        Support.HomeSeed.Join(state, rig.People["dee"].UserId, "east");
        Assert.Equal(["East|200|2", "West|220|2"], (await core.AskAsync("dee", ModuleAccessRig.Question(ModuleAccessRig.ByRegion))).Rows);
        await suite.RunAsync("a person joined a group", report);

        // a new version of the model: east no longer sees the customer at all
        rig.Publish(ModuleAccessRig.CatalogText(cache, [.. ModuleAccessRig.ColumnRules, "{ subject: east, dataset: dim_customer, column: customer, treatment: hide }"]));
        Assert.True((await core.AskAsync("ada", ModuleAccessRig.Question(ModuleAccessRig.ByCustomer))).Refused);
        await suite.RunAsync("column rule published", report);

        Assert.True(report.Passed, report.Describe());
        Assert.Contains("flightsql (semantic request)/members", report.Asked);
        Assert.Contains("feeds (saved)/by-region", report.Asked);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_module_that_reads_the_data_source_through_its_own_connection_fails_the_suite(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine);
        var report = await new ModuleAccessSuite(new CoreModule(rig), [new DirectSourceModule(rig)], People, Questions).RunAsync("initial");

        Assert.False(report.Passed);
        // every person a rule narrows gets more through it than through the core: the rules were never applied
        Assert.All(["ada", "bea", "cy", "dee"], person => Assert.Contains(report.Violations, v => v.Person == person && v.Module == "direct-source"));
        Assert.Contains(report.Violations, v => v.Person == "dee" && v.Question == "by-region" && v.Expected.Rows.Count == 0 && v.Actual.Rows.Count > 0);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_module_with_a_cache_of_its_own_that_leaves_the_person_out_of_its_key_fails_the_suite(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine);
        var report = await new ModuleAccessSuite(new CoreModule(rig), [new NaiveCachingModule(new FeedsModule(rig, "request"))], People, Questions).RunAsync("initial");

        Assert.False(report.Passed);
        Assert.Contains(report.Violations, v => v.Person != "ada" && v.Module.StartsWith("naive-cache", StringComparison.Ordinal));
    }

    /// <summary>Whether this branch has plan 22a GP12's index of ruled tables (<c>RuledTables</c>): the pass-through case below holds the Flight SQL module to it once it does.</summary>
    private static bool HasPassThroughGuard => typeof(DbDataGateway.Server.Hosting.GatewayHost).Assembly.GetType("DbDataGateway.Server.Serving.RuledTables") is not null;

    [Theory, MemberData(nameof(Engines))]
    public async Task Flight_sql_pass_through_to_a_table_the_models_rules_cover_is_refused_and_a_table_with_no_rule_is_read(string engine)
    {
        Assert.SkipUnless(HasPassThroughGuard, "plan 22a GP12 (pass-through SQL refusal) is not in this branch: until it is, a statement to a data source reads a ruled table in full; remove this check when GP12 is merged");
        await using var rig = await ModuleAccessRig.StartAsync(engine);
        rig.Execute("CREATE TABLE plain_notes (id INT, note VARCHAR(20))");
        rig.Execute("INSERT INTO plain_notes VALUES (1, 'free')");
        var pass = new PassThroughModule(rig);

        foreach (var person in People)
        {
            var refused = await pass.AskAsync(person, ModuleAccessRig.Question(ModuleAccessRig.ByRegion));
            Assert.True(refused.Refused, $"{person} read the ruled table dim_customer through a statement to the data source: {refused}");
            Assert.Contains("orders_model", refused.Reason);
        }

        Assert.Equal(["1|free"], (await pass.AskAsync("ada", new ConformanceQuestion("plain", "a table no rule covers"))).Rows);
    }
}
