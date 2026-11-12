using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Lineage;
using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Tests;

/// <summary>
/// Plan 24a UC13, UC14 and UC16 on the Gateway: the policy that reads a definition's <c>cache</c> section (off unless it says <c>enabled: true</c>; a pass-through source, a writable one, a caller without the grant and a preview are never cached), the
/// <c>data.changed</c> call that clears what reads a dataset (and everything when the Gateway cannot say), the console's counts and the clear call, the <c>serving_cache_epoch</c> module that keeps the instances of a group in step on SQLite, SQL Server
/// and PostgreSQL, and the data source file's <c>cache</c> section becoming the settings the Gateway runs on.
/// </summary>
public sealed class GatewayCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-cache-").FullName;
    private ScratchStateDatabase? _scratch;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    public static IEnumerable<object[]> Engines => [["sqlite"], ["mssql"], ["postgres"]];

    private StateDatabase Open(string engine)
    {
        if (engine == "sqlite") return GatewayState.Open(_dir);
        _scratch = ScratchStateDatabase.For(engine);
        var scratch = _scratch;
        return GatewayState.Open(_dir, new StateSettings(engine == "mssql" ? StateEngineIds.MsSql : "postgres", scratch.Connection), text => text.Replace("env:" + scratch.PasswordVariable, scratch.Environment(scratch.PasswordVariable)));
    }

    private sealed class Monitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private static GatewayOptions Options(params (string Name, DataSourceOptions Source)[] sources)
    {
        var options = new GatewayOptions();
        foreach (var (name, source) in sources) options.DataSources[name] = source;
        return options;
    }

    private static DataSourceOptions Source(bool? cache = null, string ttl = "5m", string scope = "caller", bool passThrough = false, bool writes = false, bool everyone = true, string? group = null) => new()
    {
        Provider = "sqlserver", ConnectionString = "Server=wh;Database=Sales;Integrated Security=true", Everyone = everyone, PassThrough = passThrough, AllowWrites = writes, Groups = group is null ? [] : [group],
        Cache = cache is null ? null : new DataSourceCacheOptions { Enabled = cache.Value, Ttl = ttl, Scope = scope },
    };

    private static GatewayCaller Person(string id = "u1", params string[] groups) => new(new GatewayPrincipal(id, "home-user", [], null, null, id, groups, id + "@x.invalid"));

    private static GatewayCachePolicy PolicyOf(GatewayOptions options, SemanticModels? models = null)
    {
        var monitor = new Monitor(options);
        var catalog = new DataSourceCatalog(monitor, GatewaySources.Providers());
        return new GatewayCachePolicy(catalog, () => options, () => models ?? new SemanticModels());
    }

    [Fact]
    public void The_cache_is_off_for_a_data_source_whose_definition_has_no_cache_section_or_says_no()
    {
        var policy = PolicyOf(Options(("none", Source()), ("no", Source(false))));
        var context = new ServingContext(Person());

        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("none", "select 1")));
        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("no", "select 1")));
        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("missing", "select 1")));
        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("", "select 1")));
        Assert.All(policy.Settings(), s => Assert.False(s.Enabled));
    }

    [Fact]
    public void A_data_source_that_turns_the_cache_on_gets_a_plan_with_its_time_to_live_scope_and_datasets_and_a_version_that_changes_with_the_file()
    {
        var policy = PolicyOf(Options(("erp", Source(true, "90s", "shared"))));
        var plan = policy.PlanFor(new ServingContext(Person()), new ServingRequest.Sql("erp", "select 1"))!;

        Assert.Equal((TimeSpan.FromSeconds(90), ServingCacheScope.Shared), (plan.Ttl, plan.Scope));
        Assert.Equal(["data-source/erp"], plan.Datasets);
        Assert.NotNull(plan.DefinitionVersion);
        var changed = PolicyOf(Options(("erp", Source(true, "2m", "shared")))).PlanFor(new ServingContext(Person()), new ServingRequest.Sql("erp", "select 1"))!;
        Assert.NotEqual(plan.DefinitionVersion, changed.DefinitionVersion);
        var key = ServingCacheKey.Build(new ServingContext(Person()), new ServingRequest.Sql("erp", "select 1"), plan);
        Assert.NotEqual(key, ServingCacheKey.Build(new ServingContext(Person()), new ServingRequest.Sql("erp", "select 1"), changed));
    }

    [Fact]
    public void A_pass_through_source_a_writable_one_a_preview_and_a_request_the_cache_cannot_key_are_never_cached_whatever_the_definition_says()
    {
        var policy = PolicyOf(Options(("passes", Source(true, passThrough: true)), ("writes", Source(true, writes: true)), ("erp", Source(true))));
        var context = new ServingContext(Person());

        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("passes", "select 1")));
        Assert.Null(policy.PlanFor(context, new ServingRequest.Sql("writes", "select 1")));
        Assert.Null(policy.PlanFor(context with { Preview = "draft-1" }, new ServingRequest.Sql("erp", "select 1")));
        Assert.Null(policy.PlanFor(context, new ServingRequest.Mdx("erp", "select")));
        Assert.Null(policy.PlanFor(new ServingContext(new ServingCaller("x", "x")), new ServingRequest.Sql("erp", "select 1")));   // not a caller this Gateway identified
        Assert.Contains("pass-through", policy.Settings().Single(s => s.Name == "passes").Note);
        Assert.Contains("accepts writes", policy.Settings().Single(s => s.Name == "writes").Note);
        Assert.NotNull(policy.PlanFor(context, new ServingRequest.Sql("erp", "select 1")));
    }

    [Fact]
    public void A_caller_who_may_not_use_the_data_source_gets_no_plan_so_an_entry_is_never_a_way_round_a_grant()
    {
        var policy = PolicyOf(Options(("erp", Source(true, everyone: false, group: "finance"))));
        var request = new ServingRequest.Sql("erp", "select 1");

        Assert.NotNull(policy.PlanFor(new ServingContext(Person("a", "finance")), request));
        Assert.Null(policy.PlanFor(new ServingContext(Person("b", "sales")), request));
        Assert.Null(policy.PlanFor(new ServingContext(Person("c")), request));
    }

    private static SemanticModels Models(string cache, IReadOnlyDictionary<string, string>? sources = null, IReadOnlyDictionary<string, string>? ssas = null)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml")).TrimEnd() + "\n" + cache + "\n";
        var models = new SemanticModels();
        models.Set(new SemanticModel(CatalogLoader.LoadText(text, "sales.catalog.yaml"), sources ?? new Dictionary<string, string>(), ssas) { Version = 4 });
        return models;
    }

    [Fact]
    public void The_plan_of_a_model_with_column_rules_holds_the_callers_treatments_so_callers_who_see_other_things_never_share_an_entry()
    {
        // plan 22a GP13: the key's access part is what the caller's groups do to the model's columns
        var models = Models("cache:\n  enabled: true\n  ttl: 10m\n  scope: rules\naccess:\n  columns:\n    - { subject: interns, dataset: dim_customer, column: customer, treatment: mask }\n    - { subject: auditors, dataset: dim_customer, column: customer, treatment: hide }",
            new Dictionary<string, string> { ["ssas_finance"] = "finance" });
        var request = new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + models.All.Single().Name, "SELECT 1");
        var policy = PolicyOf(Options(("sqlserver_sales", Source()), ("finance", Source())), models);
        string? Access(GatewayCaller caller) => policy.PlanFor(new ServingContext(caller), request)!.Access;

        Assert.Equal("-", Access(Person("a")));
        Assert.Equal(Access(Person("b", "interns")), Access(Person("c", "Interns", "staff")));
        Assert.NotEqual(Access(Person("a")), Access(Person("b", "interns")));
        Assert.NotEqual(Access(Person("b", "interns")), Access(Person("d", "auditors")));
        Assert.Equal(Access(Person("d", "auditors")), Access(Person("e", "interns", "auditors")));   // hide beats mask: the same treatment as the auditor alone
    }

    [Fact]
    public void A_semantic_model_is_cached_when_its_catalog_says_so_and_every_source_it_reads_may_be_shared()
    {
        // the sample model reads sqlserver_sales and, for its returns, ssas_finance, which this Gateway serves from a data source of its own called finance
        var models = Models("cache:\n  enabled: true\n  ttl: 10m\n  scope: rules", new Dictionary<string, string> { ["ssas_finance"] = "finance" });
        var name = models.All.Single().Name;
        var request = new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + name, "SELECT 1");
        var shared = PolicyOf(Options(("sqlserver_sales", Source()), ("finance", Source())), models);
        var plan = shared.PlanFor(new ServingContext(Person()), request)!;

        Assert.Equal((TimeSpan.FromMinutes(10), ServingCacheScope.Rules, "4"), (plan.Ttl, plan.Scope, plan.DefinitionVersion));
        Assert.Equal(["semantic-model/" + name, "data-source/sqlserver_sales", "data-source/finance"], plan.Datasets);
        Assert.Null(shared.PlanFor(new ServingContext(Person()), new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + name, "EXPLAIN SELECT 1")));
        Assert.Null(shared.PlanFor(new ServingContext(Person()), new ServingRequest.Semantic(SemanticModels.SourcePrefix + name, """{"kind":"describe"}""")));
        Assert.Null(PolicyOf(Options(("sqlserver_sales", Source()), ("finance", Source(passThrough: true))), models).PlanFor(new ServingContext(Person()), request));
        Assert.Null(PolicyOf(Options(("sqlserver_sales", Source(writes: true)), ("finance", Source())), models).PlanFor(new ServingContext(Person()), request));
        Assert.Null(PolicyOf(Options(("sqlserver_sales", Source()), ("finance", Source(everyone: false))), models).PlanFor(new ServingContext(Person()), request));
        Assert.Null(PolicyOf(Options(("sqlserver_sales", Source())), models).PlanFor(new ServingContext(Person()), request));   // finance is not served here
        var setting = shared.Settings().Single(s => s.Kind == "semantic-model");
        Assert.Equal((SemanticModels.SourcePrefix + name, true, null), (setting.Name, setting.Enabled, setting.Note));
    }

    [Fact]
    public void A_semantic_model_without_a_cache_section_is_not_cached_and_one_that_reads_analysis_services_says_why_it_is_not()
    {
        var off = Models("", new Dictionary<string, string> { ["ssas_finance"] = "finance" });
        Assert.Null(PolicyOf(Options(("sqlserver_sales", Source()), ("finance", Source())), off).PlanFor(new ServingContext(Person()), new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + off.All.Single().Name, "SELECT 1")));
        var ssas = Models("cache: {enabled: true}", ssas: new Dictionary<string, string> { ["ssas_finance"] = "env:x" });
        Assert.Contains("Analysis Services", PolicyOf(Options(("sqlserver_sales", Source())), ssas).Settings().Single(s => s.Kind == "semantic-model").Note);
    }

    private static LineageGraph Graph()
    {
        var table = "sqlserver://wh/sales.dbo.orders";
        var other = "sqlserver://wh/hr.dbo.people";
        return new LineageGraph(
            [
                new LineageNode(table, NodeKind.Dataset, "orders", false), new LineageNode(other, NodeKind.Dataset, "people", false),
                new LineageNode("suite://gw/data-source/erp", NodeKind.Serving, "erp", false), new LineageNode("suite://gw/data-source/hr", NodeKind.Serving, "hr", false),
                new LineageNode("suite://gw/semantic-model/sales", NodeKind.Serving, "sales", false),
            ],
            [
                new LineageEdge(table, "suite://gw/data-source/erp", EdgeKind.Serves, Granularity.Table, Confidence.Observed),
                new LineageEdge(other, "suite://gw/data-source/hr", EdgeKind.Serves, Granularity.Table, Confidence.Observed),
                new LineageEdge("suite://gw/data-source/erp", "suite://gw/semantic-model/sales", EdgeKind.Reads, Granularity.Table, Confidence.Derived),
            ]);
    }

    private (GatewayCacheManagement Api, GatewayCache Cache, FakeAnswers Inner, CachingServingQueries Queries) Make(Func<LineageGraph?>? graph = null, IServingCacheEpochs? epochs = null)
    {
        var options = Options(("erp", Source(true, scope: "shared")), ("hr", Source(true, scope: "shared")));
        var catalog = new DataSourceCatalog(new Monitor(options), GatewaySources.Providers());
        var cache = new GatewayCache(catalog, () => options, () => new SemanticModels(), epochs);
        var api = new GatewayCacheManagement(cache, graph ?? Graph, "gw", epochs: null);
        cache.Resolve = api.Resolve;
        var inner = new FakeAnswers();
        return (api, cache, inner, new CachingServingQueries(inner, cache.Store, cache.Policy));
    }

    private static async Task Ask(CachingServingQueries queries, string source)
    {
        await using var answer = await queries.RunAsync(new ServingContext(Person()), new ServingRequest.Sql(source, "select 1"));
        await foreach (var _ in answer.ReadBatchesAsync()) { }
    }

    private static ManagementCaller Root => ManagementCaller.Local("root");

    [Fact]
    public async Task A_data_changed_message_clears_what_reads_the_dataset_and_no_other_entry()
    {
        var (api, cache, inner, queries) = Make();
        await Ask(queries, "erp");
        await Ask(queries, "hr");
        await Ask(queries, "erp");
        Assert.Equal((2, 1L), (inner.Runs, cache.Store.Stats.Hits));

        var answer = api.Handle("POST", GatewayCacheManagement.Route + "/changed", DataChangedMessage.Write(new DataChangedNotice("sqlserver://wh/sales.dbo.orders", "sync", DateTimeOffset.UtcNow, "run-1")), Root);

        Assert.Equal(200, answer.Status);
        var body = (JsonObject)answer.Body!;
        Assert.Equal((1, false), ((int)body["cleared"]!, (bool)body["everything"]!));
        Assert.Equal(["data-source/erp", "semantic-model/sales"], body["datasets"]!.AsArray().Select(d => (string)d!).ToArray());
        Assert.Equal(1, cache.Store.Stats.Entries);
        await Ask(queries, "erp");
        await Ask(queries, "hr");
        Assert.Equal(3, inner.Runs);   // erp was asked again, hr was answered from the cache
    }

    [Fact]
    public async Task A_dataset_the_graph_does_not_know_clears_the_whole_cache_and_says_so()
    {
        var (api, cache, _, queries) = Make();
        await Ask(queries, "erp");
        await Ask(queries, "hr");

        var body = (JsonObject)api.Handle("POST", GatewayCacheManagement.Route + "/changed", DataChangedMessage.Write(new DataChangedNotice("mysql://x/unknown", "build", DateTimeOffset.UtcNow)), Root).Body!;

        Assert.Equal((2, true), ((int)body["cleared"]!, (bool)body["everything"]!));
        Assert.Equal(0, cache.Store.Stats.Entries);
        Assert.Equal(0, Make(() => null).Cache.Store.Stats.Entries);
        Assert.Null(Make(() => null).Api.Resolve("anything"));
    }

    [Fact]
    public void The_message_needs_the_publisher_or_admin_role_and_a_valid_body_and_carries_no_row_data()
    {
        var (api, _, _, _) = Make();
        var route = GatewayCacheManagement.Route + "/changed";

        Assert.Equal(400, api.Handle("POST", route, """{"message":"data.changed/1","dataset":"a/b","product":"sync","rows":[[1]]}""", Root).Status);
        Assert.Equal(400, api.Handle("POST", route, "nonsense", Root).Status);
        Assert.Equal(405, api.Handle("GET", route, null, Root).Status);
        Assert.Equal(404, api.Handle("GET", GatewayCacheManagement.Route + "/nothing", null, Root).Status);
        Assert.Equal(200, api.Handle("POST", route, """{"message":"data.changed/1","dataset":"a/b","product":"sync"}""", Root).Status);
    }

    [Fact]
    public async Task An_administrator_clears_one_dataset_or_all_and_asks_for_one_or_the_other()
    {
        var (api, cache, _, queries) = Make();
        await Ask(queries, "erp");
        await Ask(queries, "hr");
        var route = GatewayCacheManagement.Route + "/invalidate";

        Assert.Equal(400, api.Handle("POST", route, "{}", Root).Status);
        Assert.Equal(400, api.Handle("POST", route, """{"all":true,"dataset":"data-source/erp"}""", Root).Status);
        var one = (JsonObject)api.Handle("POST", route, """{"dataset":"data-source/erp"}""", Root).Body!;
        Assert.Equal(1, (int)one["cleared"]!);
        Assert.Equal(1, cache.Store.Stats.Entries);
        Assert.Equal(1, (int)((JsonObject)api.Handle("POST", route, """{"all":true}""", Root).Body!)["cleared"]!);
        Assert.Equal(0, cache.Store.Stats.Entries);
        Assert.Equal(2L, cache.Invalidator.Activity.Manual);
    }

    [Fact]
    public async Task The_cache_screen_gets_the_counts_by_instance_and_dataset_the_settings_and_what_the_cache_heard_of_changes()
    {
        var (api, _, _, queries) = Make();
        await Ask(queries, "erp");
        await Ask(queries, "erp");
        await Ask(queries, "erp");
        api.Handle("POST", GatewayCacheManagement.Route + "/changed", """{"message":"data.changed/1","dataset":"sqlserver://wh/sales.dbo.orders","product":"build"}""", Root);

        var body = (JsonObject)api.Handle("GET", GatewayCacheManagement.Route, null, Root).Body!;

        Assert.Equal(("gw", true), ((string)body["instance"]!, (bool)body["enabled"]!));
        var stats = (JsonObject)body["stats"]!;
        Assert.Equal((2L, 1L, 0), ((long)stats["hits"]!, (long)stats["misses"]!, (int)stats["entries"]!));
        Assert.Equal(0.6667, (double)stats["hit_rate"]!);
        var erp = body["settings"]!.AsArray().Select(s => (JsonObject)s!).Single(s => (string)s["name"]! == "erp");
        Assert.Equal((true, true, "shared", 300L), ((bool)erp["enabled"]!, (bool)erp["active"]!, (string)erp["scope"]!, (long)erp["ttl_seconds"]!));
        var events = (JsonObject)body["events"]!;
        Assert.Equal(("build", "sqlserver://wh/sales.dbo.orders", 1L, false), (((string)events["last_product"]!), (string)events["last_dataset"]!, (long)events["received"]!, (bool)events["shared"]!));
        var dataset = body["datasets"]!.AsArray().Select(d => (JsonObject)d!).Single(d => (string)d["name"]! == "data-source/erp");
        Assert.Equal((2L, 1L), ((long)dataset["hits"]!, (long)dataset["misses"]!));
    }

    [Fact]
    public async Task A_cache_that_is_dropped_gives_the_same_rows()
    {
        var (_, cache, inner, queries) = Make();
        var first = await Rows(queries);
        var warm = await Rows(queries);
        cache.Store.Clear();
        var cold = await Rows(queries);

        Assert.Equal(first, warm);
        Assert.Equal(first, cold);
        Assert.Equal(2, inner.Runs);

        static async Task<long> Rows(CachingServingQueries q)
        {
            await using var answer = await q.RunAsync(new ServingContext(Person()), new ServingRequest.Sql("erp", "select 1"));
            long n = 0;
            await foreach (var batch in answer.ReadBatchesAsync()) n += batch.Length;
            return n;
        }
    }

    [Fact]
    public void A_data_source_file_with_a_cache_section_becomes_the_settings_the_gateway_runs_on_and_a_file_without_one_has_none()
    {
        var withCache = Read("schema: connection/2\nname: erp\nengine: sqlserver\naddress: {host: wh, database: Sales}\nauth: {mode: integrated}\ncache: {enabled: true, ttl: 15m, scope: rules}\n");
        var without = Read("schema: connection/1\nname: erp\nengine: sqlserver\naddress: {host: wh, database: Sales}\nauth: {mode: integrated}\n");

        Assert.Equal("true", withCache["Gateway:DataSources:erp:Cache:Enabled"]);
        Assert.Equal("15m", withCache["Gateway:DataSources:erp:Cache:Ttl"]);
        Assert.Equal("rules", withCache["Gateway:DataSources:erp:Cache:Scope"]);
        Assert.DoesNotContain(without.Keys, k => k.Contains(":Cache:", StringComparison.Ordinal));

        Dictionary<string, string?> Read(string text)
        {
            var diagnostics = new List<DbDataKit.Config.Yaml.ConfigDiagnostic>();
            var values = new Dictionary<string, string?>();
            DbDataGateway.Server.Configuration.GatewayConnectionFile.Read("erp.yaml", "erp", text, diagnostics, (key, value, _) => values[key] = value);
            Assert.DoesNotContain(diagnostics, d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error);
            return values;
        }
    }

    [Fact]
    public void The_module_follows_the_conventions_holds_epochs_and_no_data_and_ages_out_after_the_longest_time_to_live()
    {
        Assert.Empty(StateConventions.Violations(ServingCacheEpochModule.Module));
        var rule = Assert.Single(ServingCacheEpochModule.Module.Retention);
        Assert.Equal(("cache_epoch", "changed_at"), (rule.Table, rule.TimeColumn));
        Assert.True(rule.DefaultDays * 24 > CacheSectionMaxHours);
        Assert.Equal((StateScopeKind.Group, "serving_cache_epoch", 2), (ServingCacheEpochModule.Module.Scope, ServingCacheEpochModule.Module.Name, ServingCacheEpochModule.Module.Version));
        Assert.Contains(ServingCacheEpochModule.Module, GatewayState.Modules);
        // the layout names a dataset, an epoch and who and why: nothing that could hold a row or a result
        Assert.DoesNotContain(ServingCacheEpochModule.Module.Migrations, m => m.Contains("BLOB", StringComparison.OrdinalIgnoreCase) || m.Contains("payload", StringComparison.OrdinalIgnoreCase) || m.Contains("answer", StringComparison.OrdinalIgnoreCase));
    }

    private const int CacheSectionMaxHours = 24;

    [Theory, MemberData(nameof(Engines))]
    public async Task Epochs_only_grow_and_two_instances_of_a_group_share_a_clear(string engine)
    {
        var state = Open(engine);
        var clock = new Clock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
        var store = new ServingCacheEpochStore(state, clock);
        var first = store.Bump("data-source/erp", "sync", "data.changed x");
        var second = store.Bump("data-source/erp", "build", "data.changed y");
        store.Bump("data-source/hr", "console", "cleared by ada");

        Assert.True(second > first);
        var read = store.Read();
        Assert.Equal(second, read["data-source/erp"]);
        Assert.Equal(2, read.Count);
        Assert.Equal("data-source/hr", store.Recent().First().Dataset);

        // two instances over one state: a change that reached the first clears the second at its next poll, and a cache of the second that holds another dataset keeps it
        var optionsOne = Options(("erp", Source(true, scope: "shared")), ("hr", Source(true, scope: "shared")));
        var one = new GatewayCache(new DataSourceCatalog(new Monitor(optionsOne), GatewaySources.Providers()), () => optionsOne, () => new SemanticModels(), new ServingCacheEpochStore(state));
        var two = new GatewayCache(new DataSourceCatalog(new Monitor(optionsOne), GatewaySources.Providers()), () => optionsOne, () => new SemanticModels(), new ServingCacheEpochStore(state));
        one.Resolve = d => ["data-source/erp"];
        two.Resolve = one.Resolve;
        var inner = new FakeAnswers();
        var queriesTwo = new CachingServingQueries(inner, two.Store, two.Policy);
        await Ask(queriesTwo, "erp");
        await Ask(queriesTwo, "hr");
        two.Invalidator.Poll();

        one.Invalidator.Apply(new DataChangedNotice("x/y", "sync", DateTimeOffset.UtcNow));

        Assert.Equal(1, two.Invalidator.Poll());
        Assert.Equal(1, two.Store.Stats.Entries);
        Assert.Equal(0, two.Invalidator.Poll());
    }

    /// <summary>A clock that moves a second with every look, so rows written one after the other are ordered by time.</summary>
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private sealed class FakeAnswers : IServingQueries
    {
        public int Runs;

        public Task<IServingAnswer> RunAsync(ServingContext context, ServingRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult<IServingAnswer>(new FakeAnswer(new ServingProvenance("select 1", null, context.Channel, request.Source)));
        }

        public Task<ServingStatementInfo> PrepareAsync(ServingContext context, ServingRequest.Sql statement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAnswer(ServingProvenance provenance) : IServingAnswer
    {
        private static readonly Schema Columns = new Schema.Builder().Field(new Field("n", Int32Type.Default, false)).Build();

        public ServingProvenance Provenance { get; } = provenance;

        public Schema Schema => Columns;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new RecordBatch(Columns, [new Int32Array.Builder().Append(1).Append(2).Build()], 2);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
