namespace DbDataKit.Config.Yaml.Tests;

/// <summary>Plan 24a UC03: the shared <c>cache:</c> and <c>checks:</c> sections of format 2: each key valid, each bad value refused with file and line.</summary>
public sealed class CacheSectionTests
{
    private static (CacheSection? Section, List<ConfigDiagnostic> Diagnostics) Read(string yaml)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = (YamlMapping)StrictYamlReader.Read(yaml, "orders.yaml", diagnostics)!;
        Assert.Empty(diagnostics);
        return (CacheSection.Read(root.Get("cache")!, "orders.yaml", diagnostics), diagnostics);
    }

    [Fact]
    public void Each_key_is_valid()
    {
        var (section, diagnostics) = Read("cache:\n  enabled: true\n  ttl: 90s\n  scope: rules\n  dataset: orders\n");
        Assert.Empty(diagnostics);
        Assert.Equal(new CacheSection(true, TimeSpan.FromSeconds(90), CacheScope.Rules, "orders"), section);
    }

    [Fact]
    public void The_defaults_are_off_five_minutes_per_caller()
    {
        var (section, _) = Read("cache: {}\n");
        Assert.Equal(CacheSection.Off, section);
        Assert.Equal(TimeSpan.FromMinutes(5), section!.Ttl);
        Assert.Equal(CacheScope.Caller, section.Scope);
        Assert.False(section.Enabled);
    }

    [Theory]
    [InlineData("5m", 300)]
    [InlineData("30s", 30)]
    [InlineData("2h", 7200)]
    [InlineData("24h", 86400)]
    [InlineData("1440m", 86400)]
    public void A_time_to_live_is_a_whole_number_and_a_unit_up_to_24_hours(string text, int seconds)
    {
        Assert.True(CacheSection.TryParseTtl(text, out var ttl, out var problem), problem);
        Assert.Equal(TimeSpan.FromSeconds(seconds), ttl);
    }

    [Theory]
    [InlineData("0s", "whole number above zero")]
    [InlineData("05m", "whole number above zero")]
    [InlineData("25h", "at most 24 hours")]
    [InlineData("86401s", "at most 24 hours")]
    [InlineData("5d", "the unit is")]
    [InlineData("m", "whole number above zero")]
    [InlineData("1.5h", "whole number above zero")]
    [InlineData("", "whole number above zero")]
    public void A_bad_time_to_live_says_what_is_wrong(string text, string says)
    {
        Assert.False(CacheSection.TryParseTtl(text, out _, out var problem));
        Assert.Contains(says, problem);
    }

    [Theory]
    [InlineData("cache:\n  enabled: yes\n", 2, "`cache.enabled` is `true` or `false`")]
    [InlineData("cache:\n  enabled: true\n  ttl: 25h\n", 3, "at most 24 hours")]
    [InlineData("cache:\n  scope: everyone\n", 2, "one of `caller`, `rules`, `shared`")]
    [InlineData("cache:\n  datset: orders\n", 2, "Did you mean `dataset`?")]
    [InlineData("cache: true\n", 1, "`cache` is a mapping")]
    public void Each_bad_value_is_refused_with_the_file_and_the_line(string yaml, int line, string says)
    {
        var (section, diagnostics) = Read(yaml);
        Assert.Null(section);
        var error = Assert.Single(diagnostics);
        Assert.Equal(("orders.yaml", line), (error.Path, error.Line));
        Assert.Contains(says, error.Message);
        Assert.Contains($"orders.yaml:{line}:", error.ToString());
    }

    [Fact]
    public void The_place_of_the_checks_is_a_list_of_mappings_whose_keys_are_not_read()
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = (YamlMapping)StrictYamlReader.Read("checks:\n  - {kind: freshness, anything: 1}\n  - kind: not_null\n", "orders.yaml", diagnostics)!;
        Assert.Equal(2, ChecksPlace.Read(root.Get("checks")!, "orders.yaml", diagnostics));
        Assert.Empty(diagnostics);

        var bad = (YamlMapping)StrictYamlReader.Read("checks:\n  - not a mapping\n", "orders.yaml", diagnostics)!;
        Assert.Null(ChecksPlace.Read(bad.Get("checks")!, "orders.yaml", diagnostics));
        Assert.Equal(("orders.yaml", 2), (diagnostics[0].Path, diagnostics[0].Line));
        var scalar = (YamlMapping)StrictYamlReader.Read("checks: none\n", "orders.yaml", [])!;
        Assert.Null(ChecksPlace.Read(scalar.Get("checks")!, "orders.yaml", diagnostics));
    }
}
