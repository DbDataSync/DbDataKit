using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataKit.Dialect;
using DuckDB.NET.Data;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>A surrogate key is a function (plan 25 section 1a, slice K03): the owning model defines it in `keys:`, and every query calls it by name.</summary>
public class KeyFunctionTests
{
    private static void Write(string dir, string path, string text)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        TestFiles.WriteAllText(full, text);
    }

    private static string Project(string keys, string factQuery = "SELECT customer_key(source_system, customer_id) AS customer_key FROM (SELECT 'SAP' AS source_system, 42 AS customer_id)\n")
    {
        var dir = NewProjectDir();
        Write(dir, "dbdatabuild.yml", "defaults: {connections: [sqlserver]}\ntracking: none\n");
        Write(dir, "models/marts/dim_customer.yml", "name: marts.dim_customer\nkind: view\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n  - {name: customer_key, type: BIGINT}\n" + keys);
        Write(dir, "models/marts/dim_customer.sql", "SELECT customer_key(source_system, customer_id) AS customer_key, source_system, customer_id FROM (SELECT 'SAP' AS source_system, 42 AS customer_id)\n");
        Write(dir, "models/marts/fct_sales.yml", "name: marts.fct_sales\nkind: view\ncolumns:\n  - {name: customer_key, type: BIGINT}\n");
        Write(dir, "models/marts/fct_sales.sql", factQuery);
        return dir;
    }

    private const string CustomerKey = "keys:\n  customer_key: {from: [source_system, customer_id]}\n";

    private static long? RunOnDuckDb(string sql)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var v = command.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    [Fact]
    public void A_query_calls_the_key_by_name_and_gets_the_reference_value()
    {
        var ctx = ProjectContext.Load(Project(CustomerKey));
        Assert.DoesNotContain(ctx.Diagnostics, d => d.Severity == Severity.Error);
        var built = ctx.Project.BuiltModels.Single(m => m.Definition.Name == "marts.fct_sales");
        var (sql, error) = ctx.Lowering.LowerForDuckDb(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(error);
        Assert.Equal(SurrogateKey.Compute("SAP", 42L), RunOnDuckDb(sql!));
    }

    private static (int Exit, string Err) Compile(string dir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(["project", "compile", "--project", dir], o, e, environment: _ => null), e.ToString());
    }

    [Theory]
    [InlineData("sqlserver", "HASHBYTES('SHA2_256'")]
    [InlineData("postgres", "sha256(convert_to(")]
    public void A_key_call_is_written_as_the_engines_hash_over_its_arguments(string engine, string hash)
    {
        var dir = Project(CustomerKey);
        TestFiles.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), $"defaults: {{connections: [{engine}]}}\ntracking: none\nstring_semantics:\n  case: sensitive\n  trailing_space: {(engine == "postgres" ? "significant" : "ignored")}\n  collations:\n    default: {{ duckdb: NFC, {engine}: {(engine == "postgres" ? "en_US.utf8" : "Latin1_General_100_CS_AS_SC_UTF8")} }}\n");
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_sales.yml"), File.ReadAllText(Path.Combine(dir, "models/marts/fct_sales.yml")).Replace("kind: view", "kind: full"));
        var (exit, err) = Compile(dir);
        Assert.True(exit == 0, err);
        var script = File.ReadAllText(Path.Combine(dir, "rendered", engine, "marts.fct_sales", "load.default.sql"));
        Assert.Contains(hash, script);
        Assert.DoesNotContain("ddb_key_", script);
        Assert.Contains("IS NULL", script);
        // the lowered query keeps the call, so the text of each engine is written from it
        Assert.Contains("ddb_key_ti", File.ReadAllText(Path.Combine(dir, "rendered", "lowered", "marts.fct_sales", "lowered.sql")).Replace("ddb_key_tt", "ddb_key_ti"));
    }

    [Fact]
    public void The_marker_is_replaced_whatever_its_arguments_hold()
    {
        // a comma and a parenthesis inside a string, a call inside an argument, and a second marker in the same text
        var text = "SELECT ddb_key_tid(f(a, ')'), 'x,y(', CAST(d AS DATE)) AS k, ddb_key_t(b) AS j FROM t";
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var finished = DbDataBuild.Targets.Rules.TargetRules.Finish(text, target);
            Assert.DoesNotContain("ddb_key_", finished);
            Assert.Contains("f(a, ')') IS NULL", finished);
            Assert.Contains("'x,y(' IS NULL", finished);
        }
        Assert.Equal(text, DbDataBuild.Targets.Rules.TargetRules.Finish(text, "oracle"));          // a target with no definition is left alone, and says so on the engine
    }

    [Fact]
    public void A_marker_names_its_part_types_and_refuses_a_wrong_argument_count()
    {
        Assert.Equal("ddb_key_tid", SurrogateKey.MarkerName([KeyPartType.Text, KeyPartType.Integer, KeyPartType.Date]));
        Assert.Equal([KeyPartType.Text, KeyPartType.Date], SurrogateKey.TypesOfMarker("DDB_KEY_TD"));
        Assert.Null(SurrogateKey.TypesOfMarker("ddb_key_tx"));
        Assert.Null(SurrogateKey.TypesOfMarker("ddb_key_"));
        Assert.Null(SurrogateKey.FromMarker("postgres", "ddb_key_ti", ["a"]));
    }

    [Fact]
    public void A_call_with_the_wrong_number_of_arguments_is_refused_naming_the_arguments()
    {
        var ctx = ProjectContext.Load(Project(CustomerKey, "SELECT customer_key(customer_id) AS customer_key FROM (SELECT 42 AS customer_id)\n"));
        var built = ctx.Project.BuiltModels.Single(m => m.Definition.Name == "marts.fct_sales");
        var (model, error) = ctx.Lowering.Lower(built, File.ReadAllText(Path.Combine(ctx.Root, built.QueryFile)));
        Assert.Null(model);
        Assert.Contains("`customer_key` takes 2 arguments (`source_system`, `customer_id`), but this call passes 1", error!.Found);
    }

    [Fact]
    public void A_comma_inside_an_argument_is_not_a_second_argument()
    {
        var keys = new List<Diagnostic>();
        var library = MacroLibrary.Empty.WithKeys([(Definition(CustomerKey), "models/marts/dim_customer.yml")], keys);
        Assert.Empty(keys);
        Assert.Empty(library.KeyCallProblems("SELECT customer_key(coalesce(a, 'x,y'), f(b, c)) FROM t"));
        Assert.Single(library.KeyCallProblems("SELECT customer_key(a, b, c) FROM t"));
        Assert.Single(library.KeyCallProblems("SELECT customer_key() FROM t"));
        Assert.Empty(library.KeyCallProblems("SELECT 'customer_key(a)' FROM t -- customer_key(1)"));
    }

    [Fact]
    public void Two_models_cannot_own_the_same_key_name()
    {
        var dir = Project(CustomerKey);
        Write(dir, "models/marts/dim_other.yml", "name: marts.dim_other\nkind: view\ncolumns:\n  - {name: x, type: BIGINT}\n" + CustomerKey);
        Write(dir, "models/marts/dim_other.sql", "SELECT 1 AS x\n");
        var result = ProjectValidator.Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Found.Contains("`customer_key` is defined in", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_named_like_a_duckdb_function_is_refused()
    {
        var ctx = ProjectContext.Load(Project("keys:\n  md5: {from: [source_system]}\n", "SELECT 1 AS customer_key\n"));
        Assert.Contains(ctx.Diagnostics, d => d.Found.Contains("`md5` has the name of a DuckDB function", StringComparison.Ordinal));
    }

    [Fact]
    public void A_history_key_has_a_version_function_that_takes_the_valid_from_too()
    {
        var keys = "keys:\n  product_key: {from: [source_system], history: customer_id, version_key: product_version_key}\n";
        var diags = new List<Diagnostic>();
        var library = MacroLibrary.Empty.WithKeys([(Definition(keys), "models/marts/dim_customer.yml")], diags);
        Assert.Empty(diags);
        Assert.Empty(library.KeyCallProblems("SELECT product_key(a), product_version_key(a, b)"));
        Assert.Single(library.KeyCallProblems("SELECT product_version_key(a)"));
    }

    private static ModelDefinition Definition(string keys)
    {
        var diags = new List<Diagnostic>();
        var model = ModelDefinitionLoader.Load("name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\"}\n  - {name: customer_id, type: BIGINT}\n" + keys, "models/marts/dim_customer.yml", "marts.dim_customer", diags);
        Assert.Empty(diags);
        return model!;
    }
}

public class KeyRelationshipTests
{
    private static ModelDefinition Model(string name, string keys = "")
    {
        var diags = new List<Diagnostic>();
        var m = ModelDefinitionLoader.Load($"name: {name}\nkind: view\ncolumns:\n  - {{name: a, type: BIGINT}}\n{keys}", "models/x.yml", name, diags);
        Assert.Empty(diags);
        return m!;
    }

    [Fact]
    public void A_call_with_an_alias_is_a_relationship_and_the_column_name_is_the_role()
    {
        var date = Model("marts.dim_date", "keys:\n  date_key: {from: [d]}\n");
        var fact = Model("marts.fct_sales");
        var sql = "SELECT date_key(o.order_date) AS order_date_key, date_key(o.return_date) AS return_date_key, date_key(o.d) AS date_key, date_key(o.x) + 1 FROM o WHERE date_key(o.q) = 1";
        var found = KeyRelationships.Infer([(fact, sql), (date, "SELECT date_key(d) AS date_key FROM t")], [date, fact]);
        Assert.Equal([("date_key", (string?)null), ("order_date_key", "order_date_key"), ("return_date_key", "return_date_key")], found.Select(r => (r.Column, r.Role)));
        Assert.All(found, r => { Assert.Equal("marts.dim_date", r.Owner); Assert.Equal("marts.fct_sales", r.Model); });
        Assert.Equal(3, found.Count);
    }
}
