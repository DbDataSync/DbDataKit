using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 22a GP03 (owner decision of 2026-11-08): who may read a data source is part of the definition that is published. Format 2 of the serving data source (<c>schema: connection/2</c>) has the section <c>grants</c> (home
/// <c>groups</c>, or <c>everyone</c>); a <c>connection/1</c> file that has it is refused naming the file and the line.
/// </summary>
public sealed class ConnectionGrantsTests
{
    private static string File(int version, string extra = "") => $"schema: connection/{version}\nname: warehouse\nengine: sqlserver\nconnection_string: Server=db1;Database=dw\n{extra}";

    [Fact]
    public void The_groups_and_the_everyone_switch_load_and_a_file_without_grants_has_none()
    {
        var groups = ConnectionDefinitionLoader.LoadDatabaseConnection(File(2, "grants:\n  groups: [Finance, auditors]"));
        Assert.True(groups.Ok, string.Join("\n", groups.Diagnostics));
        Assert.Equal(["Finance", "auditors"], groups.Definition!.Grants!.Groups);
        Assert.False(groups.Definition.Grants.Everyone);

        var everyone = ConnectionDefinitionLoader.LoadDatabaseConnection(File(2, "grants: { everyone: true }"));
        Assert.True(everyone.Ok, string.Join("\n", everyone.Diagnostics));
        Assert.True(everyone.Definition!.Grants!.Everyone);
        Assert.Empty(everyone.Definition.Grants.Groups);

        Assert.Null(ConnectionDefinitionLoader.LoadDatabaseConnection(File(2)).Definition!.Grants);
        Assert.Null(ConnectionDefinitionLoader.LoadDatabaseConnection(File(1)).Definition!.Grants);
        Assert.True(ConnectionDefinitionLoader.LoadDatabaseConnection(File(2, "grants: {}")).Ok);          // deliberately nothing: used by no home user
    }

    [Fact]
    public void A_format_1_file_with_grants_is_refused_naming_the_file_the_line_and_the_fix()
    {
        var result = ConnectionDefinitionLoader.LoadDatabaseConnection(File(1, "grants: { everyone: true }"), "connections/warehouse.yaml");
        var error = Assert.Single(result.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.False(result.Ok);
        Assert.Equal(ConnectionDefinitionLoader.SectionNotInThisFormat, error.Code);
        Assert.Equal(("connections/warehouse.yaml", 5), (error.Path, error.Line));
        Assert.Contains("`grants` is a section of format 2", error.Message);
        Assert.Contains("schema: connection/2", error.Fix);
    }

    [Theory]
    [InlineData("grants:\n  groups: [a, a]", 6, "listed twice")]
    [InlineData("grants:\n  groups: [a, A]", 6, "listed twice")]
    [InlineData("grants:\n  groups: ['']", 6, "is empty")]
    [InlineData("grants:\n  groups: [' a']", 6, "leading or trailing spaces")]
    [InlineData("grants:\n  everyone: true\n  groups: [a]", 6, "also names groups")]
    public void Each_bad_grant_is_refused_with_file_and_line(string extra, int line, string says)
    {
        var result = ConnectionDefinitionLoader.LoadDatabaseConnection(File(2, extra), "warehouse.yaml");
        Assert.False(result.Ok);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(ConnectionDefinitionLoader.BadGrants, error.Code);
        Assert.Equal(("warehouse.yaml", line), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
    }

    [Theory]
    [InlineData("grants:\n  group: [a]", "Unknown key `group`")]
    [InlineData("grants:\n  groups: a", "must be a list")]
    [InlineData("grants:\n  everyone: maybe", "everyone")]
    public void Unknown_keys_and_wrong_types_are_refused_by_the_strict_reader(string extra, string says)
    {
        var result = ConnectionDefinitionLoader.LoadDatabaseConnection(File(2, extra), "warehouse.yaml");
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Severity == ConfigSeverity.Error && d.Message.Contains(says, StringComparison.Ordinal) && d.Path == "warehouse.yaml");
    }

    [Fact]
    public void The_json_schema_lists_the_section()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(ConnectionDefinitionLoader.JsonSchema())!.AsObject();
        Assert.True(schema["properties"]!.AsObject().ContainsKey("grants"));
        var text = ConnectionDefinitionLoader.JsonSchema();
        Assert.Contains("\"groups\"", text, StringComparison.Ordinal);
        Assert.Contains("\"everyone\"", text, StringComparison.Ordinal);
    }
}
