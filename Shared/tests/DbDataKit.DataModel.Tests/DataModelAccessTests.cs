using DbDataKit.Access;
using DbDataKit.Config.Yaml;

namespace DbDataKit.DataModel.Tests;

/// <summary>
/// Plan 22a GP01: format 2 of DbDataStore's <c>model</c> gains the <c>access</c> section (QG2: rules are data). It is checked against the model's entities, their attributes and its connection; a rules table is <c>schema.table</c> on the
/// model's connection, never one of the model's own tables; a format 1 file with the section is refused naming the file and the line.
/// </summary>
public sealed class DataModelAccessTests
{
    private static string Model(string access, int version = 2, string cache = "") => $$"""
        schemaVersion: {{version}}
        name: sales
        storage: {connection: mdm_sql}
        {{cache}}
        {{access}}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
              region: {type: VARCHAR(20)}
              quota: {type: "DECIMAL(10,2)"}
              note: {type: VARCHAR(200)}
        """;

    private const string Good = """
        access:
          row:
            - dataset: territory
              groups:
                - id: by_region
                  rules:
                    - { id: r1, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
          columns:
            - { subject: interns, dataset: territory, column: note, treatment: mask }
            - { subject: interns, dataset: territory, column: quota, treatment: hide }
        """;

    private static DataModelLoadResult Load(string yaml) => DataModelLoader.Load(yaml, "sales.model.yaml");

    [Fact]
    public void A_model_with_an_access_section_loads_in_format_2()
    {
        var result = Load(Model(Good));
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        var access = result.Model!.Access!;
        Assert.Equal("rules.region_access", access.Row.Single().Groups.Single().Rules.Single().Table);
        Assert.Equal([ColumnTreatment.Mask, ColumnTreatment.Hide], access.Columns.Select(c => c.Treatment));
        Assert.Equal(2, result.Model.SchemaVersion);
        Assert.Null(Load(Model("")).Model!.Access);
    }

    [Fact]
    public void A_format_1_file_with_the_section_is_refused_naming_the_file_and_the_line()
    {
        var result = Load(Model("access: {max_age: 1h}", version: 1));
        Assert.False(result.Ok);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal((AccessSectionReader.NotInThisFormat, "sales.model.yaml", 5), (d.Code, d.Path, d.Line));
        Assert.Contains("schemaVersion: 2", d.Fix);
    }

    [Theory]
    [InlineData("access:\n  row:\n    - dataset: territori\n      groups:\n        - { id: g, rules: [ { id: r, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } } ] }", "Did you mean `territory`")]
    [InlineData("access:\n  row:\n    - dataset: territory\n      groups:\n        - { id: g, rules: [ { id: r, effect: include, table: rules.region_access, subject: who, columns: { regon: region_code } } ] }", "Did you mean `region`")]
    [InlineData("access:\n  row:\n    - dataset: territory\n      groups:\n        - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } } ] }", "not a rules table")]
    [InlineData("access:\n  row:\n    - dataset: territory\n      groups:\n        - { id: g, rules: [ { id: r, effect: include, table: sales.territory, subject: who, columns: { region: region_code } } ] }", "a Store table is never read directly")]
    [InlineData("access:\n  columns:\n    - { subject: s, dataset: territory, column: quota, treatment: mask }", "only text")]
    [InlineData("access:\n  columns:\n    - { subject: s, dataset: territory, column: nothing, treatment: hide }", "not a column of `territory`")]
    public void Each_bad_part_is_refused_with_file_and_line(string access, string says)
    {
        var result = Load(Model(access));
        Assert.False(result.Ok);
        var found = result.Diagnostics.Where(d => d.Message.Contains(says, StringComparison.Ordinal)).ToList();
        Assert.True(found.Count > 0, string.Join("\n", result.Diagnostics));
        Assert.All(found, d => Assert.True(d.Path == "sales.model.yaml" && d.Line >= 5));
    }

    [Fact]
    public void A_shared_cache_beside_a_rule_is_refused()
    {
        var result = Load(Model(Good, cache: "cache: {enabled: true, scope: shared}"));
        Assert.Contains(result.Diagnostics, d => d.Code == AccessSectionReader.SharedCache);
        Assert.True(Load(Model(Good, cache: "cache: {enabled: true, scope: rules}")).Ok);
    }

    [Fact]
    public void A_misspelled_root_key_still_suggests_the_section()
    {
        var result = Load(Model("acess: {max_age: 1h}"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Did you mean `access`", StringComparison.Ordinal));
    }
}
