using DbDataKit.Lineage;

namespace DbDataKit.Lineage.Tests;

/// <summary>Plan 22a GP23: the description and owner of a node (the names, the rule that a missing one is null and never an empty string).</summary>
public sealed class LineageDescriptionTests
{
    [Fact]
    public void A_description_and_an_owner_are_written_cleaned_and_a_blank_one_adds_no_key()
    {
        var map = LineageDescriptions.With(new Dictionary<string, string> { ["connection"] = "wh" }, "  Daily sales,\n  net of returns.  ", "finance");
        Assert.Equal("Daily sales, net of returns.", map["description"]);
        Assert.Equal("finance", map["owner"]);
        Assert.Equal("wh", map["connection"]);

        var blank = LineageDescriptions.With(null, "   ", "");
        Assert.Empty(blank);                                                   // no key: not an empty string
        Assert.DoesNotContain("description", LineageDescriptions.With(new Dictionary<string, string>(), null).Keys);
    }

    [Fact]
    public void A_long_description_is_cut_at_a_word_with_an_ellipsis()
    {
        var map = LineageDescriptions.With(null, string.Join(' ', Enumerable.Repeat("word", 400)));
        var text = map["description"];
        Assert.True(text.Length <= LineageDescriptions.MaxLength);
        Assert.EndsWith("word…", text);
    }

    [Fact]
    public void A_node_without_a_description_says_so_and_the_graph_lists_what_still_needs_one()
    {
        var described = new LineageNode("a", NodeKind.Dataset, "orders", false, LineageDescriptions.With(null, "Every order", "sales"));
        var bare = new LineageNode("b", NodeKind.Dataset, "payments");
        var empty = new LineageNode("c", NodeKind.Column, "id", false, new Dictionary<string, string> { ["description"] = "" });   // an emitter that wrote an empty string is still "none"
        Assert.Equal(("Every order", "sales"), (described.Description, described.Owner));
        Assert.Null(bare.Description);
        Assert.Null(empty.Description);
        Assert.Null(bare.Owner);

        var graph = new LineageGraph([described, bare, empty], []);
        Assert.Equal(["b", "c"], graph.Undescribed().Select(n => n.Id).Order());
        Assert.Equal(["b"], graph.Undescribed(NodeKind.Dataset).Select(n => n.Id));
    }
}
