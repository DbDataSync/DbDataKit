using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Conformance;
using DbDataKit.Serving.Semantic;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Config;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// Plan 24a UC15, the serving cache's safety on the real Gateway, on SQL Server and PostgreSQL: with the cache on (per rules and per caller), two callers with different rules never share an answer, callers with the same effective rules share one only in scope
/// <c>rules</c>, a refresh of the rules table (the producer's <c>data.changed</c>) or a new version of the model with other rules leaves no entry of the old rules to be served, a shared cache with rules is refused when the model is published, and every module's answers
/// equal the core's with the cache cold, warm and off (<see cref="ModuleAccessConformanceTests"/> runs the module matrix in each of the three settings, and fails the negative module whose cache key leaves the person out). The module with a cache of its own, the views
/// module's, is held to the same: its entries go with the rules.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class CacheSafetyConformanceTests
{
    public static IEnumerable<object[]> Setups => new[] { "mssql", "postgres" }.SelectMany(engine => new[] { "rules", "caller" }.Select(cache => new object[] { engine, cache }));

    public static IEnumerable<object[]> Engines => [["mssql"], ["postgres"]];

    private static async Task<(string Cache, string Rows)> Ask(ModuleAccessRig rig, string person, ModuleAccessRig.Ask ask = null!)
    {
        ask ??= ModuleAccessRig.ByRegion;
        await using var answer = await rig.Get<IServingServices>().Queries.RunAsync(rig.ContextOf(person), new ServingRequest.Semantic(SemanticModels.SourcePrefix + ModuleAccessRig.Model, ask.Json));
        var cache = answer.Provenance.Cache ?? "miss";
        var rows = string.Join(";", (await ModuleAccessRig.RowsOf(answer)).Rows);
        return (cache, rows);
    }

    [Theory, MemberData(nameof(Setups))]
    public async Task Callers_with_different_rules_never_share_an_entry_and_callers_with_the_same_rules_share_one_only_in_scope_rules(string engine, string cache)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, cache);

        Assert.Equal(("miss", "East|200|2"), await Ask(rig, "ada"));
        Assert.Equal(("hit", "East|200|2"), await Ask(rig, "ada"));                  // the same caller is answered from the cache
        Assert.Equal(("miss", "East|300|3;West|300|3"), await Ask(rig, "bea"));      // other rules: never ada's entry
        Assert.Equal(("miss", ""), await Ask(rig, "dee"));                           // no rule row: not bea's, not ada's
        Assert.Equal(("hit", "East|300|3;West|300|3"), await Ask(rig, "bea"));

        // a person in the same group as ada has the same effective rules, and so the same fingerprint: scope rules shares the entry, scope caller never does
        Support.HomeSeed.Join(rig.Get<DbDataKit.State.StateDatabase>(), rig.People["dee"].UserId, "east");
        Assert.Equal((cache == "rules" ? "hit" : "miss", "East|200|2"), await Ask(rig, "dee"));
    }

    [Theory, MemberData(nameof(Setups))]
    public async Task A_refresh_of_the_rules_table_and_a_new_version_of_the_model_leave_no_entry_of_the_old_rules(string engine, string cache)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, cache);
        Assert.Equal(("miss", "East|200|2"), await Ask(rig, "ada"));
        Assert.Equal(("hit", "East|200|2"), await Ask(rig, "ada"));

        // a rule row is added and the producer says so: the entry that read the table is gone, the next answer is the new rules' and is cached in its turn
        rig.Execute("INSERT INTO region_access VALUES ('east', 'West')");
        Assert.True(rig.Refresh("region_access") >= 1);
        Assert.Equal(("miss", "East|200|2;West|220|2"), await Ask(rig, "ada"));
        Assert.Equal(("hit", "East|200|2;West|220|2"), await Ask(rig, "ada"));

        // a new version of the model with another rule on the column: another key, so the old entry cannot answer, whether or not anything cleared it
        Assert.Equal(("miss", "********|4"), await Ask(rig, "ada", ModuleAccessRig.ByCustomer));
        Assert.Equal(("hit", "********|4"), await Ask(rig, "ada", ModuleAccessRig.ByCustomer));
        rig.Publish(ModuleAccessRig.CatalogText(cache, [.. ModuleAccessRig.ColumnRules.Skip(1), "{ subject: east, dataset: dim_customer, column: customer, treatment: mask, mask: hash }"]));
        var hashed = await Ask(rig, "ada", ModuleAccessRig.ByCustomer);
        Assert.Equal("miss", hashed.Cache);
        Assert.Matches("^(#[0-9a-f]{16}\\|[0-9]+;?)+$", hashed.Rows);       // every customer a hash of its name, in place of the mask the old version gave
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_shared_scope_with_rules_is_refused_when_the_model_is_published(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, "rules");

        var refused = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(ModuleAccessRig.CatalogText("shared"), "orders.catalog.yaml"));
        Assert.Contains(refused.Errors, e => e.Message.Contains("DBD-ACC-009", StringComparison.Ordinal));
        // the rules the model had are still served
        Assert.Equal(("miss", "East|200|2"), await Ask(rig, "ada"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_views_modules_own_result_cache_still_answers_a_repeat_and_goes_with_the_rules(string engine)
    {
        await using var rig = await ModuleAccessRig.StartAsync(engine, "off");
        var cache = rig.Get<GatewayViews>().Module.Api!.Cache!;
        var views = new ViewsModule(rig);
        var question = ModuleAccessRig.Question(ModuleAccessRig.ByRegion);

        Assert.Equal(["East|200|2"], (await views.AskAsync("ada", question)).Rows);
        var hits = cache.Hits;
        Assert.Equal(["East|200|2"], (await views.AskAsync("ada", question)).Rows);
        Assert.True(cache.Hits > hits, "the module's cache is still used for a repeat");

        // nothing that decides the rows changed: still a hit. Then a rules table is refreshed, a model published and a cache cleared by hand: each is a miss, and the rows are the new rules'
        rig.Execute("INSERT INTO region_access VALUES ('east', 'West')");
        rig.Refresh("region_access");
        hits = cache.Hits;
        Assert.Equal(["East|200|2", "West|220|2"], (await views.AskAsync("ada", question)).Rows);
        Assert.Equal(hits, cache.Hits);

        var again = cache.Hits;
        await views.AskAsync("ada", question);
        Assert.True(cache.Hits > again);
        rig.Publish(ModuleAccessRig.CatalogText("off", "{ subject: east, dataset: dim_customer, column: customer, treatment: hide }"));
        Assert.True((await views.AskAsync("ada", ModuleAccessRig.Question(ModuleAccessRig.ByCustomer))).Refused);

        again = cache.Hits;
        await views.AskAsync("ada", question);
        Assert.True(cache.Hits > again);
        rig.Get<GatewayCache>().Invalidator.ClearByHand(null, "test");
        again = cache.Hits;
        await views.AskAsync("ada", question);
        Assert.Equal(again, cache.Hits);
    }

    // ---- a rules table past its maximum age, with the cache warm (a clock the test moves) ----

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor(DbDataGateway.Server.Configuration.GatewayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<DbDataGateway.Server.Configuration.GatewayOptions>
    {
        public DbDataGateway.Server.Configuration.GatewayOptions CurrentValue => value;

        public DbDataGateway.Server.Configuration.GatewayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<DbDataGateway.Server.Configuration.GatewayOptions, string?> listener) => null;
    }

    private sealed class Quiet : DbDataKit.Web.Trust.IAuditLog
    {
        public void Append(DbDataKit.Web.Trust.AuditEntry entry) { }

        public IReadOnlyList<DbDataKit.Web.Trust.AuditEntry> Read(int limit = 100) => [];
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task An_entry_made_while_the_rules_table_was_fresh_is_not_served_once_the_table_is_past_its_maximum_age_and_a_refresh_serves_again(string engine)
    {
        using var scratch = ScratchStateDatabase.For(engine);
        var connection = scratch.Connection.Replace("env:" + scratch.PasswordVariable, scratch.Environment(scratch.PasswordVariable)) + (engine == "postgres" ? ";SSL Mode=Disable" : "");
        var dir = Directory.CreateTempSubdirectory("dbgateway-cache-age-").FullName;
        try
        {
            var options = new DbDataGateway.Server.Configuration.GatewayOptions();
            options.DataSources["warehouse"] = new DbDataGateway.Server.Configuration.DataSourceOptions { Provider = engine == "mssql" ? "sqlserver" : "postgres", ConnectionString = connection, Everyone = true };
            var clock = new Clock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
            var monitor = new Monitor(options);
            var sources = new DbDataGateway.Server.Data.DataSourceCatalog(monitor, DbDataGateway.Server.Data.GatewaySources.Providers());
            var state = DbDataGateway.Server.State.GatewayState.Open(dir);
            var models = new SemanticModels();
            models.Add(CatalogLoader.LoadText(ModuleAccessRig.CatalogText("rules"), "orders.catalog.yaml"));
            var access = new GatewayAccess(sources, () => options, () => models, new DbDataGateway.Server.State.RulesTableLedger(state, clock),
                (source, physical) => DbDataGateway.Server.Management.GatewayLineageGraph.DatasetIdentity(options.DataSources, (_, text) => text, source, physical), new Quiet(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, clock);
            models.Access = access;
            var cache = new GatewayCache(sources, () => options, () => models, null, clock, access: access);
            cache.Resolve = new DbDataGateway.Server.Management.GatewayCacheManagement(cache, () => null, "gw", access: access).Resolve;
            var serving = new GatewayServing(sources, new DbDataGateway.Server.Auth.ChainedCredentialValidator([]), null!, null!, monitor, Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayServing>.Instance, clock).WithSemantic(models).WithCache(cache.Store, cache.Policy);
            foreach (var statement in ModuleAccessRig.Seed)
            {
                System.Data.Common.DbConnection c = engine == "mssql" ? new Microsoft.Data.SqlClient.SqlConnection(connection) : new Npgsql.NpgsqlConnection(connection);
                using (c) { c.Open(); using var command = c.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery(); }
            }

            var east = new ServingContext(new GatewayCaller(new DbDataGateway.Server.Auth.GatewayPrincipal("u1", "home-user", [], null, null, "u1", ["east"], "u1@example.invalid")));
            async Task<(string, string)> Run()
            {
                await using var answer = await serving.Queries.RunAsync(east, new ServingRequest.Semantic(SemanticModels.SourcePrefix + ModuleAccessRig.Model, ModuleAccessRig.ByRegion.Json));
                return (answer.Provenance.Cache ?? "miss", string.Join(";", (await ModuleAccessRig.RowsOf(answer)).Rows));
            }

            Assert.Equal(("miss", "East|200|2"), await Run());
            Assert.Equal(("hit", "East|200|2"), await Run());
            clock.Now += TimeSpan.FromMinutes(40);   // past half of the hour: the last good rules are served, and the entry made under them
            Assert.Equal("East|200|2", (await Run()).Item2);
            clock.Now += TimeSpan.FromMinutes(30);   // past the hour: the dataset is refused to everyone it has rules for, and no entry made before answers instead
            var refused = await Assert.ThrowsAsync<ServingDeniedException>(Run);
            Assert.Contains("is not served", refused.Message);
            Assert.Equal(("miss", "East|200|2"), await Refreshed());

            async Task<(string, string)> Refreshed()
            {
                foreach (var table in new[] { "region_access", "status_access", "blocked_status" })
                {
                    var notice = new DataChangedNotice(DbDataGateway.Server.Management.GatewayLineageGraph.DatasetIdentity(options.DataSources, (_, text) => text, "warehouse", table)!, "build", clock.Now);
                    access.Changed(notice);
                    cache.Invalidator.Apply(notice);
                }

                return await Run();
            }
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestFolder.Delete(dir);
        }
    }
}
