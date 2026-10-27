using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Server;
using Json.Schema;

namespace DbDataBuild.Tests.Unit;

/// <summary>The <c>lineage</c> noun on the Build server's command line: one graph of every hosted project, narrowed by <c>--project</c> (plan 18, G05).</summary>
public class ServerLineageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddb-lineage-" + Guid.NewGuid().ToString("N"));
    private string ServerDir => Path.Combine(root, "server");

    public ServerLineageTests() => Directory.CreateDirectory(ServerDir);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private string Starter(string name)
    {
        var dir = Path.Combine(root, name);
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        return dir;
    }

    /// <summary>A second project that maps the table the starter's `marts.orders` builds, and builds a report from it: the two projects meet in that table.</summary>
    private string Reports()
    {
        var dir = Path.Combine(root, "reports");
        Directory.CreateDirectory(Path.Combine(dir, "models", "marts"));
        Directory.CreateDirectory(Path.Combine(dir, "models", "reports"));
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        File.WriteAllText(Path.Combine(dir, "models", "marts", "orders.yml"), "name: marts.orders\nkind: mapped\ngrain: [order_id]\ncolumns:\n  - {name: order_id, type: INTEGER, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n");
        File.WriteAllText(Path.Combine(dir, "models", "reports", "big_orders.yml"), "name: reports.big_orders\nkind: full\ncolumns:\n  - {name: order_id, type: INTEGER, nullable: false}\n");
        File.WriteAllText(Path.Combine(dir, "models", "reports", "big_orders.sql"), "SELECT order_id FROM marts.orders WHERE amount > 100\n");
        return dir;
    }

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await ServerCli.RunAsync([.. args, "--repo", ServerDir], o, e, TextReader.Null);
        return (exit, o.ToString(), e.ToString());
    }

    private async Task Host(string id, string path) => Assert.Equal(0, (await Run("server", "project", "add", id, "--path", path)).Exit);

    private async Task<JsonNode> Graph(params string[] args)
    {
        var result = await Run(["lineage", "graph", .. args, "--format", "json"]);
        Assert.True(result.Exit == 0, result.Out + result.Err);
        var document = JsonNode.Parse(result.Out)!;
        Assert.True(JsonSchema.FromText(ServerCli.OutputSchema(["lineage"])).Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, result.Out);
        return document["data"]!;
    }

    private static HashSet<string> NodeIds(JsonNode data) => data["nodes"]!.AsArray().Select(n => (string)n!["id"]!).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task A_server_with_no_project_has_an_empty_graph_and_no_error()
    {
        var data = await Graph();
        Assert.Empty(data["nodes"]!.AsArray());
        Assert.Empty(data["edges"]!.AsArray());
    }

    [Fact]
    public async Task The_graph_joins_every_hosted_project_and_the_same_table_is_one_node()
    {
        await Host("shop", Starter("shop"));
        await Host("reports", Reports());
        var shop = NodeIds(await Graph("--project", "shop"));
        var reports = NodeIds(await Graph("--project", "reports"));
        var all = await Graph();

        var shared = shop.Intersect(reports).ToList();
        Assert.Contains(shared, id => id.EndsWith("marts.orders", StringComparison.Ordinal));      // built by the one, mapped by the other
        Assert.Equal(shop.Union(reports).Count(), NodeIds(all).Count);
        Assert.True(NodeIds(all).Count < shop.Count + reports.Count);
        // the flow from the shop's table into the report is in the combined graph and not in the shop's own
        Assert.Contains(all["edges"]!.AsArray(), e => ((string)e!["from"]!).EndsWith("marts.orders", StringComparison.Ordinal) && ((string)e["to"]!).EndsWith("reports.big_orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Impact_follows_a_table_across_projects()
    {
        await Host("shop", Starter("shop"));
        await Host("reports", Reports());
        var all = await Graph();
        var orders = NodeIds(all).First(id => id.EndsWith("marts.orders", StringComparison.Ordinal));
        var impact = await Run("lineage", "impact", orders, "--format", "json");
        Assert.Equal(0, impact.Exit);
        var data = JsonNode.Parse(impact.Out)!["data"]!;
        Assert.Contains(data["reached"]!.AsArray(), r => ((string)r!["id"]!).EndsWith("reports.big_orders", StringComparison.Ordinal));
        Assert.True(JsonSchema.FromText(ServerCli.OutputSchema(["lineage"])).Evaluate(JsonNode.Parse(impact.Out), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid);
        Assert.Equal(0, (await Run("lineage", "provenance", orders)).Exit);
    }

    [Fact]
    public async Task An_unknown_project_is_refused_naming_the_hosted_ones()
    {
        await Host("shop", Starter("shop"));
        var refused = await Run("lineage", "graph", "--project", "nope");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("No project 'nope' is hosted", refused.Err);
        Assert.Contains("shop", refused.Err);
        var empty = Path.Combine(root, "empty");
        var (o, e) = (new StringWriter(), new StringWriter());
        Assert.Equal(1, await ServerCli.RunAsync(["lineage", "graph", "--project", "x", "--repo", empty], o, e, TextReader.Null));
        Assert.Contains("No project is hosted", e.ToString());
    }

    [Fact]
    public async Task A_project_with_no_files_is_left_out_with_a_note_and_the_others_are_kept()
    {
        var shop = Starter("shop");
        await Host("shop", shop);
        await Host("reports", Reports());
        File.Delete(Path.Combine(shop, "dbdatabuild.yml"));      // a folder that lost its project (a remote that was never cloned is the same)
        var result = await Run("lineage", "graph");
        Assert.Equal(0, result.Exit);
        Assert.Contains("Project 'shop' is left out", result.Err);
        Assert.DoesNotContain("marts.customers", result.Out);
        Assert.Contains("reports.big_orders", result.Out.Replace("->", " "));
    }
}
