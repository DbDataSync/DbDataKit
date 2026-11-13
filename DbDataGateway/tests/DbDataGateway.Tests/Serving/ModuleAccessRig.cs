using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Apache.Arrow;
using DbDataKit.Hosting;
using DbDataKit.Identity;
using DbDataKit.Serving;
using DbDataKit.Serving.Conformance;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Management;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Config;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DbDataGateway.Tests.Serving;

/// <summary>A person of the rig: a user of the identity home in some groups, with a personal access token (the user name is the address).</summary>
internal sealed record RigPerson(string Name, string UserId, string Address, string Token, string[] Groups);

/// <summary>
/// The Gateway that plan 22a GP14 and 24a UC15 run their conformance on: the real host (state, identity home, serving core with its cache, the modules <c>flightsql</c>, <c>feeds</c>, <c>mcp</c> and <c>views</c> on their own listeners) over one SQL Server or PostgreSQL
/// database that holds a small star schema and the rules tables, with a semantic model published with row and column rules, and four people of the home whose groups the rules name. The rules, the model and the cache setting are changed through the same
/// calls a producer and a publisher make (<see cref="Refresh"/>, <see cref="Publish"/>), so what a phase of the suite sees is what a person would.
/// </summary>
internal sealed class ModuleAccessRig : IAsyncDisposable
{
    public const string Model = "orders_model";
    public const string Source = "warehouse";
    public const string DataGroup = "analysts";

    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-conformance-").FullName;
    private ScratchStateDatabase? _scratch;
    private WebApplication? _app;

    public string Engine { get; private set; } = "";

    public WebApplication App => _app!;

    public int FeedsPort { get; private set; }

    public int McpPort { get; private set; }

    public int ViewsPort { get; private set; }

    public int StorePort { get; private set; }

    public string FlightAddress { get; private set; } = "";

    public Dictionary<string, RigPerson> People { get; } = new(StringComparer.Ordinal);

    public T Get<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    public string DataDirectory => _dir;

    private string _connection = "";

    // the data: Ada's rules are east (the region East without cancelled orders), Bea's emea (both regions), Cy's nullers (the customer with no region); Dee is in the data group and in no rules group
    public static readonly string[] Seed =
    [
        "CREATE TABLE dim_customer (customer_key INT, customer VARCHAR(20), region VARCHAR(20))",
        "INSERT INTO dim_customer VALUES (1, 'C1', 'West'), (2, 'C2', 'West'), (3, 'C3', 'East'), (4, 'C4', 'North'), (5, 'C5', NULL)",
        "CREATE TABLE fact_sales (order_id INT, customer_key INT, net_amount DECIMAL(19,4), status VARCHAR(20))",
        "INSERT INTO fact_sales VALUES (1, 1, 100, 'Completed'), (2, 2, 120, 'Completed'), (3, 1, 80, 'Cancelled'), (7, 3, 100, 'Completed'), (9, 3, 100, 'Cancelled'), (10, 3, 100, 'Completed'), (15, 5, 40, 'Completed')",
        "CREATE TABLE region_access (who VARCHAR(40), region_code VARCHAR(20))",
        "INSERT INTO region_access VALUES ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL)",
        "CREATE TABLE status_access (who VARCHAR(40), status_code VARCHAR(20))",
        "INSERT INTO status_access VALUES ('east', 'Completed'), ('east', 'Cancelled'), ('emea', 'Completed'), ('emea', 'Cancelled'), ('nullers', 'Completed')",
        "CREATE TABLE blocked_status (who VARCHAR(40), status_code VARCHAR(20))",
        "INSERT INTO blocked_status VALUES ('east', 'Cancelled')",
    ];

    /// <summary>The model without its <c>access</c> and <c>cache</c> sections, and a saved query.</summary>
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
        queries:
          revenue_by_region:
            description: Net revenue and orders by region
            sql: SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM orders_model GROUP BY ALL LIMIT 1000
        """;

    private const string RowRules = """
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

    /// <summary>A dashboard over the model: a table of revenue by region, a table of orders by customer and a control that lists the regions (a members request).</summary>
    public const string Dashboard = """
        schemaVersion: 1
        kind: dashboard
        title: Orders
        parameters:
          - {name: region, type: string, for: customers.region, many: true, default: []}
        widgets:
          regions:
            type: control
            parameter: region
            control: multiselect
            values: {attribute: customers.region}
          by_region:
            type: table
            encodings:
              columns: [{field: customers.region}, {field: orders.net_revenue}, {field: orders.order_count}]
            query: {model: orders_model, limit: 100}
          by_customer:
            type: table
            encodings:
              columns: [{field: customers.customer}, {field: orders.order_count}]
            query: {model: orders_model, limit: 100}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 6}
            tiles:
              - {widget: regions, x: 0, y: 0, w: 12, h: 1}
              - {widget: by_region, x: 0, y: 1, w: 6, h: 5}
              - {widget: by_customer, x: 6, y: 1, w: 6, h: 5}
        """;

    /// <summary>The model's text for a cache setting (<c>off</c>, <c>caller</c> or <c>rules</c>) and the column rules (the lines of <c>access.columns</c>).</summary>
    public static string CatalogText(string cache, params string[] columnRules) =>
        Catalog + "\ncache: " + (cache == "off" ? "{enabled: false}" : $"{{enabled: true, ttl: 10m, scope: {cache}}}") + "\naccess:\n" + RowRules + (columnRules.Length == 0 ? "" : "\n  columns:\n" + string.Join("\n", columnRules.Select(r => "    - " + r)));

    /// <summary>The column rules of the first phase: east sees the customer as a fixed mask, emea as a hash, nullers not at all.</summary>
    public static readonly string[] ColumnRules =
    [
        "{ subject: east, dataset: dim_customer, column: customer, treatment: mask }",
        "{ subject: emea, dataset: dim_customer, column: customer, treatment: mask, mask: hash }",
        "{ subject: nullers, dataset: dim_customer, column: customer, treatment: hide }",
    ];

    public static async Task<ModuleAccessRig> StartAsync(string engine, string cache = "rules")
    {
        var rig = new ModuleAccessRig { Engine = engine };
        await rig.BeginAsync(cache);
        return rig;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task BeginAsync(string cache)
    {
        _scratch = ScratchStateDatabase.For(Engine);
        _connection = _scratch.Connection.Replace("env:" + _scratch.PasswordVariable, _scratch.Environment(_scratch.PasswordVariable));
        if (Engine == "postgres") _connection += ";SSL Mode=Disable";
        foreach (var statement in Seed) Execute(statement);

        var config = GatewayConfigFile.DirectoryIn(_dir);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        for (var attempt = 1; ; attempt++)
        {
            FeedsPort = FreePort();
            McpPort = FreePort();
            ViewsPort = FreePort();
            StorePort = FreePort();
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:DataDirectory"] = _dir, ["Gateway:Management:Enabled"] = "false", ["Gateway:Listen:Address"] = "127.0.0.1", ["Gateway:Listen:Port"] = "0", ["Gateway:DefinitionPollSeconds"] = "1",
                ["Gateway:Modules:0"] = "flightsql", ["Gateway:Modules:1"] = "feeds", ["Gateway:Modules:2"] = "mcp", ["Gateway:Modules:3"] = "views", ["Gateway:Modules:4"] = "store",
                ["Gateway:Store:Address"] = "127.0.0.1", ["Gateway:Store:Port"] = StorePort.ToString(),
                ["Gateway:Rest:Address"] = "127.0.0.1", ["Gateway:Rest:Port"] = FeedsPort.ToString(),
                ["Gateway:Mcp:Address"] = "127.0.0.1", ["Gateway:Mcp:Port"] = McpPort.ToString(),
                ["Gateway:Views:Address"] = "127.0.0.1", ["Gateway:Views:Port"] = ViewsPort.ToString(),
                [$"Gateway:DataSources:{Source}:Provider"] = Engine == "mssql" ? "sqlserver" : "postgres",
                [$"Gateway:DataSources:{Source}:ConnectionString"] = _connection,
            };
            HomeSeed.Open(settings, [Source], group: DataGroup);
            _app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
            try
            {
                await _app.StartAsync();
                break;
            }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                await _app.DisposeAsync();
            }
        }

        FlightAddress = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
        var state = Get<StateDatabase>();
        var serverId = GatewayStoreModule.ServerIdOf(Get<IOptionsMonitor<GatewayOptions>>().CurrentValue, state);
        var directory = new HomeDirectory(state);
        foreach (var (name, groups) in new (string, string[])[] { ("Ada", ["east"]), ("Bea", ["emea"]), ("Cy", ["nullers"]), ("Dee", []) })
        {
            string[] all = [DataGroup, .. groups];
            var (user, token) = HomeSeed.Person(state, name, $"{name.ToLowerInvariant()}@example.invalid", all);
            directory.Assign(AssignmentSubject.User(user.Id), serverId, ServerRole.Viewer);
            People[name.ToLowerInvariant()] = new RigPerson(name, user.Id, $"{name.ToLowerInvariant()}@example.invalid", token, all);
        }

        Publish(CatalogText(cache, ColumnRules));
        if (Get<GatewayViewsPublishing>().Publish("dashboard", "orders", Dashboard, "test").Status != 200) throw new InvalidOperationException("The dashboard was not published.");
        Get<DefinitionFollower>().Poll();
    }

    /// <summary>The master data model of the Store half: territory (a key, a region the column rules treat) with row rules from two rules tables in the schema <c>rules</c>.</summary>
    public static string EntityModelText(params string[] columnRules)
    {
        var text = new StringBuilder();
        text.Append("schemaVersion: 2\nname: sales\nstorage: {connection: warehouse}\naccess:\n  max_age: 1h\n  row:\n    - dataset: territory\n      groups:\n        - id: by_region\n          rules:\n");
        text.Append("            - { id: region, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }\n");
        text.Append("            - { id: not_closed, effect: exclude, table: rules.closed_codes, subject: who, columns: { code: code } }\n");
        if (columnRules.Length > 0) text.Append("  columns:\n").AppendJoin("\n", columnRules.Select(r => "    - " + r)).Append('\n');
        text.Append("entities:\n  territory:\n    key: [code]\n    attributes:\n      code: {type: VARCHAR(10), required: true}\n      region: {type: VARCHAR(20)}\n");
        return text.ToString();
    }

    /// <summary>The rows of the entity and the rules tables the Store half reads, made on the rig's database (Store models run on SQL Server and PostgreSQL), and the model published with these column rules.</summary>
    public Task PublishEntitiesOnlyAsync(params string[] columnRules)
    {
        Get<PublishedEntityModels>().Publish("sales", EntityModelText(columnRules), Source, null, "test", format: 2);
        return Task.CompletedTask;
    }

    public async Task PublishEntitiesAsync(params string[] columnRules)
    {
        var models = Get<PublishedEntityModels>();
        models.Publish("sales", EntityModelText(columnRules), Source, null, "test", format: 2);
        var served = models.Find("sales") ?? throw new InvalidOperationException("The entity model was not served.");
        DbConnection data = Engine == "mssql" ? new SqlConnection(_connection) : new NpgsqlConnection(_connection);
        await using (data)
        {
            await data.OpenAsync();
            var engine = Engine == "mssql" ? "sqlserver" : "postgres";
            await DbDataKit.DataModel.Storage.ModelStorage.CreateAsync(data, DbDataKit.DataModel.Storage.ModelStorage.Plan(served.Model, engine));
            foreach (var statement in new[]
            {
                "CREATE SCHEMA rules",
                "CREATE TABLE rules.region_access (who VARCHAR(40), region_code VARCHAR(20))",
                "CREATE TABLE rules.closed_codes (who VARCHAR(40), code VARCHAR(10))",
                "INSERT INTO rules.region_access VALUES ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL)",
                "INSERT INTO rules.closed_codes VALUES ('east', 'E3')",
            })
            {
                using var command = data.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            var writer = new DbDataKit.Store.ModelWriter(served.Model, data, engine);
            var ada = new DbDataKit.Store.WriteActor("u-ada", "Ada");
            foreach (var (code, region) in new[] { ("E1", "East"), ("E2", "East"), ("E3", "East"), ("W1", "West"), ("N1", "North"), ("X1", null) })
                await writer.InsertAsync("territory", new Dictionary<string, object?> { ["code"] = code, ["region"] = region }, ada);
        }
    }

    /// <summary>The producer says a rules table of the entity model was refreshed.</summary>
    public int RefreshEntityRules(string table)
    {
        var notice = new DataChangedNotice(GatewayLineageGraph.DatasetIdentity(Get<IOptionsMonitor<GatewayOptions>>().CurrentValue.DataSources, (name, text) => Get<DbDataGateway.Server.Secrets.SecretsRuntime>().ResolveConnectionString($"Gateway:DataSources:{name}:ConnectionString", text, Engine == "mssql" ? "sqlserver" : "postgres"), Source, table)!, "build", DateTimeOffset.UtcNow);
        Get<GatewayAccess>().Changed(notice);
        return Get<GatewayCache>().Invalidator.Apply(notice).Removed;
    }

    /// <summary>Publishes a version of the model and serves it now, as a publisher's call does.</summary>
    public void Publish(string text)
    {
        var models = Get<PublishedSemanticModels>();
        models.Publish(Model, text, null, "test", new Dictionary<string, string> { [Source] = Source }, [], DateTimeOffset.UtcNow);
        models.ServeNow(Model);
    }

    public string Identity(string table) =>
        GatewayLineageGraph.DatasetIdentity(Get<IOptionsMonitor<GatewayOptions>>().CurrentValue.DataSources, (name, text) => Get<DbDataGateway.Server.Secrets.SecretsRuntime>().ResolveConnectionString($"Gateway:DataSources:{name}:ConnectionString", text, Engine == "mssql" ? "sqlserver" : "postgres"), Source, table)!;

    /// <summary>The producer says the rules table was refreshed: what the management call <c>cache changed</c> does. Returns how many cache entries went.</summary>
    public int Refresh(string table)
    {
        var notice = new DataChangedNotice(Identity(table), "build", DateTimeOffset.UtcNow);
        Get<GatewayAccess>().Changed(notice);
        return Get<GatewayCache>().Invalidator.Apply(notice).Removed;
    }

    public void Execute(string sql)
    {
        DbConnection connection = Engine == "mssql" ? new SqlConnection(_connection) : new NpgsqlConnection(_connection);
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    public ServingContext ContextOf(string person)
    {
        var callers = Get<HomeCallers>().GetAsync().AsTask().GetAwaiter().GetResult();
        return new ServingContext(new GatewayCaller(HomePrincipals.Create(callers, callers.UserOf(People[person].UserId)!, "conformance", Get<IOptionsMonitor<GatewayOptions>>().CurrentValue)));
    }

    public static HttpClient Trusting() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
        _scratch?.Dispose();
        TestFolder.Delete(_dir);
    }

    // ---- what the questions are, and how rows are read ----

    public sealed record Ask(string Id, string Description, string Json, string? Sql = null, string? Saved = null);

    public static readonly Ask ByRegion = new("by-region", "net revenue and orders by region",
        """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue","orders.order_count"],"attributes":["customers.region"],"limit":100}}""",
        "SELECT customers.region, MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM orders_model GROUP BY ALL LIMIT 100", "revenue_by_region");

    public static readonly Ask Total = new("total", "net revenue and orders in all",
        """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.net_revenue","orders.order_count"],"limit":100}}""",
        "SELECT MEASURE(orders.net_revenue), MEASURE(orders.order_count) FROM orders_model LIMIT 100");

    public static readonly Ask Members = new("members", "the regions a person's rows hold", """{"spec_version":"0.3","kind":"members","attribute":"customers.region"}""");

    public static readonly Ask ByCustomer = new("by-customer", "orders by customer (a column the rules hide or mask)",
        """{"spec_version":"0.3","kind":"query","query":{"measures":["orders.order_count"],"attributes":["customers.customer"],"limit":100}}""",
        "SELECT customers.customer, MEASURE(orders.order_count) FROM orders_model GROUP BY ALL LIMIT 100");

    public static readonly Ask[] Asks = [ByRegion, Total, Members, ByCustomer];

    public static ConformanceQuestion Question(Ask ask) => new(ask.Id, ask.Description);

    public static Ask AskOf(ConformanceQuestion question) => Asks.Single(a => a.Id == question.Id);

    public static async Task<ModuleAnswer> RowsOf(IServingAnswer answer, CancellationToken cancellationToken = default)
    {
        await using var _ = answer;
        var rows = new List<string>();
        await foreach (var batch in answer.ReadBatchesAsync(cancellationToken))
            for (var r = 0; r < batch.Length; r++)
                rows.Add(ModuleAnswer.Row(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowCell(batch.Column(c), r)).ToArray()));
        return ModuleAnswer.Of(rows);
    }

    private static object? ArrowCell(IArrowArray array, int row) => array.IsNull(row) ? null : array switch
    {
        StringArray s => s.GetString(row),
        DoubleArray d => d.GetValue(row)!.Value,
        Decimal128Array m => m.GetValue(row)!.Value,
        Int64Array l => l.GetValue(row)!.Value,
        Int32Array n => n.GetValue(row)!.Value,
        BooleanArray b => b.GetValue(row)!.Value,
        _ => array.GetType().Name,
    };

    /// <summary>The rows of a REST answer (<c>{status, columns, rows}</c>): arrays in column order.</summary>
    public static ModuleAnswer RowsOf(JsonNode body) => ModuleAnswer.Of(body["rows"]!.AsArray().Select(r => ModuleAnswer.Row(r!.AsArray().Select(JsonCell).ToArray())));

    public static object? JsonCell(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => node.ToJsonString(),
    };
}
