using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Yaml.Tests;

public class EditorTests
{
    private const string Doc = """
        # top comment
        name: demo   # trailing
        server:
          # about listen
          listen: { address: 0.0.0.0, port: 5432 }
          users:
            - name: alice   # first
              sources: [erp, crm]
            - name: bob
              sources: [erp]

        # tail comment
        tags: [a, b]
        empty:
        quoted: "old"
        """;

    private static string Ok(EditResult r)
    {
        Assert.True(r.Ok, r.Problem?.ToString());
        return r.Text!;
    }

    [Fact]
    public void Set_replaces_a_scalar_and_keeps_comments_and_everything_else()
    {
        var t = Ok(YamlEditor.Set(Doc, "name", "other"));
        Assert.Equal(Doc.Replace("name: demo   # trailing", "name: other   # trailing"), t);
    }

    [Fact]
    public void Set_replaces_inside_flow_and_nested_paths()
    {
        var t = Ok(YamlEditor.Set(Doc, "server.listen.port", YamlValue.Raw("9000")));
        Assert.Contains("{ address: 0.0.0.0, port: 9000 }", t);
        t = Ok(YamlEditor.Set(Doc, "server.users[1].name", "carol"));
        Assert.Contains("- name: carol", t);
        Assert.Contains("# first", t);
    }

    [Fact]
    public void Set_replaces_a_quoted_scalar_whole()
    {
        var t = Ok(YamlEditor.Set(Doc, "quoted", "new"));
        Assert.Contains("quoted: new", t);
    }

    [Fact]
    public void Set_fills_an_empty_value()
    {
        var t = Ok(YamlEditor.Set(Doc, "empty", "x"));
        Assert.Contains("empty: x\n", t);
    }

    [Fact]
    public void Set_adds_a_missing_key_after_the_last_sibling_in_the_files_indent()
    {
        var t = Ok(YamlEditor.Set(Doc, "server.timeout", YamlValue.Raw("30")));
        var lines = t.Split('\n');
        var i = Array.FindIndex(lines, l => l.Contains("timeout: 30"));
        Assert.Equal("  timeout: 30", lines[i]);
        Assert.Contains("sources: [erp]", lines[i - 1]);
    }

    [Fact]
    public void Set_adds_nested_missing_mappings_and_top_level_keys()
    {
        var t = Ok(YamlEditor.Set(Doc, "server.tls.cert", "a.pem"));
        Assert.Contains("  tls:\n    cert: a.pem", t);
        t = Ok(YamlEditor.Set(Doc, "added", "1"));
        Assert.EndsWith("added: \"1\"", t.TrimEnd());
    }

    [Fact]
    public void Set_adds_into_a_flow_mapping()
    {
        var t = Ok(YamlEditor.Set(Doc, "server.listen.tls", "on"));
        Assert.Contains("{ address: 0.0.0.0, port: 5432, tls: \"on\" }", t);
    }

    [Fact]
    public void Set_turns_an_empty_key_into_a_mapping()
    {
        var t = Ok(YamlEditor.Set(Doc, "empty.a", "b"));
        Assert.Contains("empty:\n  a: b", t);
    }

    [Fact]
    public void Set_into_an_empty_document()
    {
        Assert.Equal("a:\n  b: c\n", Ok(YamlEditor.Set("", "a.b", "c")));
        Assert.Equal("# c\na: 1", Ok(YamlEditor.Set("# c", "a", YamlValue.Raw("1"))).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    [Fact]
    public void Remove_takes_the_entry_lines_only()
    {
        var t = Ok(YamlEditor.Remove(Doc, "server.users[0]"));
        Assert.DoesNotContain("alice", t);
        Assert.DoesNotContain("# first", t);
        Assert.Contains("# about listen", t);
        Assert.Contains("- name: bob", t);
        t = Ok(YamlEditor.Remove(Doc, "server"));
        Assert.DoesNotContain("listen", t);
        Assert.Contains("# tail comment", t);
        Assert.Contains("name: demo   # trailing", t);
    }

    [Fact]
    public void Remove_an_empty_valued_key_does_not_eat_the_next_line()
    {
        var t = Ok(YamlEditor.Remove(Doc, "empty"));
        Assert.DoesNotContain("empty", t);
        Assert.Contains("quoted: \"old\"", t);
    }

    [Fact]
    public void Remove_from_flow_collections()
    {
        Assert.Contains("tags: [b]", Ok(YamlEditor.Remove(Doc, "tags[0]")));
        Assert.Contains("tags: [a]", Ok(YamlEditor.Remove(Doc, "tags[1]")));
        Assert.Contains("{ address: 0.0.0.0 }", Ok(YamlEditor.Remove(Doc, "server.listen.port")));
        Assert.Contains("{ port: 5432 }", Ok(YamlEditor.Remove(Doc, "server.listen.address")));
    }

    [Fact]
    public void Remove_first_key_of_a_list_item_keeps_the_dash()
    {
        var t = Ok(YamlEditor.Remove(Doc, "server.users[1].name"));
        Assert.Contains("- sources: [erp]", t);
    }

    [Fact]
    public void Remove_missing_path_is_a_problem()
    {
        var r = YamlEditor.Remove(Doc, "nope");
        Assert.False(r.Ok);
        Assert.Equal(SharedCodes.NotEditable, r.Problem!.Code);
    }

    [Fact]
    public void Insert_into_flow_and_block_lists()
    {
        Assert.Contains("tags: [a, b, c]", Ok(YamlEditor.Insert(Doc, "tags", "c")));
        Assert.Contains("tags: [z, a, b]", Ok(YamlEditor.Insert(Doc, "tags", "z", 0)));
        var t = Ok(YamlEditor.Insert(Doc, "server.users[0].sources", "x"));
        Assert.Contains("sources: [erp, crm, x]", t);

        const string block = "items:\n  - a   # c1\n  - b\nnext: 1\n";
        Assert.Equal("items:\n  - a   # c1\n  - b\n  - c\nnext: 1\n", Ok(YamlEditor.Insert(block, "items", "c")));
        Assert.Equal("items:\n  - z\n  - a   # c1\n  - b\nnext: 1\n", Ok(YamlEditor.Insert(block, "items", "z", 0)));
    }

    [Fact]
    public void Insert_into_empty_flow_list_and_empty_key()
    {
        Assert.Equal("a: [x]\n", Ok(YamlEditor.Insert("a: []\n", "a", "x")));
        Assert.Equal("a:\n  - x\nb: 1\n", Ok(YamlEditor.Insert("a:\nb: 1\n", "a", "x")));
    }

    [Fact]
    public void Crlf_line_endings_are_preserved()
    {
        var crlf = "a: 1\r\nb:\r\n  c: 2\r\n";
        var t = Ok(YamlEditor.Set(crlf, "b.d", "3"));
        Assert.Equal("a: 1\r\nb:\r\n  c: 2\r\n  d: \"3\"\r\n", t);
    }

    [Fact]
    public void Block_scalars_and_broken_documents_are_refused_not_rewritten()
    {
        Assert.False(YamlEditor.Set("a: |\n  x\n", "a", "y").Ok);
        Assert.False(YamlEditor.Set("a: [1\n", "a", "y").Ok);
        Assert.False(YamlEditor.Set("a: 1\n", "a..b[", "y").Ok);
        Assert.False(YamlEditor.Set("a: 1\n", "a.b", "y").Ok);   // a is a scalar
    }

    [Fact]
    public void Round_trip_untouched_parts_are_byte_identical()
    {
        var t = Ok(YamlEditor.Set(Doc, "name", "x"));
        var back = Ok(YamlEditor.Set(t, "name", "demo"));
        Assert.Equal(Doc, back);
    }
}
