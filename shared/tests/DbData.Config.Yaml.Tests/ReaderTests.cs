using DbData.Config.Yaml;

namespace DbData.Config.Yaml.Tests;

public class ReaderTests
{
    private static (YamlNode? Root, List<ConfigDiagnostic> Diags) Read(string text)
    {
        var d = new List<ConfigDiagnostic>();
        return (StrictYamlReader.Read(text, "x.yml", d), d);
    }

    [Fact]
    public void Scalars_stay_strings_with_positions()
    {
        var (root, diags) = Read("a: 1\nb: true\nc: \"q\"\nd: [x, y]\n");
        Assert.Empty(diags);
        var m = Assert.IsType<YamlMapping>(root);
        Assert.Equal("1", ((YamlScalar)m.Get("a")!).Value);
        Assert.Equal("true", ((YamlScalar)m.Get("b")!).Value);
        Assert.True(((YamlScalar)m.Get("c")!).Quoted);
        Assert.Equal(2, ((YamlScalar)m.Get("b")!).Line);
        Assert.True(((YamlSequence)m.Get("d")!).Flow);
    }

    [Fact]
    public void Empty_file_is_null_without_diagnostics()
    {
        var (root, diags) = Read("");
        Assert.Null(root);
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("a: 1\na: 2\n", "DBD-102")]
    [InlineData("a: &x 1\nb: *x\n", "DBD-103")]
    [InlineData("a: !!str 1\n", "DBD-103")]
    [InlineData("base: &b {x: 1}\nc:\n  <<: *b\n", "DBD-103")]
    [InlineData("a: 1\n---\nb: 2\n", "DBD-101")]
    [InlineData("a: [1, 2\n", "DBD-101")]
    public void Unsafe_or_broken_input_gives_a_coded_diagnostic(string text, string code)
    {
        var (_, diags) = Read(text);
        Assert.Contains(diags, d => d.Code == code && d.Severity == ConfigSeverity.Error && d.Path == "x.yml" && d.Line >= 1);
    }

    [Fact]
    public void Duplicate_key_reports_position_of_second()
    {
        var (_, diags) = Read("a: 1\nb: 2\na: 3\n");
        var d = Assert.Single(diags);
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Product_catalog_maps_the_prefix()
    {
        var d = new List<ConfigDiagnostic>();
        StrictYamlReader.Read("a: 1\na: 2\n", "p.yml", d, new PrefixedCatalog("DDB-"));
        Assert.Equal("DDB-102", Assert.Single(d).Code);
        Assert.NotNull(d[0].Fix);
    }

    [Fact]
    public void Every_prefix_of_a_document_is_read_without_an_exception()
    {
        const string doc = "a:\n  - b: [1, 2]\n    c: {d: e, f: \"g\"}\nh: |\n  text\n  more\ni: 'q'\n";
        for (var n = 0; n <= doc.Length; n++) Read(doc[..n]);
    }

    [Fact]
    public void Did_you_mean_suggests_close_keys()
    {
        Assert.Equal("timeout", DidYouMean.Closest("timeuot", ["timeout", "host"]));
        Assert.Null(DidYouMean.Closest("zzzzzz", ["timeout", "host"]));
    }
}
