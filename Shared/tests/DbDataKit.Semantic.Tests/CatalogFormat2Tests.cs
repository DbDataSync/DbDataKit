using DbDataKit.Config.Yaml;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Plan 24a UC03: format 2 of the semantic package adds the optional sections <c>cache</c> and <c>checks</c> to the catalog. A catalog that has neither is format 1 and is published as before; each key is valid, each bad
/// value is refused naming the file and line, and the schema agrees with the loader.
/// </summary>
public sealed class CatalogFormat2Tests
{
    private static string Sample => File.ReadAllText(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml"));

    private static string With(string section) => Sample.TrimEnd() + "\n" + section.TrimEnd() + "\n";

    [Fact]
    public void A_catalog_with_neither_section_is_format_1_and_has_no_cache()
    {
        var catalog = CatalogLoader.LoadText(Sample, "sales.catalog.yaml");
        Assert.Equal(1, catalog.Format);
        Assert.Null(catalog.Cache);
    }

    [Fact]
    public void Each_key_of_the_cache_section_is_valid_and_the_catalog_is_format_2()
    {
        var catalog = CatalogLoader.LoadText(With("cache:\n  enabled: true\n  ttl: 15m\n  scope: rules\n  dataset: " + FirstTable()), "sales.catalog.yaml");
        Assert.Equal(2, catalog.Format);
        Assert.Equal(new CacheSection(true, TimeSpan.FromMinutes(15), CacheScope.Rules, FirstTable()), catalog.Cache);
        Assert.Equal(2, CatalogLoader.LoadText(With("checks:\n  - {id: fresh, kind: freshness, dataset: fact_sales, column: ordered_at, max_age: 1d}"), "sales.catalog.yaml").Format);
        Assert.Equal(CacheSection.Off.Ttl, CatalogLoader.LoadText(With("cache: {enabled: false}"), "sales.catalog.yaml").Cache!.Ttl);
    }

    private static string FirstTable() => CatalogLoader.LoadText(Sample).Source["tables"]![0]!["name"]!.GetValue<string>();

    [Theory]
    [InlineData("cache: {enabled: 3}", "$/cache/enabled")]
    [InlineData("cache: {ttl: 2d}", "$/cache/ttl")]
    [InlineData("cache: {ttl: 48h}", "$.cache.ttl")]
    [InlineData("cache: {scope: all}", "$/cache/scope")]
    [InlineData("cache: {dataset: nothing_here}", "$.cache.dataset")]
    [InlineData("cache: {extra: 1}", "$/cache/extra")]
    [InlineData("checks: yes", "$/checks")]
    public void Each_bad_value_is_refused_naming_the_file_and_the_line(string section, string path)
    {
        var text = With(section);
        var line = text.TrimEnd().Split('\n').Length;
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(text, "sales.catalog.yaml"));
        var found = error.Errors.FirstOrDefault(e => e.Path == path);
        Assert.True(found is not null, "no error at " + path + ": " + string.Join(" | ", error.Errors.Select(e => e.Path + " " + e.Message)));
        Assert.StartsWith($"sales.catalog.yaml:{line}:", found!.Message, StringComparison.Ordinal);
    }

    private static string Checks(string entries) => With("checks:\n" + entries);

    [Fact]
    public void Each_kind_of_check_loads_with_its_parameters_and_is_about_a_table_and_its_columns()
    {
        var catalog = CatalogLoader.LoadText(Checks("""
              - {id: a, kind: not_null, dataset: fact_sales, column: order_id, severity: block}
              - {id: b, kind: unique, dataset: fact_sales, columns: [order_id]}
              - {id: c, kind: accepted_values, dataset: fact_sales, column: status, values: [open, shipped]}
              - {id: d, kind: range, dataset: fact_sales, column: net_amount, min: 0}
              - {id: e, kind: referential, dataset: fact_sales, column: customer_key, references: {dataset: 'sqlserver://sql1/sales.dbo.dim_customer', column: customer_key}}
              - {id: f, kind: freshness, dataset: fact_sales, max_age: 6h, on_error: block}
              - {id: g, kind: row_count_change, dataset: fact_sales, max_decrease_percent: 25}
            """), "sales.catalog.yaml");
        Assert.Equal(2, catalog.Format);
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], catalog.Checks.Select(c => c.Id));
        Assert.Equal(Enum.GetValues<DbDataKit.Quality.CheckKind>(), catalog.Checks.Select(c => c.Kind));
        Assert.Equal(DbDataKit.Quality.CheckSeverity.Block, catalog.Checks[0].Severity);
        Assert.Equal(DbDataKit.Quality.CheckOnError.Block, catalog.Checks[5].OnError);
        Assert.Empty(CatalogLoader.LoadText(Sample, "sales.catalog.yaml").Checks);
    }

    [Theory]
    [InlineData("  - {id: a, kind: sql, dataset: fact_sales}", "is not a kind of check")]
    [InlineData("  - {id: a, kind: range, dataset: fact_sales, column: net_amount, min: 5, max: 1}", "is above `max`")]
    [InlineData("  - {id: a, kind: referential, dataset: fact_sales, column: order_id}", "names no target")]
    [InlineData("  - {id: a, kind: not_null, column: order_id}", "names no dataset")]
    [InlineData("  - {id: a, kind: not_null, dataset: fact_sales, column: order_id, severity: high}", "`severity` is `high`")]
    [InlineData("  - {id: a, kind: freshness, dataset: fact_sales, max_age: 6w}", "`s`, `m`, `h` or `d`")]
    public void Each_bad_check_is_refused_naming_the_file_and_the_line(string entry, string says)
    {
        var text = Checks(entry);
        var line = text.TrimEnd().Split('\n').Length;
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(text, "sales.catalog.yaml"));
        Assert.Contains(error.Errors, e => e.Message.StartsWith($"sales.catalog.yaml:{line}:", StringComparison.Ordinal) && e.Message.Contains(says, StringComparison.Ordinal));
    }

    [Fact]
    public void A_check_about_a_table_or_a_column_the_catalog_does_not_have_is_refused_with_the_line()
    {
        var table = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(Checks("  - {id: a, kind: not_null, dataset: nothing_here, column: order_id}"), "sales.catalog.yaml"));
        var found = Assert.Single(table.Errors, e => e.Code == "unknown_check_dataset");
        Assert.Contains("isn't a table of the catalog", found.Message, StringComparison.Ordinal);
        Assert.Contains("sales.catalog.yaml:", found.Message, StringComparison.Ordinal);
        var column = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(Checks("  - {id: a, kind: not_null, dataset: fact_sales, column: nope}"), "sales.catalog.yaml"));
        Assert.Contains("doesn't have", Assert.Single(column.Errors, e => e.Code == "unknown_check_column").Message, StringComparison.Ordinal);
        // a lineage identity names a dataset outside the catalog: nothing to compare with
        Assert.Single(CatalogLoader.LoadText(Checks("  - {id: a, kind: not_null, dataset: 'sqlserver://sql1/db.dbo.other', column: whatever}"), "sales.catalog.yaml").Checks);
    }

    [Fact]
    public void A_catalog_read_as_json_has_its_checks_checked_too()
    {
        var json = CatalogLoader.YamlToJson(Checks("  - {id: a, kind: not_null, dataset: fact_sales, column: order_id}"));
        Assert.Single(CatalogLoader.LoadJson(json).Checks);
        var bad = CatalogLoader.YamlToJson(Checks("  - {id: a, kind: range, dataset: fact_sales, column: net_amount, min: 5, max: 1}"));
        Assert.Contains(Assert.Throws<CatalogException>(() => CatalogLoader.LoadJson(bad)).Errors, e => e.Code == "invalid_check" && e.Message.Contains("is above `max`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_time_to_live_over_24_hours_and_a_dataset_that_is_not_a_table_say_what_is_wrong()
    {
        var long_ = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(With("cache: {ttl: 48h}"), "sales.catalog.yaml"));
        Assert.Contains(long_.Errors, e => e.Code == "invalid_cache" && e.Message.Contains("at most 24 hours", StringComparison.Ordinal));
        var unknown = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(With("cache: {dataset: nothing_here}"), "sales.catalog.yaml"));
        Assert.Contains(unknown.Errors, e => e.Code == "unknown_cache_dataset" && e.Message.Contains("isn't a table of the catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_agrees_with_the_loader()
    {
        var assembly = typeof(CatalogLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".catalog.schema.json", StringComparison.Ordinal)))!;
        var properties = System.Text.Json.Nodes.JsonNode.Parse(stream)!["properties"]!.AsObject();
        var cache = properties["cache"]!["properties"]!.AsObject();
        Assert.Equal(CacheSection.Keys.Order(StringComparer.Ordinal), cache.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(CacheSection.ScopeNames, cache["scope"]!["enum"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.True(properties.ContainsKey("checks"));
        Assert.EndsWith("check.schema.json#/$defs/check", properties["checks"]!["items"]!["$ref"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(System.Text.Json.Nodes.JsonNode.Parse(DbDataKit.Quality.CheckSchema.Text())!["$id"]!.GetValue<string>() + "#/$defs/check", properties["checks"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.Matches(new System.Text.RegularExpressions.Regex(cache["ttl"]!["pattern"]!.GetValue<string>()), "24h");
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(cache["ttl"]!["pattern"]!.GetValue<string>()), "0s");
    }
}
