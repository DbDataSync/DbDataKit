using System.Text.Json;
using DbDataKit.Cli;
using Json.Schema;

namespace DbDataKit.Lineage.Cli.Tests;

public sealed class LineageNounTests
{
    // source -> raw -> model -> report, and a side branch whose own feed is unknown
    private static LineageGraph Graph() => new(
        [
            new LineageNode("src", NodeKind.Dataset, "src"), new LineageNode("raw", NodeKind.Dataset, "raw"),
            new LineageNode("model", NodeKind.Dataset, "model"), new LineageNode("report", NodeKind.Consumer, "report"),
            new LineageNode("orphan", NodeKind.Dataset, "orphan", UnknownUpstream: true),
        ],
        [
            new LineageEdge("src", "raw", EdgeKind.Replicates, Granularity.Table, Confidence.Declared),
            new LineageEdge("raw", "model", EdgeKind.Derives, Granularity.Table, Confidence.Derived, "aggregate"),
            new LineageEdge("model", "report", EdgeKind.Reads, Granularity.Table, Confidence.Observed),
            new LineageEdge("orphan", "model", EdgeKind.Derives, Granularity.Table, Confidence.Declared),
        ]);

    private static NounRouter Router() => new("demo", [LineageNoun.Create(_ => Task.FromResult(Graph()))]);

    private static async Task<(int Exit, JsonElement Root, string Out, string Err)> Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await Router().RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        return (exit, JsonDocument.Parse(output.ToString()).RootElement, output.ToString(), error.ToString());
    }

    private static void AssertValid(JsonElement root) =>
        Assert.True(JsonSchema.FromText(Router().OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, root.ToString());

    private static string[] Ids(JsonElement reached) => [.. reached.EnumerateArray().Select(e => e.GetProperty("id").GetString()!)];

    [Fact]
    public async Task Graph_lists_every_node_and_edge_and_the_document_validates()
    {
        var (exit, root, _, _) = await Run("lineage", "graph");

        Assert.Equal(0, exit);
        AssertValid(root);
        var data = root.GetProperty("data");
        Assert.Equal(5, data.GetProperty("nodes").GetArrayLength());
        Assert.Equal(4, data.GetProperty("edges").GetArrayLength());
        Assert.Equal(Graph().Fingerprint(), data.GetProperty("fingerprint").GetString());
        Assert.Contains(data.GetProperty("edges").EnumerateArray(), e => e.TryGetProperty("detail", out var d) && d.GetString() == "aggregate");
    }

    [Fact]
    public async Task Graph_can_be_limited_to_one_kind_of_node_and_the_edges_between_them()
    {
        var (exit, root, _, _) = await Run("lineage", "graph", "--kind", "dataset");

        Assert.Equal(0, exit);
        AssertValid(root);
        Assert.DoesNotContain("report", Ids(root.GetProperty("data").GetProperty("nodes")));
        Assert.Equal(3, root.GetProperty("data").GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public async Task Impact_lists_what_a_node_feeds_nearest_first()
    {
        var (exit, root, _, _) = await Run("lineage", "impact", "src");

        Assert.Equal(0, exit);
        AssertValid(root);
        var data = root.GetProperty("data");
        Assert.Equal(["raw", "model", "report"], Ids(data.GetProperty("reached")));
        Assert.Equal([1, 2, 3], data.GetProperty("reached").EnumerateArray().Select(e => e.GetProperty("depth").GetInt32()));
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, data.GetProperty("gaps").GetArrayLength());       // the orphan feeds the model but is not downstream of src
    }

    [Fact]
    public async Task Provenance_lists_what_feeds_a_node_and_reports_the_gap_apart_from_nothing()
    {
        var (exit, root, _, _) = await Run("lineage", "provenance", "report");

        Assert.Equal(0, exit);
        AssertValid(root);
        var data = root.GetProperty("data");
        Assert.Equal(["model", "orphan", "raw", "src"], Ids(data.GetProperty("reached")).Order());
        Assert.Equal(["orphan"], data.GetProperty("gaps").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_depth_that_stops_the_walk_says_so()
    {
        var (_, root, _, _) = await Run("lineage", "impact", "src", "--depth", "1");

        AssertValid(root);
        Assert.Equal(["raw"], Ids(root.GetProperty("data").GetProperty("reached")));
        Assert.True(root.GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_node_is_exit_1_naming_it_and_a_bad_use_is_exit_2()
    {
        var (exit, root, _, _) = await Run("lineage", "impact", "nowhere");
        Assert.Equal(1, exit);
        AssertValid(root);
        Assert.Contains("no node 'nowhere'", root.GetProperty("errors")[0].GetString());

        Assert.Equal(2, (await Run("lineage", "impact")).Exit);
        Assert.Equal(2, (await Run("lineage", "impact", "src", "--depth", "0")).Exit);
        Assert.Equal(2, (await Run("lineage", "graph", "--kind", "planet")).Exit);
        Assert.Equal(2, (await Run("lineage", "graph", "--bogus")).Exit);
    }

    [Fact]
    public async Task The_text_form_prints_the_walk_indented_by_depth()
    {
        var output = new StringWriter();
        var exit = await Router().RunAsync(["lineage", "impact", "src"], new CliContext(output, new StringWriter(), TextReader.Null));

        Assert.Equal(0, exit);
        Assert.Contains("impact of src: 3 nodes", output.ToString());
        Assert.Contains("    model", output.ToString());
    }
}
