using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 22a GP03 (owner decision of 2026-11-08): a project's connection file may carry <c>grants</c> (<c>schema: connection/2</c>), who may use the data source the connection is published as. The loader reads them onto the
/// connection, a <c>connection/1</c> file with them is refused naming the file and line, the sections of format 2 that Build does not use stay refused, and the published JSON Schema agrees.
/// </summary>
public class ConnectionGrantsFileTests
{
    private static string Project(string name, string text)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [warehouse]}\n");
        Directory.CreateDirectory(Path.Combine(dir, "connections"));
        File.WriteAllText(Path.Combine(dir, "connections", name), text);
        return dir;
    }

    private static (ProjectConfig Config, List<Diagnostic> Diags) Load(string dir)
    {
        var diags = new List<Diagnostic>();
        return (ProjectConfigLoader.LoadFromProject(dir, diags), diags);
    }

    private const string Head = "name: warehouse\nengine: sqlserver\n";

    [Fact]
    public void The_grants_of_a_connection_file_are_on_the_connection_and_a_connection_without_them_has_none()
    {
        var dir = Project("warehouse.yaml", "schema: connection/2\n" + Head + "grants:\n  groups: [Finance, auditors]\n");
        try
        {
            var (config, diags) = Load(dir);
            Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
            Assert.Equal(["Finance", "auditors"], config.Connections["warehouse"].Grants!.Groups);
            Assert.False(config.Connections["warehouse"].Grants!.Everyone);
            Assert.Null(config.Connections["sqlserver"].Grants);                    // implicit, no file
        }
        finally { Directory.Delete(dir, true); }

        var open = Project("warehouse.yaml", "schema: connection/2\n" + Head + "grants: { everyone: true }\n");
        try { Assert.True(Load(open).Config.Connections["warehouse"].Grants!.Everyone); }
        finally { Directory.Delete(open, true); }

        var bare = Project("warehouse.yaml", "schema: connection/1\n" + Head);
        try { Assert.Null(Load(bare).Config.Connections["warehouse"].Grants); }
        finally { Directory.Delete(bare, true); }
    }

    [Fact]
    public void A_format_1_file_with_grants_is_refused_naming_the_file_and_line_and_a_bad_list_is_refused_too()
    {
        var one = Project("warehouse.yaml", "schema: connection/1\n" + Head + "grants:\n  everyone: true\n");
        try
        {
            var error = Assert.Single(Load(one).Diags, d => d.Descriptor == DiagnosticCatalog.ConnectionFileInvalid);
            Assert.Equal(("connections/warehouse.yaml", 4), (error.Location.File, error.Location.Line));
            Assert.Contains("`grants` is a section of format 2", error.Found);
            Assert.Contains("schema: connection/2", error.Fix);
        }
        finally { Directory.Delete(one, true); }

        var both = Project("warehouse.yaml", "schema: connection/2\n" + Head + "grants:\n  everyone: true\n  groups: [a]\n");
        try { Assert.Contains("also names groups", Assert.Single(Load(both).Diags, d => d.Descriptor == DiagnosticCatalog.ConnectionFileInvalid).Found); }
        finally { Directory.Delete(both, true); }

        // the other sections of format 2 are still not for a Build connection file (it has no address to cache by, and no use for one)
        var cache = Project("warehouse.yaml", "schema: connection/2\n" + Head + "cache: { enabled: true }\n");
        try { Assert.Contains(Load(cache).Diags, d => d.Descriptor == DiagnosticCatalog.ConnectionFileInvalid); }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public void The_published_schema_takes_grants_in_a_connection_2_file_and_refuses_unknown_keys_in_them()
    {
        var schema = SchemaConformanceTests.LoadSchema("connection");
        Assert.True(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/2\n" + Head + "grants:\n  groups: [finance, auditors]\n"));
        Assert.True(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/2\n" + Head + "grants: { everyone: true }\n"));
        Assert.True(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/1\n" + Head));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/3\n" + Head));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/2\n" + Head + "grants:\n  group: [finance]\n"));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, "schema: connection/2\n" + Head + "grants:\n  groups: [finance, finance]\n"));
    }
}
