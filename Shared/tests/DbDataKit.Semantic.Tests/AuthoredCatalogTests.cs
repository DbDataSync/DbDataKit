using DbDataKit.Access;
using DbDataKit.Semantic.Authoring;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// A semantic model written without its physical layer (the form a DbDataBuild project keeps in <c>semantic/</c>): the layer is generated from the host's models and every
/// error comes back at the file and line the person wrote it (plan 19 J07, J08).
/// </summary>
public sealed class AuthoredCatalogTests
{
    private sealed class Layer(params (string Model, string Source, PhysicalTable Table)[] tables) : IPhysicalLayer
    {
        public string? SourceKind(string source) => source is "warehouse" or "reporting" ? "sqlserver" : null;
        public bool KnowsModel(string model) => tables.Any(t => t.Model == model);
        public PhysicalTable? Table(string model, string source) => tables.FirstOrDefault(t => t.Model == model && t.Source == source).Table;
    }

    private static PhysicalTable Fact => new("marts.fct_sales", [new("order_id", "Integer"), new("customer_id", "Integer"), new("amount", "Decimal"), new("ordered_on", "Date")], ["order_id"]);
    private static PhysicalTable Customers => new("marts.dim_customer", [new("customer_id", "Integer"), new("region", "String")], ["customer_id"]);

    private static Layer Standard => new(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", Customers));

    private const string Model = """
        version: "0.3"
        name: sales
        relationships:
          - {name: sales_to_customer, from: marts.fct_sales, to: marts.dim_customer, from_columns: [customer_id], to_columns: [customer_id]}
        dimensions:
          customers:
            attributes:
              region:
                type: string
                bindings:
                  warehouse: {table: marts.dim_customer, column: region}
          orders:
            attributes:
              ordered_on:
                type: date
                bindings:
                  warehouse: {table: marts.fct_sales, column: ordered_on}
        measures:
          orders.revenue:
            type: number
            source: warehouse
            agg: sum
            of: {table: marts.fct_sales, column: amount}
        """;

    private static (AuthoredCatalog? Read, List<AuthoredProblem> Problems) Read(string yaml)
    {
        var problems = new List<AuthoredProblem>();
        return (AuthoredCatalog.Read(yaml, "semantic/sales.yml", problems), problems);
    }

    private static (AuthoredCompilation? Result, List<AuthoredProblem> Problems) Compile(string yaml, IPhysicalLayer? layer = null)
    {
        var (read, problems) = Read(yaml);
        Assert.True(read is not null, string.Join("\n", problems));
        var result = read.Compile(layer ?? Standard, problems);
        return (result, problems);
    }

    [Fact]
    public void A_model_without_a_physical_layer_compiles_against_the_hosts_models()
    {
        var (result, problems) = Compile(Model);
        Assert.Empty(problems);
        Assert.NotNull(result);
        Assert.Equal(["marts.dim_customer", "marts.fct_sales"], result.Tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal("sqlserver", result.Sources["warehouse"]);
        Assert.Contains("customers.region", result.Catalog.Doc.Attributes.Keys);
        Assert.Contains("orders.revenue", result.Catalog.Doc.Measures.Keys);
        var fact = result.Catalog.Doc.Sources["warehouse"];
        Assert.Equal("marts.fct_sales", fact.Fact.Table);
        Assert.Single(result.Relationships);
    }

    private static PhysicalTable Rules => new("rules.region_access", [new("who", "String"), new("region_code", "String")], ["who"]);

    private const string Access = """
        access:
          row:
            - dataset: marts.dim_customer
              groups:
                - id: by_region
                  rules:
                    - {id: r1, effect: include, table: rules.region_access, subject: who, columns: {region: region_code}}
          columns:
            - {subject: interns, dataset: marts.dim_customer, column: region, treatment: hide}
        """;

    [Fact]
    public void The_access_section_names_models_and_the_rules_table_becomes_a_generated_table()
    {
        var layer = new Layer(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", Customers), ("rules.region_access", "warehouse", Rules));
        var (result, problems) = Compile(Model + "\n" + Access, layer);
        Assert.Empty(problems);
        Assert.Contains("rules.region_access", result!.Tables.Select(t => t.Name));
        var access = result.Catalog.Access!;
        Assert.Equal(2, result.Catalog.Format);
        Assert.Equal("marts.dim_customer", access.Row.Single().Dataset);
        Assert.Equal("rules.region_access", access.Row.Single().Groups.Single().Rules.Single().Table);
        Assert.Equal(ColumnTreatment.Hide, access.Columns.Single().Treatment);
    }

    [Fact]
    public void A_model_on_two_sources_gets_rules_of_its_own_on_each()
    {
        var layer = new Layer(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", Customers), ("marts.dim_customer", "reporting", Customers), ("marts.fct_sales", "reporting", Fact),
            ("rules.region_access", "warehouse", Rules), ("rules.region_access", "reporting", Rules));
        var yaml = Model.Replace("warehouse: {table: marts.dim_customer, column: region}", "warehouse: {table: marts.dim_customer, column: region}\n          reporting: {table: marts.dim_customer, column: region}") + "\n  orders.revenue_reporting: {type: number, source: reporting, agg: sum, of: {table: marts.fct_sales, column: amount}}\n" + Access;
        var (result, problems) = Compile(yaml, layer);
        Assert.Empty(problems);
        var access = result!.Catalog.Access!;
        Assert.Equal(["marts.dim_customer@reporting", "marts.dim_customer@warehouse"], access.Row.Select(r => r.Dataset).Order());
        Assert.Equal(["rules.region_access@reporting", "rules.region_access@warehouse"], access.Row.SelectMany(r => r.Groups).SelectMany(g => g.Rules).Select(r => r.Table).Order());
        Assert.Equal(["r1.reporting", "r1.warehouse"], access.Row.SelectMany(r => r.Groups).SelectMany(g => g.Rules).Select(r => r.Id).Order());
        Assert.Equal(2, access.Columns.Count);
    }

    [Fact]
    public void A_rules_table_that_is_not_a_model_or_not_built_where_its_dataset_is_a_problem_at_its_line()
    {
        var yaml = Model + "\n" + Access;
        var (read, problems) = Read(yaml);
        Assert.NotNull(read);
        Assert.Null(read.Compile(Standard, problems));
        var unknown = Assert.Single(problems);
        Assert.Equal(("unknown_model", LineOf(yaml, "table: rules.region_access")), (unknown.Code, unknown.Line));

        problems.Clear();
        var onlyOnReporting = new Layer(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", Customers), ("rules.region_access", "reporting", Rules));
        Assert.Null(read.Compile(onlyOnReporting, problems));
        Assert.Contains(problems, p => p.Code is "rules_table_not_on_source" or "unknown_access_table" or "invalid_access");
    }

    [Fact]
    public void A_bad_access_section_is_a_problem_at_the_line_it_is_written()
    {
        var yaml = Model + "\naccess:\n  row:\n    - dataset: marts.dim_customer\n      groups:\n        - id: g\n          rules:\n            - {id: r, effect: exclude, table: rules.region_access, subject: who, columns: {region: region_code}}\n";
        var (read, problems) = Read(yaml);
        Assert.Null(read);
        var problem = problems.First(p => p.Code is "invalid_access" or "semantic_schema_invalid" || p.Message.Contains("include", StringComparison.Ordinal));
        Assert.True(problem.Line >= LineOf(yaml, "access:"), problem.ToString());
    }

    private const string SavedQueries = """
        queries:
          revenue_by_region:
            description: Revenue by region
            sql: SELECT customers.region, MEASURE(orders.revenue) FROM sales GROUP BY ALL LIMIT 100
          revenue_for_region:
            sql: SELECT customers.region, MEASURE(orders.revenue) FROM sales WHERE customers.region = :region GROUP BY ALL LIMIT 100
            parameters:
              region: {for: customers.region, type: string}
        """;

    [Fact]
    public void Saved_queries_written_in_the_project_are_compiled_into_the_catalog_the_server_is_given()
    {
        var (result, problems) = Compile(Model + "\n" + SavedQueries);

        Assert.Empty(problems);
        Assert.Equal(["revenue_by_region", "revenue_for_region"], result!.Catalog.Doc.Queries.Keys.Order().ToArray());
        Assert.Equal(["revenue_by_region", "revenue_for_region"], result.Json["queries"]!.AsObject().Select(q => q.Key).Order().ToArray());
        // what the server loads is the same catalog, saved queries included
        Assert.Equal(2, CatalogLoader.LoadText(result.Json.ToJsonString()).Doc.Queries.Count);
    }

    [Fact]
    public void A_saved_query_that_does_not_translate_is_a_problem_naming_it()
    {
        var yaml = Model + "\nqueries:\n  broken:\n    sql: SELECT nonsense FROM\n";
        var (read, problems) = Read(yaml);
        Assert.NotNull(read);
        var result = read.Compile(Standard, problems);

        Assert.Null(result);
        Assert.Equal(LineOf(yaml, "sql: SELECT nonsense"), Assert.Single(problems).Line);   // at the line of the saved query, as every other problem is
    }

    [Fact]
    public void A_model_that_is_not_in_the_project_is_a_problem_at_the_line_that_names_it()
    {
        var (result, problems) = Compile(Model.Replace("table: marts.dim_customer, column: region", "table: marts.dim_custmer, column: region"));
        Assert.Null(result);
        var problem = problems.First(p => p.Code == "unknown_model");
        Assert.Equal("semantic/sales.yml", problem.File);
        Assert.Equal(LineOf(Model.Replace("marts.dim_customer, column: region", "marts.dim_custmer, column: region"), "marts.dim_custmer, column"), problem.Line);
        Assert.Contains("marts.dim_custmer", problem.Message);
    }

    [Fact]
    public void A_column_the_model_lacks_is_a_problem_at_the_column()
    {
        var yaml = Model.Replace("column: region", "column: territory");
        var (result, problems) = Compile(yaml);
        Assert.Null(result);
        var problem = Assert.Single(problems);
        Assert.Equal("unknown_column", problem.Code);
        Assert.Equal(LineOf(yaml, "column: territory"), problem.Line);
        Assert.Contains("territory", problem.Message);
    }

    [Fact]
    public void A_measure_with_a_dotted_name_is_found_by_its_whole_name()
    {
        var yaml = Model.Replace("column: amount", "column: amout");
        var (_, problems) = Compile(yaml);
        Assert.Equal(LineOf(yaml, "column: amout"), Assert.Single(problems).Line);
    }

    [Fact]
    public void A_type_that_disagrees_with_the_column_is_a_problem()
    {
        var yaml = Model.Replace("type: date", "type: string");
        var (result, problems) = Compile(yaml);
        Assert.Null(result);
        Assert.Contains(problems, p => p.Code == "binding_type_mismatch" && p.Line == LineOf(yaml, "warehouse: {table: marts.fct_sales, column: ordered_on}"));
    }

    [Fact]
    public void A_relationship_to_a_model_without_that_key_is_not_many_to_one()
    {
        var customers = new PhysicalTable("marts.dim_customer", Customers.Columns, []);
        var (result, problems) = Compile(Model, new Layer(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", customers)));
        Assert.Null(result);
        var problem = Assert.Single(problems);
        Assert.Equal("relationship_not_many_to_one", problem.Code);
        Assert.Equal(LineOf(Model, "- {name: sales_to_customer"), problem.Line);
    }

    [Fact]
    public void A_source_that_is_not_a_connection_is_a_problem()
    {
        var (_, problems) = Compile(Model.Replace("source: warehouse", "source: lake"));
        Assert.Contains(problems, p => p.Code == "unknown_source" && p.Message.Contains("lake"));
    }

    [Fact]
    public void A_model_that_is_not_on_the_source_a_binding_names_is_a_problem_at_that_binding()
    {
        var yaml = Model.Replace("warehouse: {table: marts.dim_customer", "reporting: {table: marts.dim_customer");
        var (_, problems) = Compile(yaml);
        var problem = Assert.Single(problems, p => p.Code == "model_not_on_source");
        Assert.Equal(LineOf(yaml, "reporting: {table: marts.dim_customer"), problem.Line);
    }

    [Fact]
    public void A_model_on_two_sources_gets_a_table_on_each_and_the_relationships_follow()
    {
        var layer = new Layer(("marts.fct_sales", "warehouse", Fact), ("marts.dim_customer", "warehouse", Customers), ("marts.fct_sales", "reporting", Fact), ("marts.dim_customer", "reporting", Customers));
        var yaml = Model.Replace("warehouse: {table: marts.dim_customer, column: region}", "warehouse: {table: marts.dim_customer, column: region}\n          reporting: {table: marts.dim_customer, column: region}");
        yaml += "\n  orders.revenue_reporting: {type: number, source: reporting, agg: sum, of: {table: marts.fct_sales, column: amount}}";
        var (result, problems) = Compile(yaml, layer);
        Assert.Empty(problems);
        Assert.NotNull(result);
        Assert.Contains("marts.dim_customer@reporting", result.Tables.Select(t => t.Name));
        Assert.Equal(["reporting", "warehouse"], result.Relationships.Select(r => r.Source).Order().ToArray());
    }

    [Fact]
    public void The_physical_layer_is_not_written()
    {
        var (read, problems) = Read(Model + "\ntables:\n  - {name: x, source: x, x_source: warehouse}\n");
        Assert.Null(read);
        Assert.Equal("physical_layer_is_generated", Assert.Single(problems).Code);
        var (_, kind) = Read(Model + "\nsources:\n  warehouse: {kind: sqlserver}\n");
        Assert.Contains(kind, p => p.Code == "physical_layer_is_generated");
    }

    [Fact]
    public void A_syntax_error_and_an_unknown_key_name_the_file_and_the_line()
    {
        var (read, problems) = Read("name: sales\nversion: [unclosed\n");
        Assert.Null(read);
        Assert.Equal("semantic_yaml_invalid", problems[0].Code);
        Assert.Equal("semantic/sales.yml", problems[0].File);
        Assert.True(problems[0].Line >= 1);

        var yaml = Model.Replace("agg: sum", "agg: sum\n    colour: red");
        var (schemaRead, schemaProblems) = Read(yaml);
        Assert.Null(schemaRead);
        Assert.Contains(schemaProblems, p => p.Code == "semantic_schema_invalid" && p.Line > 1);
    }

    [Fact]
    public void A_period_column_that_is_not_a_date_is_marked_on_its_binding()
    {
        var fact = new PhysicalTable("marts.fct_sales", [new("order_id", "Integer"), new("month", "String"), new("amount", "Decimal")], ["order_id"]);
        var yaml = """
            version: "0.3"
            name: sales
            dimensions:
              orders:
                attributes:
                  month:
                    type: string
                    bindings:
                      warehouse: {table: marts.fct_sales, column: month, is_time: true}
            measures:
              orders.revenue: {type: number, source: warehouse, agg: sum, of: {table: marts.fct_sales, column: amount}}
            """;
        var (result, problems) = Compile(yaml, new Layer(("marts.fct_sales", "warehouse", fact)));
        Assert.Empty(problems);
        Assert.True(result!.Catalog.Doc.Attributes["orders.month"].IsTime);
    }

    [Fact]
    public void The_authoring_schema_is_the_catalog_schema_without_its_physical_layer()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(AuthoringSchema.Text);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("properties").TryGetProperty("tables", out _));
        Assert.DoesNotContain("tables", root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(root.GetProperty("$defs").GetProperty("binding").GetProperty("properties").TryGetProperty("is_time", out _));
        Assert.False(root.GetProperty("$defs").TryGetProperty("table", out _));
        Assert.EndsWith("\n", AuthoringSchema.Text);
        Assert.DoesNotContain("\r", AuthoringSchema.Text);
    }

    private static int LineOf(string text, string needle)
    {
        var lines = text.Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal));
        Assert.True(index >= 0, $"`{needle}` is not in the text");
        return index + 1;
    }
}
