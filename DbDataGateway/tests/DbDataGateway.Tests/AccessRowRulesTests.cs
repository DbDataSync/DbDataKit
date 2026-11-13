using System.Globalization;
using Apache.Arrow;
using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Data;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DbDataGateway.Tests;

/// <summary>
/// Plan 22a GP11 (QG2, QR1 to QR4) end to end on SQL Server and PostgreSQL: a model with row rules over a real database, the caller's groups from the identity home, the rules tables as real tables. A person in a group sees the rows the rules table gives
/// through a semantic request, semantic SQL, a members listing and an explain; the same request as two callers gives two answers; a new rule row shows after the producer's <c>data.changed</c> and no cache entry of the old rules is served; a table past its
/// age refuses; counts and totals are over the filtered rows.
/// </summary>
public sealed class AccessRowRulesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-access-").FullName;
    private ScratchStateDatabase? _scratch;
    private StateDatabase? _state;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _scratch?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    public static IEnumerable<object[]> Engines => [["mssql"], ["postgres"]];

    private sealed class Monitor(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string Catalog = """
        version: "0.3"
        name: orders_model
        timezone: UTC
        sources:
          warehouse: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
        tables:
          - name: fact_sales
            x_source: warehouse
            source: fact_sales
            primary_key: [order_id]
            columns:
              - {name: order_id, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: order_id}]}}
              - {name: customer_key, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: customer_key}]}}
              - {name: net_amount, datatype: Decimal, expression: {dialects: [{dialect: ANSI_SQL, expression: net_amount}]}}
              - {name: status, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: status}]}}
          - name: dim_customer
            x_source: warehouse
            source: dim_customer
            primary_key: [customer_key]
            columns:
              - {name: customer_key, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: customer_key}]}}
              - {name: customer, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: customer}]}}
              - {name: region, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region}]}}
          - name: region_access
            x_source: warehouse
            source: region_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}
          - name: status_access
            x_source: warehouse
            source: status_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: status_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: status_code}]}}
          - name: blocked_status
            x_source: warehouse
            source: blocked_status
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: status_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: status_code}]}}
        relationships:
          - {name: sales_to_customer, from: fact_sales, to: dim_customer, from_columns: [customer_key], to_columns: [customer_key]}
        dimensions:
          customers:
            attributes:
              region:
                type: string
                bindings:
                  warehouse: {table: dim_customer, column: region}
              customer:
                type: string
                bindings:
                  warehouse: {table: dim_customer, column: customer}
          orders:
            attributes:
              status:
                type: string
                bindings:
                  warehouse: {table: fact_sales, column: status}
        measures:
          orders.net_revenue:
            type: number
            source: warehouse
            agg: sum
            of: {table: fact_sales, column: net_amount}
            semantics: {grain: [customers.region], rollup: {default: sum}, spread: blank, status: governed}
          orders.order_count:
            type: integer
            source: warehouse
            agg: count_distinct
            of: {table: fact_sales, column: order_id}
            semantics: {grain: [customers.region], rollup: {default: none}, spread: blank, status: governed}
        cache: {enabled: true, ttl: 10m, scope: rules}
        access:
          max_age: 1h
          row:
            - dataset: dim_customer
              groups:
                - id: by_region
                  rules:
                    - { id: region, effect: include, table: region_access, subject: who, columns: { region: region_code } }
            - dataset: fact_sales
              groups:
                - id: visible
                  rules:
                    - { id: statuses, effect: include, table: status_access, subject: who, columns: { status: status_code } }
                    - { id: no_blocked, effect: exclude, table: blocked_status, subject: who, columns: { status: status_code } }
        """;

    // West 300 (orders 1, 2, 3: order 3 is Cancelled), East 300 (7, 9, 10: order 9 is Cancelled), a customer with no region (NULL) with order 15 of 40: 640 in all
    private static readonly string[] Seed =
    [
        "CREATE TABLE dim_customer (customer_key INT, customer VARCHAR(20), region VARCHAR(20))",
        "INSERT INTO dim_customer VALUES (1, 'C1', 'West'), (2, 'C2', 'West'), (3, 'C3', 'East'), (4, 'C4', 'North'), (5, 'C5', NULL)",
        "CREATE TABLE fact_sales (order_id INT, customer_key INT, net_amount DECIMAL(19,4), status VARCHAR(20))",
        "INSERT INTO fact_sales VALUES (1, 1, 100, 'Completed'), (2, 2, 120, 'Completed'), (3, 1, 80, 'Cancelled'), (7, 3, 100, 'Completed'), (9, 3, 100, 'Cancelled'), (10, 3, 100, 'Completed'), (15, 5, 40, 'Completed')",
        "CREATE TABLE region_access (who VARCHAR(40), region_code VARCHAR(20))",
        "INSERT INTO region_access VALUES ('east', 'East'), ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL)",
        "CREATE TABLE status_access (who VARCHAR(40), status_code VARCHAR(20))",
        "INSERT INTO status_access VALUES ('east', 'Completed'), ('east', 'Cancelled'), ('emea', 'Completed'), ('emea', 'Cancelled'), ('nullers', 'Completed')",
        "CREATE TABLE blocked_status (who VARCHAR(40), status_code VARCHAR(20))",
        "INSERT INTO blocked_status VALUES ('east', 'Cancelled')",
    ];

    private sealed class Fixture
    {
        public required string Engine { get; init; }
        public required string Connection { get; init; }
        public required GatewayOptions Options { get; init; }
        public required Clock Time { get; init; }
        public required GatewayAccess Access { get; init; }
        public required GatewayCache Cache { get; init; }
        public required IServingServices Services { get; init; }
        public required SemanticModels Models { get; init; }
        public required List<string> Alerts { get; init; }
        public string Identity => GatewayLineageGraph.DatasetIdentity(Options.DataSources, (_, text) => text, "warehouse", "region_access")!;

        public void Execute(string sql)
        {
            if (Engine == "mssql") { using var c = new SqlConnection(Connection); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            else { using var c = new NpgsqlConnection(Connection); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }

        /// <summary>The producer says the rules table was refreshed: what the management call does (the table's age starts again, what read it goes).</summary>
        public int RulesRefreshed()
        {
            var notice = new DataChangedNotice(Identity, "build", Time.Now);
            Access.Changed(notice);
            return Cache.Invalidator.Apply(notice).Removed;
        }
    }

    private Fixture Make(string engine, string? catalog = null)
    {
        _scratch = ScratchStateDatabase.For(engine);
        var connection = _scratch.Connection.Replace("env:" + _scratch.PasswordVariable, _scratch.Environment(_scratch.PasswordVariable));
        var options = new GatewayOptions();
        options.DataSources["warehouse"] = new DataSourceOptions { Provider = engine == "mssql" ? "sqlserver" : "postgres", ConnectionString = engine == "mssql" ? connection : connection + ";SSL Mode=Disable", Everyone = true };
        var clock = new Clock(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
        var monitor = new Monitor(options);
        var sources = new DataSourceCatalog(monitor, GatewaySources.Providers());
        _state = GatewayState.Open(_dir);
        var models = new SemanticModels();
        models.Add(CatalogLoader.LoadText(catalog ?? Catalog, "orders.catalog.yaml"));
        var alerts = new List<string>();
        var access = new GatewayAccess(sources, () => options, () => models, new RulesTableLedger(_state, clock),
            (source, physical) => GatewayLineageGraph.DatasetIdentity(options.DataSources, (_, text) => text, source, physical), new Audit(alerts), NullLogger.Instance, clock);
        models.Access = access;
        var cache = new GatewayCache(sources, () => options, () => models, null, clock, access: access);
        var serving = new GatewayServing(sources, new ChainedCredentialValidator([]), null!, null!, monitor, NullLogger<GatewayServing>.Instance, clock);
        cache.Resolve = new GatewayCacheManagement(cache, () => null, "gw", access: access).Resolve;
        var fixture = new Fixture { Engine = engine, Connection = connection, Options = options, Time = clock, Access = access, Cache = cache, Models = models, Alerts = alerts, Services = serving.WithSemantic(models).WithCache(cache.Store, cache.Policy).WithPassThroughGuard(new RuledTables(() => models, () => []), sources) };
        foreach (var statement in Seed) fixture.Execute(statement);
        return fixture;
    }

    private sealed class Audit(List<string> alerts) : DbDataKit.Web.Trust.IAuditLog
    {
        public void Append(DbDataKit.Web.Trust.AuditEntry entry) => alerts.Add(entry.Command + ": " + entry.Target);

        public IReadOnlyList<DbDataKit.Web.Trust.AuditEntry> Read(int limit = 100) => [];
    }

    private static ServingContext Person(string id, params string[] groups) => new(new GatewayCaller(new GatewayPrincipal(id, "home-user", [], null, null, id, groups, id + "@example.invalid")));

    private const string ByRegion = """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue","orders.order_count"],"attributes":["customers.region"],"limit":100}}""";

    private static async Task<List<string>> Rows(IServingServices services, ServingContext context, ServingRequest request)
    {
        await using var answer = await services.Queries.RunAsync(context, request);
        var rows = new List<string>();
        await foreach (var batch in answer.ReadBatchesAsync())
            for (var i = 0; i < batch.Length; i++)
                rows.Add(string.Join("|", Enumerable.Range(0, batch.ColumnCount).Select(c => Cell(batch.Column(c), i))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Cell(IArrowArray array, int row) => array.IsNull(row) ? "NULL" : array switch
    {
        StringArray s => s.GetString(row),
        DoubleArray d => d.GetValue(row)!.Value.ToString("0.##", CultureInfo.InvariantCulture),
        Decimal128Array m => m.GetValue(row)!.Value.ToString("0.##", CultureInfo.InvariantCulture),
        Int64Array l => l.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
        Int32Array n => n.GetValue(row)!.Value.ToString(CultureInfo.InvariantCulture),
        _ => array.GetType().Name,
    };

    private static ServingRequest.Semantic Query(string json = ByRegion) => new("semantic:orders_model", json);

    [Theory, MemberData(nameof(Engines))]
    public async Task A_person_in_a_group_sees_the_rows_the_rules_table_gives_and_two_callers_get_two_answers(string engine)
    {
        var f = Make(engine);
        // the rules: east sees East (twice in the table, once in the answer) without the cancelled order 9; emea sees both regions; nullers only the customer with no region
        Assert.Equal(["East|200|2"], await Rows(f.Services, Person("u1", "east"), Query()));
        Assert.Equal(["East|300|3", "West|300|3"], await Rows(f.Services, Person("u2", "emea"), Query()));
        Assert.Equal(["NULL|40|1"], await Rows(f.Services, Person("u3", "nullers"), Query()));
        // a person no rule names sees none (D2), and so does one who is in the group of a table of the dataset but not of the other
        Assert.Empty(await Rows(f.Services, Person("u4", "nobody"), Query()));
        // the same query, two callers, two answers: the second one was not answered from the first one's cache entry
        var total = """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue","orders.order_count"],"limit":100}}""";
        Assert.Equal(["200|2"], await Rows(f.Services, Person("u1", "east"), Query(total)));
        Assert.Equal(["600|6"], await Rows(f.Services, Person("u2", "emea"), Query(total)));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Semantic_sql_a_members_listing_and_an_explain_apply_the_rules_too(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        Assert.Equal(["East|200"], await Rows(f.Services, east, new ServingRequest.SemanticSql("semantic:orders_model", "SELECT customers.region, MEASURE(orders.net_revenue) FROM orders_model GROUP BY ALL LIMIT 100")));
        // the members of an attribute are the ones the person's rows hold, not the whole dimension table (North has no sale, NULL is no member)
        Assert.Equal(["East"], await Rows(f.Services, east, Query("""{"spec_version":"0.3","kind":"members","attribute":"customers.region"}""")));
        Assert.Equal(["East", "West"], await Rows(f.Services, Person("u2", "emea"), Query("""{"spec_version":"0.3","kind":"members","attribute":"customers.region"}""")));
        // an explain shows the semi-join to the rules table and no value of the caller
        var plan = string.Join("\n", await Rows(f.Services, east, Query("""{"spec_version":"0.3","kind":"explain","query":{"measures":["orders.net_revenue"],"attributes":["customers.region"],"limit":100}}""")));
        Assert.Contains("EXISTS (SELECT 1 FROM region_access", plan);
        Assert.Contains("NOT EXISTS", plan.Replace("(NOT EXISTS", "NOT EXISTS"));
        Assert.DoesNotContain("'east'", plan);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_new_rule_row_shows_without_a_restart_and_no_entry_of_the_old_rules_is_served(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        var first = await Rows(f.Services, east, Query());
        Assert.Equal(["East|200|2"], first);
        Assert.Equal(first, await Rows(f.Services, east, Query()));
        Assert.Equal(1, f.Cache.Store.Stats.Hits);   // the second ask was answered from the cache

        f.Execute("INSERT INTO region_access VALUES ('east', 'West')");
        // the rows are read live, but the names the table holds are read once per generation: until the producer says it refreshed, the cache entry stands too
        f.Time.Now += TimeSpan.FromMinutes(5);
        Assert.True(f.RulesRefreshed() >= 1);   // the entries of the old rules go
        Assert.Equal(["East|200|2", "West|220|2"], await Rows(f.Services, east, Query()));   // West: orders 1 and 2 (order 3 is Cancelled and blocked for east)
        Assert.Equal(1, f.Cache.Store.Stats.Hits);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_rules_table_past_half_its_age_is_served_and_alerted_once_and_past_its_age_refuses(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        await Rows(f.Services, east, Query());
        f.Time.Now += TimeSpan.FromMinutes(40);   // past half of an hour
        for (var i = 0; i < 3; i++) Assert.Equal(["East|200|2"], await Rows(f.Services, east, Query()));
        Assert.Equal(3, f.Alerts.Count);   // one per rules table (region_access, status_access, blocked_status), however many requests follow

        f.Time.Now += TimeSpan.FromMinutes(30);   // past the hour
        var refused = await Assert.ThrowsAsync<ServingDeniedException>(() => Rows(f.Services, east, Query()));
        Assert.Contains("is not served", refused.Message);
        // a person the tables have no row for sees nothing anyway, no refusal
        Assert.Empty(await Rows(f.Services, Person("u4", "nobody"), Query()));
        // the producer refreshes the tables and the model is served again
        f.Execute("UPDATE region_access SET region_code = region_code");
        foreach (var table in new[] { "region_access", "status_access", "blocked_status" })
        {
            var notice = new DataChangedNotice(GatewayLineageGraph.DatasetIdentity(f.Options.DataSources, (_, text) => text, "warehouse", table)!, "build", f.Time.Now);
            f.Access.Changed(notice);
        }

        Assert.Equal(["East|200|2"], await Rows(f.Services, east, Query()));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_statement_that_names_a_table_with_rules_is_refused_in_every_spelling_and_the_same_data_through_the_model_works(string engine)
    {
        var f = Make(engine);
        var east = Person("u1", "east");
        var quoted = engine == "mssql" ? "SELECT * FROM [dbo].[DIM_CUSTOMER]" : "SELECT * FROM \"public\".\"dim_customer\"";
        foreach (var text in new[] { "SELECT * FROM dim_customer", "select * from DIM_Customer c", quoted, "SELECT f.order_id FROM fact_sales f", "SELECT 1 FROM (SELECT * FROM fact_sales /* c */) x", "SELECT * FROM dbo/**/./**/fact_sales".Replace("dbo", engine == "mssql" ? "dbo" : "public") })
        {
            var refused = await Assert.ThrowsAsync<ServingDeniedException>(() => Rows(f.Services, east, new ServingRequest.Sql("warehouse", text)));
            Assert.Contains("access rules", refused.Message);
            Assert.Contains("semantic:orders_model", refused.Message);
            Assert.Contains("row rules", refused.Message);
        }

        // rule names: the table of the other dataset names its own rules
        var named = await Assert.ThrowsAsync<ServingDeniedException>(() => Rows(f.Services, east, new ServingRequest.Sql("warehouse", "SELECT * FROM fact_sales")));
        Assert.Contains("'statuses'", named.Message);
        Assert.Contains("'no_blocked'", named.Message);
        // prepared statements are checked as well
        await Assert.ThrowsAsync<ServingDeniedException>(() => f.Services.Queries.PrepareAsync(east, new ServingRequest.Sql("warehouse", "SELECT * FROM dim_customer")));
        // the rules tables and a table nobody put rules on are untouched (the data source's own grants decide), and so is a statement that names none
        Assert.Equal(["East", "East"], await Rows(f.Services, east, new ServingRequest.Sql("warehouse", "SELECT region_code FROM region_access WHERE who = 'east' AND region_code IS NOT NULL")));
        Assert.Equal(["1"], await Rows(f.Services, east, new ServingRequest.Sql("warehouse", "SELECT 1")));
        // the same data through the model: the rules apply
        Assert.Equal(["East|200|2"], await Rows(f.Services, east, Query()));
        Assert.Equal(["East|200|2"], await Rows(f.Services, east, new ServingRequest.Sql("semantic:orders_model", """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue","orders.order_count"],"attributes":["customers.region"],"limit":100}}""")));
        // the listing: the raw data source does not list a ruled table (nor its columns), an unruled one is listed; the model lists its own datasets
        async Task<List<string>> Listed(string source, bool columns = false)
        {
            var names = new List<string>();
            await foreach (var o in f.Services.Catalog.DescribeAsync(east, [], withColumns: columns, source: source)) names.Add($"{o.Kind}:{o.Name}");
            return names;
        }

        var raw = await Listed("warehouse", columns: true);
        Assert.DoesNotContain(raw, n => n.EndsWith(":dim_customer", StringComparison.OrdinalIgnoreCase) || n.EndsWith(":fact_sales", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(raw, n => n.StartsWith("Table:", StringComparison.Ordinal) && n.EndsWith(":region_access", StringComparison.OrdinalIgnoreCase));
        var model = await Listed("semantic:orders_model");
        Assert.Contains("Measure:orders.net_revenue", model);
        Assert.Contains("Dimension:customers", model);
        // the model taken out of service frees the tables, and the listing
        f.Models.Remove("orders_model");
        Assert.Contains(await Listed("warehouse"), n => n.EndsWith(":dim_customer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, (await Rows(f.Services, east, new ServingRequest.Sql("warehouse", "SELECT * FROM dim_customer"))).Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_data_source_that_answers_with_the_callers_own_login_cannot_hold_rules(string engine)
    {
        var f = Make(engine);
        f.Options.DataSources["warehouse"].PassThrough = true;
        var refused = await Assert.ThrowsAsync<ServingDeniedException>(() => Rows(f.Services, Person("u1", "east"), Query()));
        Assert.Contains("cannot be read", refused.Message);
    }
}
