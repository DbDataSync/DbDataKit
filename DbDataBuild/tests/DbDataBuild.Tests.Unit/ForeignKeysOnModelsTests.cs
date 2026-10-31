using DbDataBuild.Cli;
using DbDataBuild.Models;
using DbDataBuild.Semantic;
using Xunit;

namespace DbDataBuild.Tests.Unit;

/// <summary>`foreign_keys:` on built models (plan 19 J29): validated against the other models, read by a semantic file as relationships where it says nothing of its own, and tested by `connection check`.</summary>
public class ForeignKeysOnModelsTests : IDisposable
{
    private readonly List<string> dirs = [];

    public void Dispose() { foreach (var d in dirs.Where(Directory.Exists)) Directory.Delete(d, recursive: true); }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    private string Retail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-fk-" + Guid.NewGuid().ToString("N"));
        dirs.Add(dir);
        var (exit, _, err) = Run("project", "create", "retail", dir);
        Assert.True(exit == 0, err);
        return dir;
    }

    private static string Sales(string dir) => Path.Combine(dir, "models", "marts", "fct_sales_lines.yml");
    private static string Semantic(string dir) => Path.Combine(dir, "semantic", "sales.yml");

    private static void Replace(string path, string from, string to)
    {
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    private static void DeclareKey(string dir, string name = "sales_to_customer", string table = "marts.dim_customer", string columns = "[customer_id]") =>
        File.AppendAllText(Sales(dir), $"foreign_keys:\n  - {{name: {name}, columns: [customer_id], references: {{table: {table}, columns: {columns}}}}}\n");

    /// <summary>The retail semantic file without its own sales_to_customer relationship, so the model's key is what says the join.</summary>
    private static void DropAuthoredRelationship(string dir) =>
        Replace(Semantic(dir), "  - {name: sales_to_customer, from: marts.fct_sales_lines, to: marts.dim_customer, from_columns: [customer_id], to_columns: [customer_id]}\n", "");

    [Fact]
    public void A_key_on_a_built_model_is_read_as_a_relationship_when_the_semantic_file_has_none_between_them()
    {
        var dir = Retail();
        DropAuthoredRelationship(dir);
        DeclareKey(dir);

        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.True(exit == 0, output + err);

        var model = Assert.Single(ProjectContext.Load(dir).Semantic.Models);
        var derived = Assert.Single(model.Compilation!.Relationships, r => r.Name == "sales_to_customer");
        Assert.Equal(("marts.fct_sales_lines", "marts.dim_customer"), (derived.FromModel, derived.ToModel));
        Assert.NotNull(derived.DeclaredAt);
    }

    [Fact]
    public void A_semantic_file_that_writes_the_join_itself_is_read_as_written_and_the_key_adds_nothing()
    {
        var dir = Retail();
        DeclareKey(dir, name: "fk_other_name");

        var (exit, output, err) = Run("project", "compile", "--project", dir);
        Assert.True(exit == 0, output + err);

        var model = Assert.Single(ProjectContext.Load(dir).Semantic.Models);
        Assert.DoesNotContain(model.Compilation!.Relationships, r => r.DeclaredAt is not null);
        Assert.Single(model.Compilation.Relationships, r => r.FromModel == "marts.fct_sales_lines" && r.ToModel == "marts.dim_customer");
    }

    [Fact]
    public void A_key_named_like_another_relationship_of_the_semantic_file_is_a_problem_at_the_key()
    {
        var dir = Retail();
        DropAuthoredRelationship(dir);
        DeclareKey(dir, name: "sales_to_product");       // the semantic file's join to dim_product has this name

        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("models/marts/fct_sales_lines.yml", err);
        Assert.Contains("relationship_name_taken", err);
    }

    [Theory]
    [InlineData("marts.nowhere", "[customer_id]", "which is not a model of the project")]
    [InlineData("marts.dim_customer", "[customer_key]", "which `marts.dim_customer` does not declare")]
    [InlineData("marts.dim_customer", "[region]", "but the `grain` of `marts.dim_customer` is (customer_id)")]
    public void A_key_that_does_not_match_its_target_is_DDB_618_at_its_line(string table, string columns, string expected)
    {
        var dir = Retail();
        DeclareKey(dir, table: table, columns: columns);

        var (exit, _, err) = Run("project", "compile", "--project", dir);
        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("error DDB-618  models/marts/fct_sales_lines.yml:", err);
        Assert.Contains(expected, err);
    }

    [Fact]
    public void A_key_is_tested_by_two_generated_checks_per_connection_and_the_checks_are_listed()
    {
        var dir = Retail();
        DeclareKey(dir);

        var checks = ForeignKeyChecks.Generate(ProjectModels.Of(ProjectContext.Load(dir).Project, ["sqlserver"]), ProjectContext.Load(dir).Config);
        var orphans = Assert.Single(checks, c => c.Kind == "foreign_key_orphans");
        Assert.Equal("model.marts.fct_sales_lines.foreign_key.sales_to_customer.orphans@sqlserver", orphans.Name);
        Assert.Contains("LEFT JOIN [marts].[dim_customer]", orphans.Sql);
        Assert.Contains("GROUP BY [customer_id] HAVING COUNT(*) > 1", Assert.Single(checks, c => c.Kind == "foreign_key_grain").Sql);

        var (exit, output, err) = Run("project", "tests", "list", "--kind", "semantic", "--project", dir);
        Assert.True(exit == 0, output + err);
        Assert.Contains("model.marts.fct_sales_lines.foreign_key.sales_to_customer.grain@sqlserver", output);
    }
}
