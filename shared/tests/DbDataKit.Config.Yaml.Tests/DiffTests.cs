using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Yaml.Tests;

public class DiffTests
{
    [Fact]
    public void Equal_texts_give_empty_diff()
    {
        Assert.Equal("", UnifiedDiff.Create("a\n", "a\n", "f"));
    }

    [Fact]
    public void Changed_line_is_shown_with_context()
    {
        var d = UnifiedDiff.Create("a\nb\nc\n", "a\nB\nc\n", "f.yml");
        Assert.Contains("--- a/f.yml", d);
        Assert.Contains("+++ b/f.yml", d);
        Assert.Contains("-b", d);
        Assert.Contains("+B", d);
    }
}
