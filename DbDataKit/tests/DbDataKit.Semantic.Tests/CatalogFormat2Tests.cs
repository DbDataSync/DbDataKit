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
        Assert.Equal(2, CatalogLoader.LoadText(With("checks:\n  - {kind: freshness}"), "sales.catalog.yaml").Format);
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
        Assert.Matches(new System.Text.RegularExpressions.Regex(cache["ttl"]!["pattern"]!.GetValue<string>()), "24h");
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(cache["ttl"]!["pattern"]!.GetValue<string>()), "0s");
    }
}
