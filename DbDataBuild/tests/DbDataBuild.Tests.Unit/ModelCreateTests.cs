using System.Text.Json.Nodes;
using DbDataBuild.Cli;

namespace DbDataBuild.Tests.Unit;

public class ModelCreateTests
{
    private static string Starter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-create-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        return dir;
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(args, o, e, environment: _ => null);
        return (exit, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("view")]
    [InlineData("full")]
    [InlineData("incremental_by_unique_key")]
    [InlineData("incremental_by_time_range")]
    public void A_created_model_of_each_kind_is_a_model_the_project_compiles_and_the_definition_matches_its_query(string kind)
    {
        var dir = Starter();
        var r = Run("project", "model", "create", "marts.scratch", "--kind", kind, "--project", dir);
        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(Path.Combine(dir, "models/marts/scratch.yml")));
        Assert.True(File.Exists(Path.Combine(dir, "models/marts/scratch.sql")));
        Assert.Contains("Next:", r.Out);
        var compile = Run("project", "compile", "--project", dir);
        Assert.True(compile.Exit == 0, compile.Out + compile.Err);
        Assert.Equal(0, Run("project", "model", "update", "--check", "--project", dir).Exit);       // the definition says what the query returns
        Assert.True(File.Exists(Path.Combine(dir, "rendered/sqlserver/marts.scratch/manifest.yml")) || kind == "view", "a table model renders its loads");
    }

    [Fact]
    public void It_refuses_a_name_that_is_taken_a_name_that_is_not_one_and_a_kind_it_cannot_scaffold_and_writes_nothing()
    {
        var dir = Starter();
        var taken = Run("project", "model", "create", "marts.orders", "--project", dir);
        Assert.Equal(1, taken.Exit);
        Assert.Contains("already exists", taken.Err);
        Assert.Equal(2, Run("project", "model", "create", "orders", "--project", dir).Exit);
        Assert.Equal(2, Run("project", "model", "create", "marts.a-b", "--project", dir).Exit);
        Assert.NotEqual(0, Run("project", "model", "create", "marts.x", "--kind", "copy", "--project", dir).Exit);
        Assert.False(File.Exists(Path.Combine(dir, "models/marts/x.yml")));
        var unknown = Run("project", "model", "create", "marts.x", "--connection", "nowhere", "--project", dir);
        Assert.Equal(2, unknown.Exit);
    }

    [Fact]
    public void A_connection_is_written_into_the_definition_and_the_dotted_layout_is_followed()
    {
        var dir = Starter();
        File.AppendAllText(Path.Combine(dir, "dbdatabuild.yml"), "\nmodel_layout: dotted\n");
        // the starter's files are in folders: the layout only matters for the new one
        var r = Run("project", "model", "create", "marts.fresh", "--connection", "sqlserver", "--project", dir, "--format", "json");
        Assert.Equal(0, r.Exit);
        var files = JsonNode.Parse(r.Out)!["data"]!["files"]!.AsArray().Select(f => (string)f!).ToList();
        Assert.Equal(["models/marts.fresh.yml", "models/marts.fresh.sql"], files);
        Assert.Contains("connections: [sqlserver]", File.ReadAllText(Path.Combine(dir, "models/marts.fresh.yml")));
    }
}
