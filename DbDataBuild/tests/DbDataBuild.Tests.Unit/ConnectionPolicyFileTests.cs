using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 13c D19 (owner decision of 2026-11-09): a project's connection file may carry the Gateway's serving policy under <c>extensions.gateway</c>, which is published with the definition and replaces the Gateway's whole.
/// <c>allow_writes</c> and <c>allowed_directories</c> are not part of any definition: a file that has either is refused naming the file, the line and the field, and so is a key the Gateway does not have or one that belongs to
/// <c>grants</c>. The published JSON Schema agrees.
/// </summary>
public class ConnectionPolicyFileTests
{
    private static string Project(string text)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [warehouse]}\n");
        Directory.CreateDirectory(Path.Combine(dir, "connections"));
        File.WriteAllText(Path.Combine(dir, "connections", "warehouse.yaml"), text);
        return dir;
    }

    private static (ProjectConfig Config, List<Diagnostic> Diags) Load(string dir)
    {
        var diags = new List<Diagnostic>();
        return (ProjectConfigLoader.LoadFromProject(dir, diags), diags);
    }

    private const string Head = "schema: connection/1\nname: warehouse\nengine: sqlserver\n";

    [Fact]
    public void The_serving_policy_of_a_connection_file_is_on_the_connection_and_a_connection_without_it_has_none()
    {
        var dir = Project(Head + "extensions:\n  gateway:\n    batch_size: 1000\n    classifier: strict\n    pass_through: false\n    compression:\n      enabled: true\n");
        try
        {
            var (config, diags) = Load(dir);
            Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
            var policy = config.Connections["warehouse"].GatewayPolicy!;
            Assert.Equal(["batch_size", "classifier", "pass_through", "compression"], policy.Entries.Select(e => e.Key.Value));
            Assert.Null(config.Connections["sqlserver"].GatewayPolicy);       // implicit, no file
        }
        finally { Directory.Delete(dir, true); }

        var bare = Project(Head + "extensions:\n  build:\n    version: 16\n");
        try { Assert.Null(Load(bare).Config.Connections["warehouse"].GatewayPolicy); }
        finally { Directory.Delete(bare, true); }
    }

    [Fact]
    public void A_definition_with_allow_writes_or_allowed_directories_is_refused_naming_the_file_the_line_and_the_field()
    {
        var dir = Project(Head + "extensions:\n  gateway:\n    batch_size: 10\n    allow_writes: true\n    allowed_directories: ['/data']\n");
        try
        {
            var errors = Load(dir).Diags.Where(d => d.Descriptor == DiagnosticCatalog.ConnectionFileInvalid).ToList();
            Assert.Equal([("connections/warehouse.yaml", 7), ("connections/warehouse.yaml", 8)], errors.Select(e => (e.Location.File, e.Location.Line)));
            Assert.Contains("`extensions.gateway.allow_writes`", errors[0].Found);
            Assert.Contains("Build does not author or publish it", errors[0].Found);
            Assert.Contains("`extensions.gateway.allowed_directories`", errors[1].Found);
            Assert.Contains("A publish replaces the whole data source file on the Gateway", errors[0].Fix);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Groups_an_unknown_key_and_a_value_of_the_wrong_kind_are_refused_with_their_line()
    {
        var dir = Project(Head + "extensions:\n  gateway:\n    groups: [a]\n    colour: red\n    batch_size: many\n");
        try
        {
            var errors = Load(dir).Diags.Where(d => d.Descriptor == DiagnosticCatalog.ConnectionFileInvalid).ToList();
            Assert.Equal([6, 7, 8], errors.Select(e => e.Location.Line));
            Assert.Contains("`grants`", errors[0].Found);
            Assert.Contains("not a serving policy field", errors[1].Found);
            Assert.Contains("whole number", errors[2].Found);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_published_schema_takes_the_policy_fields_and_refuses_the_two_that_only_an_administrator_sets()
    {
        var schema = SchemaConformanceTests.LoadSchema("connection");
        Assert.True(SchemaConformanceTests.SchemaAccepts(schema, Head + "extensions:\n  gateway:\n    batch_size: 1000\n    classifier: strict\n    pass_through: true\n    compression: { enabled: true }\n"));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, Head + "extensions:\n  gateway:\n    allow_writes: true\n"));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, Head + "extensions:\n  gateway:\n    allowed_directories: [/data]\n"));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, Head + "extensions:\n  gateway:\n    groups: [a]\n"));
        Assert.False(SchemaConformanceTests.SchemaAccepts(schema, Head + "extensions:\n  gateway:\n    batch_size: many\n"));
    }
}
