using System.Text.Json.Nodes;
using DbDataBuild.Semantic;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 14a SL09 (D33): the scale a model's decimal column declares reaches the catalog the project publishes, so the Gateway answers a measure over it as a decimal of that scale.</summary>
public sealed class ProjectPhysicalLayerScaleTests
{
    [Theory]
    [InlineData("DECIMAL(18, 2)", 2)]
    [InlineData("decimal(38,9)", 9)]
    [InlineData("NUMERIC(10)", 0)]
    [InlineData("DEC( 12 , 4 )", 4)]
    public void A_decimal_type_gives_its_declared_scale(string type, int scale) => Assert.Equal(scale, ProjectPhysicalLayer.ScaleOf(type));

    [Theory]
    [InlineData("DECIMAL")]
    [InlineData("DOUBLE")]
    [InlineData("INTEGER")]
    [InlineData("VARCHAR(20)")]
    [InlineData("DATE")]
    public void A_type_that_declares_no_scale_gives_none_and_the_engines_default_applies(string type) => Assert.Null(ProjectPhysicalLayer.ScaleOf(type));

    [Fact]
    public void The_compiled_catalog_of_a_template_carries_the_scale_of_its_decimal_columns_and_only_those()
    {
        var root = Path.Combine(Path.GetTempPath(), "ddb-scale-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Equal(0, DbDataBuild.Cli.CliApp.Run(["project", "create", "retail", root, "--format", "json"], new StringWriter(), new StringWriter()));
            var loaded = DbDataBuild.Cli.ProjectContext.Load(root);
            var file = loaded.Semantic.Models.Single(m => m.Name == "retail_sales");
            var tables = JsonNode.Parse(file.Compilation!.Json.ToJsonString())!["tables"]!.AsArray();
            var columns = tables.SelectMany(t => t!["columns"]!.AsArray()).Select(c => c!.AsObject()).ToList();
            var amount = columns.First(c => (string)c["name"]! == "net_amount");
            Assert.Equal("Decimal", (string)amount["datatype"]!);
            Assert.Equal(2, (int)amount["scale"]!);
            Assert.All(columns.Where(c => (string)c["datatype"]! != "Decimal"), c => Assert.Null(c["scale"]));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
