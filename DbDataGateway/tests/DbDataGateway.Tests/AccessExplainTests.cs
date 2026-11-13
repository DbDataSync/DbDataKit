using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
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
/// Plan 22a GP15 on SQL Server and PostgreSQL: <c>access explain</c> and <c>access coverage</c> over the management API, made by the one resolver and predicate the reads use, with real rules tables. An administrator explains anyone; a person only themselves
/// and never learns another's rows; the explanation of a person with no rule row says so, one excluded by a rule names the rule, one whose table is expired says that, and a rule row added then refreshed changes it; the coverage lists a value no row covers and a NULL.
/// </summary>
public sealed class AccessExplainTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-explain-").FullName;
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

    private sealed class ListAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Append(AuditEntry entry) => Entries.Add(entry);
        public IReadOnlyList<AuditEntry> Read(int limit = 100) => Entries;
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
        public required GatewayAccessManagement Api { get; init; }
        public required ListAudit Audit { get; init; }
        public required StateDatabase State { get; init; }

        public void Execute(string sql)
        {
            if (Engine == "mssql") { using var c = new SqlConnection(Connection); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            else { using var c = new NpgsqlConnection(Connection); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }

        /// <summary>The producer says a rules table was refreshed (what the management call for <c>data.changed</c> does).</summary>
        public void Refreshed(string table) =>
            Access.Changed(new DataChangedNotice(GatewayLineageGraph.DatasetIdentity(Options.DataSources, (_, text) => text, "warehouse", table)!, "build", Time.Now));

        public string UserId(string name) => new IdentityUserStore(State).List().Single(u => u.DisplayName == name).Id;

        public ManagementCaller Holding(ServerRole roles, string? id = null, string? name = null)
        {
            var client = new PairedClient("console-1", "Console", [], ServerRole.Admin);
            return new ManagementCaller(new ValidatedToken(client, id ?? "root", name ?? "Root", false, roles, new Dictionary<string, ServerRole>(), "token-1", Time.Now, Time.Now.AddMinutes(5)));
        }

        public ManagementResult Get(ManagementCaller caller, string route, params (string Name, string Value)[] query)
        {
            var collection = new System.Collections.Specialized.NameValueCollection();
            foreach (var (name, value) in query) collection.Add(name, value);
            return Api.Handle("GET", route, caller, collection);
        }
    }

    private Fixture Make(string engine)
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
        models.Add(CatalogLoader.LoadText(Catalog, "orders.catalog.yaml"));
        var access = new GatewayAccess(sources, () => options, () => models, new RulesTableLedger(_state, clock),
            (source, physical) => GatewayLineageGraph.DatasetIdentity(options.DataSources, (_, text) => text, source, physical), null, NullLogger.Instance, clock);
        models.Access = access;
        var cache = new GatewayCache(sources, () => options, () => models, null, clock, access: access);
        var key = new GroupMaskKey(_state, new GroupSessionKey(_state, monitor), clock);
        var audit = new ListAudit();
        var inspector = new GatewayAccessInspector(access, () => models, new PublishedEntityModels(new DefinitionStore(_state)), _state, null!, () => options);
        var fixture = new Fixture { Engine = engine, Connection = connection, Options = options, Time = clock, Access = access, Audit = audit, State = _state, Api = new GatewayAccessManagement(key, cache, audit, clock, inspector) };
        foreach (var statement in Seed) fixture.Execute(statement);
        // people of the identity home: Ann is in east, Bob in emea, Cy in no group that a rules table names
        var users = new IdentityUserStore(_state);
        var directory = new HomeDirectory(_state);
        var groups = new[] { "east", "emea" }.ToDictionary(g => g, g => directory.CreateGroup(g));
        foreach (var (name, group) in new[] { ("Ann", "east"), ("Bob", "emea"), ("Cy", null) })
        {
            var user = users.CreateUser(name, name.ToLowerInvariant() + "@example.invalid", UserRole.Viewer);
            if (group is not null) directory.AddMember(groups[group].Id, user.Id);
        }

        return fixture;
    }

    private static JsonObject Ok(ManagementResult result)
    {
        Assert.True(result.Status == 200, result.Body.ToJsonString());
        return (JsonObject)result.Body;
    }

    private static JsonObject Dataset(JsonObject explanation, string name) => explanation["datasets"]!.AsArray().OfType<JsonObject>().Single(d => (string?)d["dataset"] == name);

    [Theory, MemberData(nameof(Engines))]
    public void An_administrator_asks_why_a_person_can_or_cannot_see_a_row_and_gets_the_rule_that_decided(string engine)
    {
        var f = Make(engine);
        var admin = f.Holding(ServerRole.Admin);
        // Ann is in east: the rules table has East twice, statuses Completed and Cancelled, and blocks Cancelled for east
        var completed = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "fact_sales"), ("row", "status=Completed")));
        Assert.Equal("person", (string?)completed["subject_kind"]);
        Assert.Contains("east", completed["names"]!.AsArray().Select(n => (string?)n));
        var sales = Dataset(completed, "fact_sales");
        Assert.True((bool)sales["verdict"]!["included"]!);
        Assert.Equal("visible/statuses", (string?)sales["verdict"]!["decided_by"]![0]);
        Assert.Equal(["Cancelled", "Completed"], sales["groups"]![0]!["rules"]!.AsArray().Single(r => (string?)r!["id"] == "statuses")!["rows"]!.AsArray().Select(r => (string?)r!["values"]!["status_code"]).Order());
        var cancelled = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "fact_sales"), ("row", "status=Cancelled")));
        var excluded = Dataset(cancelled, "fact_sales")["verdict"]!;
        Assert.False((bool)excluded["included"]!);
        Assert.Equal("visible/no_blocked", (string?)excluded["decided_by"]![0]);
        Assert.Contains("rule 'visible/no_blocked' removes it", (string?)excluded["text"]);
        // a status no rule row of theirs names
        var other = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "fact_sales"), ("row", "status=Pending")));
        Assert.False((bool)Dataset(other, "fact_sales")["verdict"]!["included"]!);
        // the tables and their state
        Assert.All(completed["tables"]!.AsArray(), t => Assert.Equal("fresh", (string?)t!["state"]));
        Assert.Equal(3, completed["tables"]!.AsArray().Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_person_with_no_rule_row_is_told_so_and_a_group_is_explained_by_its_name(string engine)
    {
        var f = Make(engine);
        var admin = f.Holding(ServerRole.Admin);
        var cy = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Cy"), ("model", "orders_model")));
        Assert.Contains("No row of 'status_access' names them", (string?)Dataset(cy, "fact_sales")["summary"]);
        Assert.Contains("No row of 'region_access' names them", (string?)Dataset(cy, "dim_customer")["summary"]);
        // a group, with a NULL row: `nullers` has the NULL region only
        var nullers = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "nullers"), ("kind", "group"), ("model", "orders_model"), ("dataset", "dim_customer"), ("row", "region=null")));
        Assert.Equal("group", (string?)nullers["subject_kind"]);
        Assert.True((bool)Dataset(nullers, "dim_customer")["verdict"]!["included"]!);
        var west = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "nullers"), ("kind", "group"), ("model", "orders_model"), ("dataset", "dim_customer"), ("row", "region=West")));
        Assert.False((bool)Dataset(west, "dim_customer")["verdict"]!["included"]!);
        // a name the home does not know is explained as it stands, with a note
        var unknown = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "stranger"), ("model", "orders_model")));
        Assert.Contains("knows no person or group named 'stranger'", (string?)unknown["warnings"]![0]);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_table_past_its_age_is_said_to_be_expired_and_a_new_rule_row_with_a_refresh_changes_the_explanation(string engine)
    {
        var f = Make(engine);
        var admin = f.Holding(ServerRole.Admin);
        var ask = () => Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "dim_customer"), ("row", "region=West")));
        Assert.False((bool)Dataset(ask(), "dim_customer")["verdict"]!["included"]!);
        // a row for east/West is added to the rules table's source; until the refresh is reported the names are as they were, the rows read now
        f.Execute("INSERT INTO region_access VALUES ('east', 'West')");
        f.Time.Now += TimeSpan.FromMinutes(5);
        f.Refreshed("region_access");
        var changed = Dataset(ask(), "dim_customer");
        Assert.True((bool)changed["verdict"]!["included"]!);
        Assert.Equal("by_region/region", (string?)changed["verdict"]!["decided_by"]![0]);
        Assert.NotNull(ask()["tables"]!.AsArray().Single(t => (string?)t!["table"] == "region_access")!["last_changed"]);

        f.Time.Now += TimeSpan.FromHours(2);
        var expired = Ok(f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model")));
        Assert.Equal("expired", (string?)expired["tables"]!.AsArray().Single(t => (string?)t!["table"] == "status_access")!["state"]);
        Assert.Contains("is not served to them", (string?)Dataset(expired, "dim_customer")["summary"] + (string?)Dataset(expired, "fact_sales")["summary"]);
        Assert.Contains("past its maximum age", (string?)Dataset(expired, "fact_sales")["summary"]);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Coverage_lists_the_value_no_rule_row_covers_and_a_NULL_and_counts_them(string engine)
    {
        var f = Make(engine);
        var admin = f.Holding(ServerRole.Admin);
        var report = Ok(f.Get(admin, GatewayAccessManagement.CoverageRoute, ("model", "orders_model")));
        var region = report["rules"]!.AsArray().OfType<JsonObject>().Single(r => (string?)r["rule"] == "region");
        // dim_customer.region: West, East, North, NULL; the rules table has East, West and a NULL row (for nullers): North is the one value no row covers
        Assert.Equal(1, (int)region["uncovered"]!);
        Assert.Equal("North", (string?)region["values"]![0]!["values"]!["region"]);
        Assert.Equal(1, (long)region["values"]![0]!["rows"]!);
        var statuses = report["rules"]!.AsArray().OfType<JsonObject>().Single(r => (string?)r["rule"] == "statuses");
        Assert.Equal(0, (int)statuses["uncovered"]!);
        // without the NULL row the NULL region is uncovered too; counted, listed as a null value
        f.Execute("DELETE FROM region_access WHERE region_code IS NULL");
        var again = Ok(f.Get(admin, GatewayAccessManagement.CoverageRoute, ("model", "orders_model"), ("dataset", "dim_customer")))["rules"]!.AsArray().Single()!;
        Assert.Equal(2, (int)again["uncovered"]!);
        Assert.Contains(again["values"]!.AsArray(), v => v!["values"]!["region"] is null);
        // the exclude rule's table has one status; Completed (five orders) is not in it
        var blocked = Ok(f.Get(admin, GatewayAccessManagement.CoverageRoute, ("model", "orders_model"), ("dataset", "fact_sales")))["rules"]!.AsArray().Single(r => (string?)r!["rule"] == "no_blocked")!;
        Assert.Equal("Completed", (string?)blocked["values"]![0]!["values"]!["status"]);
        Assert.Equal(5, (long)blocked["values"]![0]!["rows"]!);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_person_asks_only_about_themselves_and_never_learns_the_rows_of_another_and_coverage_is_an_administrators(string engine)
    {
        var f = Make(engine);
        var ann = f.Holding(ServerRole.Viewer, f.UserId("Ann"), "Ann");
        var own = Ok(f.Get(ann, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model")));
        Assert.Equal("Ann", (string?)own["subject"]);
        // what she gets are the rows that name her groups (east), none of emea's
        var text = own.ToJsonString();
        Assert.DoesNotContain("emea", text);
        Assert.DoesNotContain("nullers", text);
        // by her id as well as her name; never another person, a group, or someone else's id
        Assert.Equal(200, f.Get(ann, GatewayAccessManagement.ExplainRoute, ("subject", f.UserId("Ann")), ("model", "orders_model")).Status);
        Assert.Equal(403, f.Get(ann, GatewayAccessManagement.ExplainRoute, ("subject", "Bob"), ("model", "orders_model")).Status);
        Assert.Equal(403, f.Get(ann, GatewayAccessManagement.ExplainRoute, ("subject", "emea"), ("kind", "group"), ("model", "orders_model")).Status);
        Assert.Equal(403, f.Get(ann, GatewayAccessManagement.ExplainRoute, ("subject", "east"), ("kind", "group"), ("model", "orders_model")).Status);
        Assert.Equal(403, f.Get(ann, GatewayAccessManagement.CoverageRoute, ("model", "orders_model")).Status);
        // the local channel (the operating system's decision) holds every role
        Assert.Equal(200, f.Get(ManagementCaller.Local("root"), GatewayAccessManagement.CoverageRoute, ("model", "orders_model")).Status);
        // the list: names for a viewer, the rules for an administrator
        var names = Ok(f.Get(ann, GatewayAccessManagement.RulesRoute));
        Assert.Equal("orders_model", (string?)names["definitions"]![0]!["name"]);
        Assert.Null(names["definitions"]![0]!["row"]);
        Assert.Null(names["mask_key"]);
        var full = Ok(f.Get(f.Holding(ServerRole.Admin), GatewayAccessManagement.RulesRoute));
        Assert.Equal(2, full["definitions"]![0]!["row"]!.AsArray().Count);
        Assert.Equal(3, full["definitions"]![0]!["tables"]!.AsArray().Count);
        Assert.False((bool)full["mask_key"]!["exists"]!);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Questions_that_name_nothing_real_are_answered_in_words(string engine)
    {
        var f = Make(engine);
        var admin = f.Holding(ServerRole.Admin);
        Assert.Equal(404, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "nope")).Status);
        Assert.Equal(404, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "nope")).Status);
        Assert.Equal(400, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("model", "orders_model")).Status);
        Assert.Equal(400, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann")).Status);
        Assert.Equal(400, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("row", "status=Completed")).Status);   // a row needs its dataset
        Assert.Equal(400, f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"), ("dataset", "fact_sales"), ("row", "status")).Status);
        Assert.Equal(405, f.Api.Handle("POST", GatewayAccessManagement.ExplainRoute, admin).Status);
        // an administrator's explain and coverage are audited, a person's own is not an administrator's act
        f.Get(admin, GatewayAccessManagement.ExplainRoute, ("subject", "Ann"), ("model", "orders_model"));
        f.Get(admin, GatewayAccessManagement.CoverageRoute, ("model", "orders_model"));
        Assert.Equal(["access explain", "access coverage"], f.Audit.Entries.Select(e => e.Command));
    }

    /// <summary>The command line over the local channel: a channel server that answers as the host's does (the operating-system user holds every role), the real command, its JSON checked against the output schema.</summary>
    [Fact]
    public async Task The_explain_and_coverage_verbs_ask_the_running_gateway_and_their_json_satisfies_the_schema()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix socket's folder is chosen here; the Windows pipe has one name per machine");
        var f = Make("postgres");
        var socket = Path.Combine(_dir, "run");
        Directory.CreateDirectory(DbDataGateway.Server.Configuration.GatewayConfigFile.DirectoryIn(_dir));
        File.WriteAllText(Path.Combine(DbDataGateway.Server.Configuration.GatewayConfigFile.DirectoryIn(_dir), "server.yaml"), $"management: {{ localChannel: true, localChannelDirectory: '{socket}' }}\n");
        await using var server = new DbDataKit.Hosting.Local.LocalChannelServer(new DbDataKit.Hosting.Local.LocalChannelOptions("dbdatagateway") { Directory = socket }, (request, _) =>
        {
            var message = JsonNode.Parse(request)!.AsObject();
            var path = (string)message["path"]!;
            var query = System.Web.HttpUtility.ParseQueryString(path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "");
            var result = f.Api.Handle((string)message["method"]!, path.Split('?')[0], ManagementCaller.Local("root"), query);
            return Task.FromResult(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["status"] = result.Status, ["body"] = result.Body }));
        });
        server.Start();

        async Task<(int Exit, string Out, string Error)> Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await DbDataGateway.Server.Hosting.Cli.RunAsync([.. args, "--repo", _dir], new DbDataKit.Cli.CliContext(output, error, TextReader.Null));
            return (exit, output.ToString(), error.ToString());
        }

        // text: the rule that decided; `--row` takes the words that follow it
        var text = await Run("access", "explain", "Ann", "--model", "orders_model", "--dataset", "fact_sales", "--row", "status=Cancelled");
        Assert.Equal(0, text.Exit);
        Assert.Contains("rule 'visible/no_blocked' removes it", text.Out);
        Assert.Contains("Rules table status_access: fresh", text.Out);
        var two = await Run("access", "explain", "nullers", "--kind", "group", "--model", "orders_model", "--dataset", "dim_customer", "--row", "region=null");
        Assert.Contains("The row is included", two.Out);

        var json = await Run("access", "explain", "Ann", "--model", "orders_model", "--dataset", "fact_sales", "--row", "status=Completed", "--format", "json");
        Assert.Equal(0, json.Exit);
        using var document = System.Text.Json.JsonDocument.Parse(json.Out);
        var schema = Json.Schema.JsonSchema.FromText(DbDataGateway.Server.Hosting.Cli.Router().OutputSchema());
        Assert.True(schema.Evaluate(JsonNode.Parse(json.Out), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List }).IsValid, json.Out);
        Assert.Equal("fact_sales", document.RootElement.GetProperty("data").GetProperty("datasets")[0].GetProperty("dataset").GetString());

        var coverage = await Run("access", "coverage", "--model", "orders_model", "--format", "json");
        using var covered = System.Text.Json.JsonDocument.Parse(coverage.Out);
        Assert.True(schema.Evaluate(JsonNode.Parse(coverage.Out), new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List }).IsValid, coverage.Out);
        Assert.Contains("North", coverage.Out);
        Assert.Contains("1 uncovered", (await Run("access", "coverage", "--model", "orders_model", "--dataset", "dim_customer")).Out);

        // a usage error and a refusal in words
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Run("access", "explain", "--model", "orders_model")).Exit);
        Assert.Equal(DbDataKit.Cli.CliExit.Usage, (await Run("access", "coverage")).Exit);
        var nope = await Run("access", "explain", "Ann", "--model", "nope");
        Assert.NotEqual(0, nope.Exit);
        Assert.Contains("serves no model or entity model named 'nope'", nope.Error);
    }

    [Fact]
    public void The_row_arguments_read_column_equals_value_null_is_NULL_and_quotes_keep_the_text()
    {
        var row = GatewayAccessInspector.ParseRow(["region=EU", "channel=null", "note=\"null\"", "amount=10.5", "text=a=b"])!;
        Assert.Equal("EU", row["region"]);
        Assert.Null(row["channel"]);
        Assert.Equal("null", row["note"]);
        Assert.Equal("a=b", row["text"]);
        Assert.Throws<AccessInspectorException>(() => GatewayAccessInspector.ParseRow(["nothing"]));
        Assert.Null(GatewayAccessInspector.ParseRow([]));
    }
}
