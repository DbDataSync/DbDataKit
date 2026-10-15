using DbData.Config.Yaml;

namespace DbData.Config.Yaml.Tests;

public class JsonInputTests
{
    private const string Json = "{\n\t\"Listen\": { \"Address\": \"0.0.0.0\", \"Port\": 6000 },\n\t\"Tags\": [\"a\", \"b/c\", \"\\u00e9\\n\"],\n\t\"Verbose\": true,\n\t\"Idle\": null\n}\n";

    [Fact]
    public void Json_builds_the_same_node_tree_with_positions()
    {
        var d = new List<ConfigDiagnostic>();
        var root = Assert.IsType<YamlMapping>(JsonInput.Read(Json, "appsettings.json", d));
        Assert.Empty(d);
        var listen = Assert.IsType<YamlMapping>(root.Get("Listen"));
        Assert.Equal("6000", ((YamlScalar)listen.Get("Port")!).Value);
        Assert.Equal(2, listen.Line);
        var tags = Assert.IsType<YamlSequence>(root.Get("Tags"));
        Assert.Equal("é\n", ((YamlScalar)tags.Items[2]).Value);
        Assert.Equal("true", ((YamlScalar)root.Get("Verbose")!).Value);
    }

    [Theory]
    [InlineData("{\"a\": 1,, }")]
    [InlineData("{\"a\": 1 /* open }")]
    [InlineData("{\"a\": ")]
    [InlineData("{'a': 1}")]
    [InlineData("not json")]
    public void Invalid_json_is_a_syntax_diagnostic_with_a_position_never_an_exception(string text)
    {
        var d = new List<ConfigDiagnostic>();
        Assert.Null(JsonInput.Read(text, "a.json", d));
        var diag = Assert.Single(d);
        Assert.Equal(SharedCodes.YamlSyntax, diag.Code);
        Assert.True(diag.Line >= 1);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_and_positions_kept()
    {
        var text = "{\n  // line\n  \"a\": \"x // not a comment, \", /* block\n c */\n  \"b\": [1, 2,],\n}\n";
        var d = new List<ConfigDiagnostic>();
        var root = Assert.IsType<YamlMapping>(JsonInput.Read(text, "a.json", d));
        Assert.Empty(d);
        Assert.Equal("x // not a comment, ", ((YamlScalar)root.Get("a")!).Value);
        Assert.Equal(2, ((YamlSequence)root.Get("b")!).Items.Count);
        Assert.Equal(5, root.Get("b")!.Line);
        Assert.Null(JsonInput.Read("// only a comment\n", "a.json", d));
        Assert.Empty(d);
    }

    [Fact]
    public void Duplicate_keys_stay_errors_even_with_comments()
    {
        var d = new List<ConfigDiagnostic>();
        JsonInput.Read("{\"a\": 1, // c\n \"a\": 2,}", "a.json", d);
        Assert.Contains(d, x => x.Code == SharedCodes.DuplicateKey);
    }

    [Fact]
    public void Duplicate_keys_are_errors_like_in_yaml()
    {
        var d = new List<ConfigDiagnostic>();
        JsonInput.Read("{\"a\": 1, \"a\": 2}", "a.json", d);
        Assert.Contains(d, x => x.Code == SharedCodes.DuplicateKey);
    }

    [Fact]
    public void Extension_selects_the_format_and_empty_is_null()
    {
        var d = new List<ConfigDiagnostic>();
        Assert.NotNull(JsonInput.ReadByExtension("a: 1\n", "x.yml", d));
        Assert.NotNull(JsonInput.ReadByExtension("{\"a\": 1}", "x.JSON", d));
        Assert.Empty(d);
        Assert.Null(JsonInput.ReadByExtension("  ", "x.json", d));
        Assert.Empty(d);
    }
}
