using DbData.Config.Yaml;

namespace DbData.Config.Yaml.Tests;

public class MergeTests
{
    private static YamlLayer L(string file, string text) => new(file, (YamlMapping)StrictYamlReader.Read(text, file, [])!);
    private static readonly IReadOnlySet<string> Keys = new HashSet<string> { "tags", "defaults", "name", "connections" };

    private static (MergedYaml M, List<ConfigDiagnostic> D) Merge(params YamlLayer[] layers)
    {
        var d = new List<ConfigDiagnostic>();
        return (YamlMerge.Merge(layers, Keys, d), d);
    }

    private static string Val(MergedYaml m, string path) => m.Origins().First(o => o.Path == path).Value;

    [Fact]
    public void Maps_merge_nearer_layer_wins_and_origin_is_tracked()
    {
        var (m, d) = Merge(L("base.yml", "defaults:\n  a: 1\n  b: 2\n"), L("proj.yml", "defaults:\n  b: 3\n  c: 4\n"));
        Assert.Empty(d);
        Assert.Equal("1", Val(m, "defaults.a"));
        Assert.Equal("3", Val(m, "defaults.b"));
        var o = m.Origins();
        Assert.Equal("base.yml", o.First(x => x.Path == "defaults.a").File);
        Assert.Equal("proj.yml", o.First(x => x.Path == "defaults.b").File);
        Assert.Equal(3, o.First(x => x.Path == "defaults.c").Line);
    }

    [Fact]
    public void Lists_append_and_repeated_scalars_are_kept_once()
    {
        var (m, _) = Merge(L("a", "tags: [x, y]\n"), L("b", "tags: [y, z]\n"));
        Assert.Equal(["tags[0]", "tags[1]", "tags[2]"], m.Origins().Select(o => o.Path));
        Assert.Equal("z", Val(m, "tags[2]"));
    }

    [Fact]
    public void Equals_suffix_replaces()
    {
        var (m, _) = Merge(L("a", "tags: [x, y]\n"), L("b", "tags=: [z]\n"));
        Assert.Equal(["tags[0]"], m.Origins().Select(o => o.Path));
        Assert.Equal("z", Val(m, "tags[0]"));
    }

    [Fact]
    public void Minus_suffix_removes_listed_items_or_keys()
    {
        var (m, _) = Merge(L("a", "tags: [x, y]\ndefaults: {a: 1, b: 2}\n"), L("b", "tags-: [x]\ndefaults-: [a]\n"));
        Assert.Equal(["tags[0]", "defaults.b"], m.Origins().Select(o => o.Path).OrderByDescending(p => p == "tags[0]"));
        Assert.Equal("y", Val(m, "tags[0]"));
    }

    [Fact]
    public void Plus_suffix_is_merge_and_named_items_merge_by_name()
    {
        var (m, d) = Merge(L("a", "connections:\n  - {name: one, v: 1}\n"), L("b", "connections+:\n  - {name: one, w: 2}\n  - {name: two}\n"));
        Assert.Empty(d);
        var paths = m.Origins().Select(o => o.Path).ToList();
        Assert.Contains("connections[0].v", paths);
        Assert.Contains("connections[0].w", paths);
        Assert.Contains("connections[1].name", paths);
    }

    [Fact]
    public void Shape_mismatch_and_bad_suffix_are_diagnostics()
    {
        var (_, d1) = Merge(L("a", "tags: [x]\n"), L("b", "tags: {k: v}\n"));
        Assert.Single(d1);
        var (_, d2) = Merge(L("a", "name: n\n"), L("b", "name+: m\n"));
        Assert.Single(d2);
        var (_, d3) = Merge(L("a", "tags: [x]\n"), L("b", "tags-: notalist\n"));
        Assert.Single(d3);
    }

    [Fact]
    public void Non_layered_keys_keep_their_suffix_in_the_name()
    {
        var (m, _) = Merge(L("a", "other: 1\n"), L("b", "other=: 2\n"));
        Assert.Contains(m.Root.Entries, e => e.Key.Value == "other=");
    }
}
