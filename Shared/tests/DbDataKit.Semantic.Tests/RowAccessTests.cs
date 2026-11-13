using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Plan 22a GP11 (QG2): the rows a caller's rules leave, in the plan before it runs. The rules tables are real tables of the seed (DuckDB); the semi-join, the strict NULL, the exclude, the join the rules force and the members are checked on the answers,
/// and the text of each dialect is checked for the semi-join and for the absence of any value of the caller.
/// </summary>
public sealed class RowAccessTests
{
    private const string Tables = """
          - name: region_access
            x_source: sqlserver_sales
            source: region_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}
          - name: status_access
            x_source: sqlserver_sales
            source: status_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: status_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: status_code}]}}
          - name: blocked_status
            x_source: sqlserver_sales
            source: blocked_status
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: status_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: status_code}]}}
        """;

    private const string Access = """
        access:
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

    // FY27-Q1: West 500, East 400 (order 9, 100, is Cancelled), and a customer with no region (NULL) with one order of 40
    private const string Seed = """
        INSERT INTO dim_customer VALUES (5, 'C5', NULL);
        INSERT INTO fact_sales (order_id, customer_key, product_key, date_key, net_amount, status, ship_date_key) VALUES (15, 5, 1, 20260701, 40, 'Completed', 20260801);
        CREATE TABLE region_access (who VARCHAR, region_code VARCHAR);
        INSERT INTO region_access VALUES ('east', 'East'), ('east', 'East'), ('emea', 'East'), ('emea', 'West'), ('nullers', NULL), ('regional', 'West');
        CREATE TABLE status_access (who VARCHAR, status_code VARCHAR);
        INSERT INTO status_access VALUES ('east', 'Completed'), ('east', 'Cancelled'), ('emea', 'Completed'), ('emea', 'Cancelled'), ('nullers', 'Completed');
        CREATE TABLE blocked_status (who VARCHAR, status_code VARCHAR);
        INSERT INTO blocked_status VALUES ('east', 'Cancelled');
        """;

    private static readonly Catalog Sales = CatalogLoader.LoadText(
        File.ReadAllText(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml")).Replace("\ntables:\n", "\ntables:\n" + Tables.Replace("\r\n", "\n").TrimStart('\n').TrimEnd() + "\n", StringComparison.Ordinal).TrimEnd() + "\n" + Access, "sales.catalog.yaml");

    private sealed class Ledger : IRulesTableLedger
    {
        public RulesTableRecord Touch(string key, DateTimeOffset now) => new(key, now, now);

        public void Changed(string key, DateTimeOffset at, string product, string reason) { }
    }

    private sealed class Subjects : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column) => table == "blocked_status" ? ["east"] : ["east", "emea", "nullers", "regional"];
    }

    private static AccessDecision Decide(params string[] groups) =>
        new AccessResolver(new Ledger()).Resolve(new AccessCaller("u1", "u1@example.com", groups), new AccessScope("semantic-model/sales", Sales.Access!, t => t, new Subjects()));

    private static JsonObject Request(params string[] attributes) => new()
    {
        ["spec_version"] = "0.3", ["kind"] = "query",
        ["query"] = new JsonObject
        {
            ["measures"] = new JsonArray("orders.net_revenue"),
            ["attributes"] = new JsonArray([.. attributes.Select(a => (JsonNode)a)]),
            ["filters"] = new JsonArray(new JsonObject { ["attribute"] = "date.fiscal_quarter", ["operator"] = "equals", ["values"] = new JsonArray("FY27-Q1") }),
            ["limit"] = 100,
        },
    };

    private static DuckDbStandInExecutor Executor() => new(ConformanceTests.Seed + Seed);

    private static QisEngine Engine(DuckDbStandInExecutor executor, AccessDecision? decision) =>
        new(decision is null ? Sales : Sales.Restricted(RowAccessPlan.Build(Sales, decision)), executor, new Validator(Sales), 1);

    /// <summary>The answer as <c>attribute=revenue</c> text ordered by the attribute; with no attribute, the total.</summary>
    private static List<string> Answer(AccessDecision? decision, params string[] attributes)
    {
        using var executor = Executor();
        var result = Engine(executor, decision).Run(Request(attributes));
        Assert.Equal("complete", result.Status);
        return [.. result.Combined!.Rows.Select(r => (attributes.Length == 0 ? "total" : r[Catalog.ColName(attributes[0])]?.ToString() ?? "NULL") + "=" + Convert.ToDecimal(r["orders_net_revenue"], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void With_no_plan_every_row_is_read()
    {
        Assert.Equal(["East=400", "NULL=40", "West=500"], Answer(null, "customers.region"));
        Assert.Equal(["total=940"], Answer(null));
    }

    [Fact]
    public void A_group_sees_the_rows_its_rules_table_gives_an_exclude_removes_rows_and_a_repeated_rule_row_never_multiplies_them()
    {
        // east has the row East twice in region_access; Cancelled is an include row of status_access and an exclude row of blocked_status: order 9 (100) goes
        Assert.Equal(["East=300"], Answer(Decide("east"), "customers.region"));
        // emea: two regions, nothing excluded
        Assert.Equal(["East=400", "West=500"], Answer(Decide("emea"), "customers.region"));
        // two callers, the same request, two answers
        Assert.NotEqual(Answer(Decide("east"), "customers.region"), Answer(Decide("emea"), "customers.region"));
    }

    [Fact]
    public void A_caller_with_no_matching_rule_row_sees_no_row_and_so_does_one_who_misses_a_table_of_the_dataset()
    {
        Assert.Empty(Answer(Decide("nobody"), "customers.region"));
        Assert.Equal(["total=0"], Answer(Decide("nobody")));   // an empty total, not the total of everything
        // `regional` has a row for West in region_access and none in status_access: the include rule of fact_sales never holds
        Assert.Empty(Answer(Decide("regional"), "customers.region"));
    }

    [Fact]
    public void A_null_rule_row_matches_only_a_null_value_and_a_value_without_one_is_never_matched()
    {
        Assert.Equal(["NULL=40"], Answer(Decide("nullers"), "customers.region"));
        // east has no NULL row: the customer with no region is not theirs, and an exclude keeps what it does not name
        Assert.DoesNotContain(Answer(Decide("east"), "customers.region"), r => r.StartsWith("NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_apply_to_totals_and_to_a_request_that_never_names_the_filtered_table()
    {
        Assert.Equal(["total=300"], Answer(Decide("east")));
        Assert.Equal(["total=900"], Answer(Decide("emea")));
        // by product: the customer table is joined all the same, so the rows are the East ones: A 100, B 200
        Assert.Equal(["A=100", "B=200"], Answer(Decide("east"), "products.product"));
    }

    [Fact]
    public void The_members_of_an_attribute_are_the_ones_the_filtered_rows_hold()
    {
        using var executor = Executor();
        Assert.Contains("North", Engine(executor, null).Members("customers.region").Members);   // read whole from the dimension table
        Assert.Equal(["East"], Engine(executor, Decide("east")).Members("customers.region").Members);
    }

    [Fact]
    public void A_table_the_rules_refuse_stops_the_request()
    {
        var refused = Decide("east") with { Datasets = [.. Decide("east").Datasets.Select(d => d with { Refusal = "the rules table 'region_access' is past its age" })] };
        using var executor = Executor();
        var error = Assert.Throws<RowAccessRefusedException>(() => Engine(executor, refused).Run(Request("customers.region")));
        Assert.Contains("past its age", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tsql")]
    [InlineData("postgres")]
    [InlineData("duckdb")]
    public void The_plan_of_each_dialect_has_the_semi_join_and_no_value_of_the_caller(string dialect)
    {
        var restricted = Sales.Restricted(RowAccessPlan.Build(Sales, Decide("east")));
        var engine = dialect switch { "tsql" => EmitDialects.TSql, "postgres" => EmitDialects.Postgres, _ => EmitDialects.DuckDb };
        var (text, parameters) = SqlEmitter.BuildSelect(restricted, "sqlserver_sales", ["customers.region"], ["orders.net_revenue"], [], [], null, engine);
        Assert.Contains("EXISTS (SELECT 1 FROM region_access ra1 WHERE ra1.who IN (", text, StringComparison.Ordinal);
        Assert.Contains("EXISTS (SELECT 1 FROM status_access", text, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", text.Replace("(NOT EXISTS", "NOT EXISTS"), StringComparison.Ordinal);
        Assert.Contains("JOIN dim_customer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'east'", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("east", parameters.OfType<string>());
        if (dialect == "tsql") Assert.DoesNotContain("IS NOT DISTINCT FROM", text, StringComparison.Ordinal);
        else Assert.Contains("IS NOT DISTINCT FROM", text, StringComparison.Ordinal);
        // a text that holds no rule is exactly what it was
        Assert.DoesNotContain("EXISTS", SqlEmitter.BuildSelect(Sales, "sqlserver_sales", ["customers.region"], ["orders.net_revenue"], [], [], null, engine).Text, StringComparison.Ordinal);
    }

    // the row rules and the column rules of one model together (plan 22a GP10, GP11, GP13, QG2): the rule is on dim_customer.region and the same column is hidden, nulled or masked for the caller
    private static readonly Catalog WithColumnRules = CatalogLoader.LoadText(
        File.ReadAllText(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml")).Replace("\ntables:\n", "\ntables:\n" + Tables.Replace("\r\n", "\n").TrimStart('\n').TrimEnd() + "\n", StringComparison.Ordinal).TrimEnd() + "\n" + Access + "\n" + """
          columns:
            - { subject: hiders, dataset: dim_customer, column: region, treatment: hide }
            - { subject: nullers_of_region, dataset: dim_customer, column: region, treatment: "null" }
            - { subject: maskers, dataset: dim_customer, column: region, treatment: mask }
            - { subject: hashers, dataset: dim_customer, column: region, treatment: mask, mask: hash }
        """, "sales.catalog.yaml");

    /// <summary>The caller's catalog as the serving core makes it: the column view of the model, then restricted by the row plan built on that catalog.</summary>
    private static (Catalog Catalog, CatalogColumnView View) Both(params string[] groups)
    {
        var view = CatalogColumnView.For(WithColumnRules, ColumnTreatments.For(WithColumnRules.Access, "u1", "u1@example.com", groups));
        var decision = new AccessResolver(new Ledger()).Resolve(new AccessCaller("u1", "u1@example.com", groups), new AccessScope("semantic-model/sales", WithColumnRules.Access!, t => t, new Subjects()));
        return (view.Catalog.Restricted(RowAccessPlan.Build(view.Catalog, decision)), view);
    }

    private static List<string> AnswerBoth(string[] groups, params string[] attributes)
    {
        var (catalog, _) = Both(groups);
        using var executor = Executor();
        var result = new QisEngine(catalog, executor, new Validator(catalog), 1).Run(Request(attributes));
        Assert.Equal("complete", result.Status);
        return [.. result.Combined!.Rows.Select(r => (attributes.Length == 0 ? "total" : r[Catalog.ColName(attributes[0])]?.ToString() ?? "NULL") + "=" + Convert.ToDecimal(r["orders_net_revenue"], CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void A_row_rule_on_a_column_that_is_nulled_for_the_caller_still_filters_on_the_raw_value()
    {
        // east sees East rows only (order 9 is excluded): 300, shown as one group of NULL regions
        Assert.Equal(["NULL=300"], AnswerBoth(["east", "nullers_of_region"], "customers.region"));
        Assert.Equal(["total=300"], AnswerBoth(["east", "nullers_of_region"]));
    }

    [Fact]
    public void A_row_rule_on_a_column_that_is_masked_for_the_caller_still_filters_on_the_raw_value()
    {
        Assert.Equal(["********=300"], AnswerBoth(["east", "maskers"], "customers.region"));
        Assert.Equal(["total=300"], AnswerBoth(["east", "maskers"]));
        // emea has both regions: still two rows' worth, one masked group
        Assert.Equal(["********=900"], AnswerBoth(["emea", "maskers"], "customers.region"));
    }

    [Fact]
    public void A_row_rule_on_a_column_that_is_hashed_or_hidden_for_the_caller_still_filters_on_the_raw_value()
    {
        // hash: the engine reads the raw value (grouped by it) and the answer replaces it; the filter is on the raw one
        Assert.Equal(["East=300"], AnswerBoth(["east", "hashers"], "customers.region"));
        Assert.Contains("customers.region", Both("east", "hashers").View.Hashed);
        // hide: the attribute is not there, the rows are the East ones all the same
        Assert.Equal(["total=300"], AnswerBoth(["east", "hiders"]));
        Assert.Equal(["total=900"], AnswerBoth(["emea", "hiders"]));
        Assert.DoesNotContain("customers.region", Both("east", "hiders").Catalog.Doc.Attributes.Keys);
    }
}
