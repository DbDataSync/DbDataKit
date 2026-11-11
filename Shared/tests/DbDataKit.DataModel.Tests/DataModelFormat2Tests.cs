using DbDataKit.Config.Yaml;
using DbDataKit.Quality;

namespace DbDataKit.DataModel.Tests;

/// <summary>
/// Plan 24a UC03: format 2 of DbDataStore's <c>model</c> (<c>schemaVersion: 2</c>) adds the sections <c>cache</c> and <c>checks</c>. Each key is valid, each bad value is refused with file and line, a format-1 file
/// that has a section is refused, a dataset that cannot be cached refuses the cache, and a program that reads only format 1 refuses a format-2 file by its version (the clean break).
/// </summary>
public sealed class DataModelFormat2Tests
{
    private static string Model(int version, string extra = "", string history = "audit") => $$"""
        schemaVersion: {{version}}
        name: sales
        storage: {connection: mdm_sql}
        {{extra}}
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
            history: {{history}}
          region:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
        """;

    private static DataModelLoadResult Load(string yaml) => DataModelLoader.Load(yaml, "sales.model.yaml");

    [Fact]
    public void Format_1_is_still_read_and_format_2_is_current()
    {
        Assert.Equal(2, DataModelLoader.Version);
        Assert.Equal("model/2", DataModel.FormatId);
        var one = Load(Model(1));
        Assert.True(one.Ok, string.Join("\n", one.Diagnostics));
        Assert.Equal((1, null), (one.Model!.SchemaVersion, one.Model.Cache));
        var two = Load(Model(2));
        Assert.True(two.Ok, string.Join("\n", two.Diagnostics));
        Assert.Equal(2, two.Model!.SchemaVersion);
        Assert.Null(two.Model.Cache);
    }

    [Fact]
    public void A_save_or_a_publish_writes_the_current_format_and_a_version_out_of_range_is_refused_naming_the_file()
    {
        // plan 33 6d: format 1 (the oldest still read) loads, and what a save or a publish writes of it is format 2, the rest as written
        var current = DataModelLoader.WithCurrentVersion("# kept\n" + Model(1));
        Assert.StartsWith("# kept\nschemaVersion: 2\n", current);
        var loaded = Load(current);
        Assert.True(loaded.Ok, string.Join("\n", loaded.Diagnostics));
        Assert.Equal(2, loaded.Model!.SchemaVersion);
        Assert.Equal(current, DataModelLoader.WithCurrentVersion(current));
        Assert.StartsWith("schemaVersion: 2\n", DataModelLoader.WithCurrentVersion(Model(1).Replace("schemaVersion: 1", "schemaVersion: \"1\"")));

        foreach (var outside in new[] { 0, 3 })
        {
            var refused = Load(Model(outside));
            Assert.False(refused.Ok);
            Assert.Contains(refused.Diagnostics, d => d.Path == "sales.model.yaml" && d.Message.Contains("schemaVersion", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_format_2_file_with_each_key_of_the_cache_section_loads()
    {
        var result = Load(Model(2, "cache: {enabled: true, ttl: 10m, scope: rules, dataset: region}\nchecks:\n  - {id: region_fresh, kind: freshness, dataset: region, max_age: 6h, severity: block}"));
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        Assert.Equal(new CacheSection(true, TimeSpan.FromMinutes(10), CacheScope.Rules, "region"), result.Model!.Cache);
        var check = Assert.Single(result.Model.Checks!);
        Assert.Equal(("region_fresh", CheckKind.Freshness, CheckSeverity.Block), (check.Id, check.Kind, check.Severity));
        Assert.Equal(TimeSpan.FromHours(6), ((FreshnessParameters)check.Parameters).MaxAge);
    }

    [Fact]
    public void A_format_1_file_with_a_section_of_format_2_is_refused_naming_the_file_and_the_line()
    {
        var result = Load(Model(1, "cache: {enabled: true}\nchecks: []"));
        Assert.False(result.Ok);
        Assert.All(result.Diagnostics, d => Assert.Equal(DataModelLoader.CacheNotInThisFormat, d.Code));
        Assert.Equal([("sales.model.yaml", 4), ("sales.model.yaml", 5)], result.Diagnostics.Select(d => (d.Path, d.Line)));
        Assert.Contains("section of format 2 (model/2)", result.Diagnostics[0].Message);
        Assert.Contains("schemaVersion: 2", result.Diagnostics[0].Fix);
    }

    [Theory]
    [InlineData("cache: {enabled: maybe}", "`cache.enabled` is `true` or `false`")]
    [InlineData("cache: {ttl: 2d}", "the unit is")]
    [InlineData("cache: {ttl: 48h}", "at most 24 hours")]
    [InlineData("cache: {scope: all}", "`cache.scope` is `all`")]
    [InlineData("cache: {enabled: true, dataset: nothing}", "not an entity of this model")]
    [InlineData("cache: {unknown: 1}", "Unknown key `unknown`")]
    [InlineData("checks: yes", "`checks` is a list")]
    public void Each_bad_value_is_refused_with_file_and_line(string extra, string says)
    {
        var result = Load(Model(2, extra));
        Assert.False(result.Ok);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("sales.model.yaml", 4), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
    }

    [Theory]
    [InlineData("checks:\n  - {id: a, kind: sql, dataset: region}", 5, "is not a kind of check")]
    [InlineData("checks:\n  - {id: a, kind: range, dataset: region, column: code, min: 5, max: 1}", 5, "is above `max`")]
    [InlineData("checks:\n  - {id: a, kind: referential, dataset: region, column: code}", 5, "names no target")]
    [InlineData("checks:\n  - {id: a, kind: not_null, dataset: region, column: code, severity: high}", 5, "`severity` is `high`")]
    public void Each_bad_check_is_refused_with_file_and_line(string extra, int line, string says)
    {
        var result = Load(Model(2, extra));
        Assert.False(result.Ok);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("sales.model.yaml", line), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
    }

    [Fact]
    public void A_dataset_that_cannot_be_cached_refuses_the_cache()
    {
        // an entity with effective-dated history is read while writes of other dates are in flight
        var whole = Load(Model(2, "cache: {enabled: true}", history: "effective_dated"));
        var error = Assert.Single(whole.Diagnostics);
        Assert.Equal(DataModelLoader.CannotBeCached, error.Code);
        Assert.Contains("`territory`", error.Message);
        Assert.Contains("effective-dated", error.Message);

        // another entity of the model can be cached, and the cache can be left off
        Assert.True(Load(Model(2, "cache: {enabled: true, dataset: region}", history: "effective_dated")).Ok);
        Assert.True(Load(Model(2, "cache: {enabled: false}", history: "effective_dated")).Ok);
        Assert.False(Load(Model(2, "cache: {enabled: true, dataset: territory}", history: "effective_dated")).Ok);
    }

    [Fact]
    public void A_program_that_reads_only_format_1_refuses_a_format_2_file_by_its_version()
    {
        // what the previous release does with the file: its newest version is 1 (modelled by a file that says 3 here, which this one does not read either)
        var future = Load(Model(3));
        var error = Assert.Single(future.Diagnostics);
        Assert.Equal(SharedCodes.SchemaVersionNewer, error.Code);
        Assert.Equal(("sales.model.yaml", 1), (error.Path, error.Line));
        Assert.Contains("up to 2", error.Message);
    }
}
