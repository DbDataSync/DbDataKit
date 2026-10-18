using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

public class ProjectValidatorTests
{
    [Fact]
    public void Valid_project_passes_and_validation_writes_nothing()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.yml"), ValidModel);
        File.WriteAllText(Path.Combine(dir, "models/marts/fct_orders.sql"), "SELECT 1 AS order_id");
        var before = Snapshot(dir);

        var result = ProjectValidator.Validate(dir);

        Assert.False(result.HasErrors);
        Assert.Equal("marts.fct_orders", Assert.Single(result.Models).Name);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Orphans_of_either_kind_are_reported_and_point_to_define()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/only_sql.sql"), "SELECT 1");
        File.WriteAllText(Path.Combine(dir, "models/marts/only_yml.yml"), ValidModel.Replace("fct_orders", "only_yml"));

        var result = ProjectValidator.Validate(dir);

        var orphans = result.Diagnostics.Where(d => d.Code == "DDB-108").ToList();
        Assert.Equal(2, orphans.Count);
        Assert.Contains(orphans, d => d.Location.File == "models/marts/only_sql.sql" && d.Fix!.Contains("dbdatabuild project model update"));
        Assert.Contains(orphans, d => d.Location.File == "models/marts/only_yml.yml");
    }

    [Fact]
    public void Name_must_match_path_convention()
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "models/marts/dim_x.yml"), ValidModel); // declares marts.fct_orders
        File.WriteAllText(Path.Combine(dir, "models/marts/dim_x.sql"), "SELECT 1");
        var result = ProjectValidator.Validate(dir);
        Assert.Contains(result.Diagnostics, d => d.Code == "DDB-107" && d.Found.Contains("marts.dim_x"));
    }

    [Fact]
    public void Missing_models_directory_is_a_diagnostic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.True(ProjectValidator.Validate(dir).HasErrors);
    }

    [Fact]
    public void Output_order_is_deterministic()
    {
        var dir = NewProjectDir();
        foreach (var n in new[] { "b", "a", "c" })
            File.WriteAllText(Path.Combine(dir, $"models/marts/{n}.sql"), "SELECT 1");
        var files = ProjectValidator.Validate(dir).Diagnostics.Select(d => d.Location.File).ToList();
        Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal), files);
    }
}
