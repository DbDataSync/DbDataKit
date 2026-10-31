using System.Text.Json.Nodes;
using DbDataBuild.Cli;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// A mapped model written by `project import` says which connection it exists on, and that is a read origin (DuckDB, say), which is not an engine tables are built on. The JSON document of `project compile` and `project show
/// metadata`, which the console runs, looked an engine up for every connection of a mapped model and failed with an unknown target (DDB-900) on the first import; found by the console's import story (plan 13a, W07).
/// </summary>
public sealed class MappedModelOnReadOriginTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-origin-model-" + Guid.NewGuid().ToString("N")[..8]);

    public MappedModelOnReadOriginTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", root, "--format", "json"], new StringWriter(), new StringWriter()));
        File.WriteAllText(Path.Combine(root, "connections", "origin.yaml"), "schema: connection/1\nname: origin\nengine: duckdb\naccess: readonly\n");
        Directory.CreateDirectory(Path.Combine(root, "models", "hr"));
        File.WriteAllText(Path.Combine(root, "models", "hr", "department.yml"), "name: hr.department\nkind: mapped\nconnections=: [origin]\ncolumns:\n  - {name: department_id, type: INTEGER, nullable: false}\n  - {name: name, type: VARCHAR, nullable: false}\n");
    }

    public void Dispose() => Directory.Delete(root, true);

    private JsonNode Run(params string[] args)
    {
        var output = new StringWriter();
        CliApp.Run([.. args, "--project", root, "--format", "json"], output, new StringWriter());
        return JsonNode.Parse(output.ToString())!;
    }

    [Fact]
    public void Compile_reports_a_mapped_model_on_a_read_origin_instead_of_failing()
    {
        var document = Run("project", "compile", "--check");
        Assert.DoesNotContain(document["diagnostics"]!.AsArray(), d => (string)d!["code"]! == "DDB-900");
        Assert.NotEqual(70, (int)document["exit_code"]!);
    }

    [Fact]
    public void Metadata_lists_the_model_on_its_origin_with_no_native_types_for_it()
    {
        var model = Run("project", "show", "metadata")["data"]!["models"]!.AsArray().Single(m => (string)m!["name"]! == "hr.department")!;
        Assert.Equal(["origin"], model["connections"]!.AsArray().Select(c => (string)c!).ToArray());
        Assert.All(model["columns"]!.AsArray(), c => Assert.Empty(c!["native"]!.AsObject()));
    }
}
