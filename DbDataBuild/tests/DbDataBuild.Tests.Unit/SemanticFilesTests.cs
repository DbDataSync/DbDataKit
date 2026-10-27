using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Semantic;
using DbDataKit.Semantic.Authoring;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Semantic files in a project (plan 19 J07 and J08): a catalog written without its physical layer, in `semantic/`, compiled against the project's models by every command that loads the project.
/// A reference to a model or a column that is not there is a diagnostic at the file and line that wrote it.
/// </summary>
public class SemanticFilesTests : IDisposable
{
    private readonly List<string> dirs = [];

    public void Dispose() { foreach (var d in dirs.Where(Directory.Exists)) Directory.Delete(d, recursive: true); }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    /// <summary>A copy of the retail template, which carries a semantic model.</summary>
    private string Retail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-semantic-" + Guid.NewGuid().ToString("N"));
        dirs.Add(dir);
        var (exit, _, err) = Run("project", "create", "retail", dir);
        Assert.True(exit == 0, err);
        return dir;
    }

    private static string SemanticFile(string dir) => Path.Combine(dir, "semantic", "sales.yml");

    private static void Edit(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    private static int LineOf(string path, string needle) => Array.FindIndex(File.ReadAllLines(path), l => l.Contains(needle, StringComparison.Ordinal)) + 1;

    [Fact]
    public void A_template_project_with_a_semantic_model_compiles()
    {
        var dir = Retail();
        Assert.True(File.Exists(SemanticFile(dir)));
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.True(exit == 0, output + err);

        var ctx = ProjectContext.Load(dir);
        var model = Assert.Single(ctx.Semantic.Models);
        Assert.Equal("retail_sales", model.Name);
        Assert.NotNull(model.Compilation);
        Assert.Contains("customers.region", model.Compilation.Catalog.Doc.Attributes.Keys);
        Assert.Contains("sales.net_amount", model.Compilation.Catalog.Doc.Measures.Keys);
        Assert.Equal("sqlserver", model.Connections["default"]);
        Assert.Contains(model.Compilation.Tables, t => t.Model == "marts.fct_sales_lines" && t.Physical.PrimaryKey.SequenceEqual(["order_id", "line_no"]));
    }

    [Fact]
    public void A_model_that_is_not_in_the_project_is_a_diagnostic_with_the_file_and_the_line()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "table: marts.dim_customer, column: country_code", "table: marts.dim_custmer, column: country_code");
        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        var line = LineOf(SemanticFile(dir), "marts.dim_custmer");
        Assert.Contains($"error DDB-611  semantic/sales.yml:{line}", err);
        Assert.Contains("`marts.dim_custmer` is not a model of the project", err);
        Assert.DoesNotContain("   at ", err);
        _ = output;
    }

    [Fact]
    public void A_column_that_is_not_declared_is_a_diagnostic_at_the_column()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "column: category_name", "column: category_nam");
        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains($"error DDB-613  semantic/sales.yml:{LineOf(SemanticFile(dir), "category_nam}")}", err);
    }

    [Fact]
    public void A_column_renamed_in_a_model_breaks_the_compile_at_the_semantic_file_that_used_it()
    {
        var dir = Retail();
        Assert.Equal(0, Run("project", "compile", "--project", dir).Exit);
        // the model's declaration says `margin_amount` is now `profit_amount`: its query follows, the semantic file does not
        var yml = Path.Combine(dir, "models", "marts", "fct_sales_lines.yml");
        Edit(yml, "{name: margin_amount,", "{name: profit_amount,");
        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains($"error DDB-613  semantic/sales.yml:{LineOf(SemanticFile(dir), "column: margin_amount")}", err);
        Assert.Contains("marts.fct_sales_lines has no column margin_amount", err);
    }

    [Fact]
    public void A_model_renamed_or_removed_breaks_the_compile_at_the_line_that_names_it()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "from: marts.fct_returns,    to: marts.dim_product", "from: marts.fct_returnz,    to: marts.dim_product");
        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains($"semantic/sales.yml:{LineOf(SemanticFile(dir), "marts.fct_returnz")}", err);
    }

    [Fact]
    public void A_type_that_disagrees_with_the_column_is_DDB_614()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "      year:\n        type: integer", "      year:\n        type: string");
        var (_, _, err) = Run("project", "compile", "--project", dir);
        Assert.Contains("error DDB-614  semantic/sales.yml:", err);
    }

    [Fact]
    public void A_relationship_to_columns_that_are_not_the_grain_is_DDB_615()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "to: marts.dim_store,    from_columns: [store_id],    to_columns: [store_id]", "to: marts.dim_store,    from_columns: [store_id],    to_columns: [region]");
        var (_, _, err) = Run("project", "compile", "--project", dir);
        Assert.Contains("error DDB-615  semantic/sales.yml:", err);
    }

    [Fact]
    public void A_connection_that_is_not_a_source_is_DDB_616_and_a_model_that_is_not_on_it_DDB_612()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "default: {table: marts.dim_store, column: store_name}", "lake: {table: marts.dim_store, column: store_name}");
        Assert.Contains("error DDB-616  semantic/sales.yml:", Run("project", "compile", "--project", dir).Err);

        var other = Retail();
        Edit(Path.Combine(other, "dbdatabuild.yml"), "  sqlserver: { version: 16 }", "  sqlserver: { version: 16 }\n  postgres: {}");
        Edit(SemanticFile(other), "default: {table: marts.dim_store, column: store_name}", "postgres: {table: marts.dim_store, column: store_name}");
        Assert.Contains("error DDB-612  semantic/sales.yml:", Run("project", "compile", "--project", other).Err);
    }

    [Fact]
    public void Everything_else_the_catalog_validation_finds_is_DDB_617_with_its_own_code_in_the_message()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "levels: [date.year, date.year_month]", "levels: [date.year, date.fiscal_month]");
        var (_, _, err) = Run("project", "compile", "--project", dir);
        Assert.Contains("error DDB-617  semantic/sales.yml:", err);
        Assert.Contains("[hierarchy_unknown_level]", err);
    }

    [Fact]
    public void A_file_that_writes_the_physical_layer_a_syntax_error_and_a_duplicate_name_are_DDB_610_with_lines()
    {
        var dir = Retail();
        File.AppendAllText(SemanticFile(dir), "tables:\n  - {name: x, source: x, x_source: default}\n");
        var (_, _, err) = Run("project", "compile", "--project", dir);
        Assert.Contains($"error DDB-610  semantic/sales.yml:{LineOf(SemanticFile(dir), "tables:")}", err);

        var syntax = Retail();
        File.WriteAllText(Path.Combine(syntax, "semantic", "sales.yml"), "name: x\nversion: [unclosed\n");
        var syntaxErr = Run("project", "compile", "--project", syntax).Err;
        Assert.Matches(@"error DDB-610  semantic/sales\.yml:\d+", syntaxErr);

        var twice = Retail();
        File.Copy(SemanticFile(twice), Path.Combine(twice, "semantic", "again.yml"));
        var twiceErr = Run("project", "compile", "--project", twice).Err;
        Assert.Contains("error DDB-610  semantic/sales.yml:", twiceErr);
        Assert.Contains("the name `retail_sales` is the name of `semantic/again.yml` too", twiceErr);
    }

    [Fact]
    public void The_semantic_schema_is_the_one_the_catalog_format_derives_and_the_editor_and_console_know_it()
    {
        var path = Path.Combine(PolyglotBindingTests.RepoRoot(), "schemas", "semantic.schema.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") File.WriteAllText(path, AuthoringSchema.Text);
        Assert.Equal(AuthoringSchema.Text, File.ReadAllText(path).Replace("\r\n", "\n"));
        Assert.Contains(AgentKitCommand.Files(), f => f.Path == "schemas/semantic.schema.json");
    }

    [Fact]
    public void Project_show_semantic_lists_what_every_member_is_bound_to()
    {
        var dir = Retail();
        var (exit, output, err) = Run("project", "show", "semantic", "--project", dir);
        Assert.True(exit == 0, output + err);
        Assert.Contains("retail_sales  (semantic/sales.yml)", output);
        Assert.Contains("sources: default = sqlserver (sqlserver)", output);
        Assert.Matches(@"customers\.region\s+attribute\s+default\s+marts\.dim_customer\.region", output);
        Assert.Matches(@"sales\.net_amount\s+measure\s+default\s+marts\.fct_sales_lines\.net_amount\s+\(sum\)", output);
        Assert.Contains("relationship sales_to_customer: marts.fct_sales_lines(customer_id) -> marts.dim_customer(customer_id)", output);
        Assert.Contains("hierarchy date.by_month: date.year > date.year_month", output);

        var (jsonExit, json, _) = Run("project", "show", "semantic", "--project", dir, "--format", "json");
        Assert.Equal(0, jsonExit);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("data").GetProperty("models")[0];
        Assert.Equal("retail_sales", model.GetProperty("name").GetString());
        var bindings = model.GetProperty("bindings").EnumerateArray().ToList();
        Assert.Contains(bindings, b => b.GetProperty("member").GetString() == "sales.net_amount" && b.GetProperty("connection").GetString() == "sqlserver"
            && b.GetProperty("model").GetString() == "marts.fct_sales_lines" && b.GetProperty("column").GetString() == "net_amount" && b.GetProperty("aggregate").GetString() == "sum");
        var fact = model.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("model").GetString() == "marts.fct_sales_lines");
        Assert.Equal(["order_id", "line_no"], fact.GetProperty("primary_key").EnumerateArray().Select(k => k.GetString()!).ToArray());
        Assert.Equal("marts.fct_sales_lines", fact.GetProperty("physical").GetString());

        Assert.Equal(CliApp.ExitUsage, Run("project", "show", "semantic", "nope", "--project", dir).Exit);
    }

    [Fact]
    public void Project_show_semantic_says_so_when_a_file_does_not_compile()
    {
        var dir = Retail();
        Edit(SemanticFile(dir), "column: category_name", "column: category_nam");
        var (exit, output, err) = Run("project", "show", "semantic", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("do not compile", output);
        Assert.Contains("DDB-613", err);
    }

    [Fact]
    public void A_project_without_semantic_files_is_not_asked_anything()
    {
        var dir = Retail();
        Directory.Delete(Path.Combine(dir, "semantic"), recursive: true);
        Assert.Empty(ProjectContext.Load(dir).Semantic.Models);
        Assert.Equal(0, Run("project", "compile", "--project", dir).Exit);
    }

    [Fact]
    public void A_damaged_semantic_file_is_answered_with_a_diagnostic_never_an_exception()
    {
        var dir = Retail();
        var original = File.ReadAllText(SemanticFile(dir));
        var seen = 0;
        foreach (var damaged in FuzzTests.Mutations(original, 20261027, 60))
        {
            File.WriteAllText(SemanticFile(dir), damaged);
            var diags = new List<Diagnostic>();
            var ctx = ProjectContext.Load(dir);
            diags.AddRange(ctx.Diagnostics);
            Assert.DoesNotContain(diags, d => d.Code == "DDB-900");
            seen++;
        }
        Assert.Equal(60, seen);
    }
}
