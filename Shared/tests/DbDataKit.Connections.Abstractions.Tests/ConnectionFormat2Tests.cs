using DbDataKit.Config.Yaml;
using DbDataKit.Quality;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 24a UC03: format 2 of the serving data source (<c>schema: connection/2</c>) adds the sections <c>cache</c> and <c>checks</c>; <c>connection/1</c> is read as before. The generated JSON Schema agrees with the loader.
/// </summary>
public sealed class ConnectionFormat2Tests
{
    private static string File(int version, string extra = "", string address = "address: { host: db1, database: dw }") => $"""
        schema: connection/{version}
        name: warehouse
        engine: sqlserver
        {address}
        {extra}
        """;

    [Fact]
    public void Format_1_is_read_as_before_and_format_2_is_the_newest()
    {
        Assert.Equal(2, ConnectionDefinition.CurrentVersion);
        var one = ConnectionDefinitionLoader.Load(File(1));
        Assert.True(one.Ok, string.Join("\n", one.Diagnostics));
        Assert.Equal(1, one.Definition!.SchemaVersion);
        Assert.Null(one.Definition.Cache);
        var two = ConnectionDefinitionLoader.Load(File(2));
        Assert.True(two.Ok, string.Join("\n", two.Diagnostics));
        Assert.Equal(2, two.Definition!.SchemaVersion);
        Assert.Equal(1, ConnectionDefinitionLoader.FormatOfText(File(1)));
        Assert.Equal(2, ConnectionDefinitionLoader.FormatOfText(File(2)));
        Assert.Equal(1, ConnectionDefinitionLoader.FormatOfText("name: x\n"));
    }

    [Fact]
    public void Each_key_of_the_cache_section_loads_and_the_checks_have_their_place()
    {
        var result = ConnectionDefinitionLoader.Load(File(2, "cache: { enabled: true, ttl: 2h, scope: shared, dataset: dbo.orders }\nchecks:\n  - { id: orders_fresh, kind: freshness, dataset: dbo.orders, max_age: 6h }\n  - { id: fk, kind: referential, dataset: dbo.orders, column: customer_id, references: { dataset: dbo.customers, column: id } }\n  - { id: st, kind: accepted_values, dataset: dbo.orders, column: status, values: [open, shipped] }"));
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        var cache = result.Definition!.Cache!;
        Assert.Equal((true, "2h", "shared", "dbo.orders"), (cache.Enabled, cache.Ttl, cache.Scope, cache.Dataset));
        Assert.Equal(3, result.Definition.Checks!.Count);
        Assert.Equal(["orders_fresh", "fk", "st"], result.Definition.CheckDefinitions!.Select(c => c.Id));
        Assert.Equal(TimeSpan.FromHours(6), ((FreshnessParameters)result.Definition.CheckDefinitions![0].Parameters).MaxAge);
        Assert.Equal(("5m", "caller", false), (new CacheSpec().Ttl, new CacheSpec().Scope, new CacheSpec().Enabled));
    }

    [Fact]
    public void A_format_1_file_with_a_section_of_format_2_is_refused_naming_the_file_and_the_line()
    {
        var result = ConnectionDefinitionLoader.Load(File(1, "cache: { enabled: true }\nchecks: []"), "warehouse.yaml");
        var errors = result.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
        Assert.False(result.Ok);
        Assert.Equal([ConnectionDefinitionLoader.SectionNotInThisFormat, ConnectionDefinitionLoader.SectionNotInThisFormat], errors.Select(e => e.Code));
        Assert.Equal([("warehouse.yaml", 5), ("warehouse.yaml", 6)], errors.Select(e => (e.Path, e.Line)));
        Assert.Contains("schema: connection/2", errors[0].Fix);
    }

    [Theory]
    [InlineData("checks:\n  - { id: a, kind: sql, dataset: t }", 6, "is not a kind of check")]
    [InlineData("checks:\n  - { id: a, kind: range, dataset: t, column: c, min: 5, max: 1 }", 6, "is above `max`")]
    [InlineData("checks:\n  - { id: a, kind: referential, dataset: t, column: c }", 6, "names no target")]
    [InlineData("checks:\n  - { id: a, kind: not_null, column: c }", 6, "names no dataset")]
    [InlineData("checks:\n  - { id: a, kind: not_null, dataset: t, column: c, severity: high }", 6, "`severity` is `high`")]
    public void Each_bad_check_is_refused_with_file_and_line(string extra, int line, string says)
    {
        var result = ConnectionDefinitionLoader.Load(File(2, extra), "warehouse.yaml");
        Assert.False(result.Ok);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(("warehouse.yaml", line), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
    }

    [Theory]
    [InlineData("cache: { ttl: 25h }", "at most 24 hours")]
    [InlineData("cache: { scope: everyone }", "scope")]
    [InlineData("cache: { ttl: soon }", "ttl")]
    public void Each_bad_value_is_refused_with_file_and_line(string extra, string says)
    {
        var result = ConnectionDefinitionLoader.Load(File(2, extra), "warehouse.yaml");
        Assert.False(result.Ok);
        var error = result.Diagnostics.First(d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(("warehouse.yaml", 5), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
    }

    [Fact]
    public void A_data_source_with_no_lineage_identity_cannot_have_the_cache_turned_on()
    {
        // no server, no database, no connection string: nothing to key the expiry on a data change by
        var secretOnly = "connection_string_secret: \"env:DW\"";
        var refused = ConnectionDefinitionLoader.Load(File(2, "cache: { enabled: true }", address: secretOnly), "warehouse.yaml");
        var error = Assert.Single(refused.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(ConnectionDefinitionLoader.CannotBeCached, error.Code);
        Assert.Contains("lineage identity", error.Message);
        Assert.Equal(("warehouse.yaml", 5), (error.Path, error.Line));

        Assert.True(ConnectionDefinitionLoader.Load(File(2, "cache: { enabled: false }", address: secretOnly)).Ok);
        Assert.True(ConnectionDefinitionLoader.Load(File(2, "cache: { enabled: true }", address: "connection_string: \"Server=db1;Database=dw\"")).Ok);
        Assert.True(ConnectionDefinitionLoader.Load(File(2, "cache: { enabled: true }", address: "address: { database: dw.duckdb }")).Ok);
    }

    [Fact]
    public void A_file_of_a_newer_format_is_refused_by_its_version()
    {
        var result = ConnectionDefinitionLoader.Load(File(3), "warehouse.yaml");
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(SharedCodes.SchemaVersionNewer, error.Code);
        Assert.Equal(("warehouse.yaml", 1), (error.Path, error.Line));
        Assert.Contains("up to version 2", error.Message);
    }

    [Fact]
    public void The_json_schema_agrees_with_the_loader()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(ConnectionDefinitionLoader.JsonSchema())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        Assert.True(properties.ContainsKey("cache"));
        Assert.True(properties.ContainsKey("checks"));
        var text = ConnectionDefinitionLoader.JsonSchema();
        foreach (var key in CacheSection.Keys) Assert.Contains($"\"{key}\"", text, StringComparison.Ordinal);
        Assert.Contains("caller", text, StringComparison.Ordinal);
    }
}
